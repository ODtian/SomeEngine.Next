using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Importers;

/// <summary>
/// Built-in high-density cluster-local encoding. Its private blob uses variable-width quantized
/// positions and UVs plus packed signed-normalized normal/tangent fields in one bitstream.
/// </summary>
public sealed class StandardClusterVertexEncoder : IClusterVertexEncoder
{
    public const string EncodingCompatibility = "someengine.vertex.standard.cluster.v1";
    public const uint BlobMagic = 0x31564353u; // "SCV1"
    public const int HeaderSize = 64;

    public static StandardClusterVertexEncoder Instance { get; } = new();

    private StandardClusterVertexEncoder()
    {
    }

    public string Compatibility => EncodingCompatibility;

    public EncodedClusterVertices Encode(
        ReadOnlySpan<Vector3> positions,
        IReadOnlyList<RawAttribute> attributes,
        ReadOnlySpan<uint> clusterVertices)
    {
        if (clusterVertices.IsEmpty || clusterVertices.Length > 255)
            throw new ArgumentOutOfRangeException(
                nameof(clusterVertices),
                "Standard cluster vertex blobs require between one and 255 vertices.");

        RawAttribute? normal = Find(attributes, "NORMAL");
        RawAttribute? tangent = Find(attributes, "TANGENT");
        RawAttribute? uv0 = Find(attributes, "TEXCOORD_0");
        ValidateAttributes(attributes, normal, tangent, uv0, positions.Length);

        Vector3 positionMin = new(float.MaxValue);
        Vector3 positionMax = new(float.MinValue);
        Vector2 uvMin = new(float.MaxValue);
        Vector2 uvMax = new(float.MinValue);
        for (int local = 0; local < clusterVertices.Length; local++)
        {
            int vertex = ResolveVertex(clusterVertices[local], positions.Length);
            Vector3 position = positions[vertex];
            RequireFinite(position, $"Position[{vertex}]");
            positionMin = Vector3.Min(positionMin, position);
            positionMax = Vector3.Max(positionMax, position);

            Vector2 uv = ReadVector2(uv0, vertex, Vector2.Zero);
            RequireFinite(uv, $"TEXCOORD_0[{vertex}]");
            uvMin = Vector2.Min(uvMin, uv);
            uvMax = Vector2.Max(uvMax, uv);
        }

        float positionStep = QuantizationStep(positionMax - positionMin);
        float uvStep = QuantizationStep(uvMax - uvMin);
        UInt3 positionMaximum = default;
        UInt2 uvMaximum = default;
        for (int local = 0; local < clusterVertices.Length; local++)
        {
            int vertex = checked((int)clusterVertices[local]);
            UInt3 quantizedPosition = Quantize(positions[vertex], positionMin, positionStep);
            UInt2 quantizedUv = Quantize(ReadVector2(uv0, vertex, Vector2.Zero), uvMin, uvStep);
            positionMaximum = UInt3.Max(positionMaximum, quantizedPosition);
            uvMaximum = UInt2.Max(uvMaximum, quantizedUv);
        }

        int positionBitsX = BitsRequired(positionMaximum.X);
        int positionBitsY = BitsRequired(positionMaximum.Y);
        int positionBitsZ = BitsRequired(positionMaximum.Z);
        int uvBitsX = BitsRequired(uvMaximum.X);
        int uvBitsY = BitsRequired(uvMaximum.Y);
        int recordBits = checked(
            positionBitsX + positionBitsY + positionBitsZ + 24 + 32 + uvBitsX + uvBitsY);
        int dataBits = checked(recordBits * clusterVertices.Length);
        // The shader reads words from each concatenated blob. Padding both aligns
        // the next header and makes the final partial word safe to load.
        int blobSize = checked((HeaderSize + ((dataBits + 7) / 8) + 3) & ~3);
        var blob = new byte[blobSize];
        var header = new StandardVertexBlobHeader
        {
            Magic = BlobMagic,
            BlobSize = checked((uint)blobSize),
            VertexCount = checked((uint)clusterVertices.Length),
            PackedBits = PackBits(
                positionBitsX,
                positionBitsY,
                positionBitsZ,
                uvBitsX,
                uvBitsY),
            RecordBitStride = checked((uint)recordBits),
            DataBitOffset = HeaderSize * 8,
            PositionOrigin = positionMin,
            PositionStep = positionStep,
            UvOrigin = uvMin,
            UvStep = uvStep,
        };
        MemoryMarshal.Write(blob, in header);

        var writer = new BitWriter(blob, HeaderSize * 8);
        Vector3 decodedMin = new(float.MaxValue);
        Vector3 decodedMax = new(float.MinValue);
        for (int local = 0; local < clusterVertices.Length; local++)
        {
            int vertex = checked((int)clusterVertices[local]);
            UInt3 position = Quantize(positions[vertex], positionMin, positionStep);
            UInt2 uv = Quantize(ReadVector2(uv0, vertex, Vector2.Zero), uvMin, uvStep);
            writer.Write(position.X, positionBitsX);
            writer.Write(position.Y, positionBitsY);
            writer.Write(position.Z, positionBitsZ);
            writer.Write(PackSnorm3(ReadVector3(normal, vertex, Vector3.UnitY)), 24);
            writer.Write(PackSnorm4(ReadVector4(tangent, vertex, new Vector4(1, 0, 0, 1))), 32);
            writer.Write(uv.X, uvBitsX);
            writer.Write(uv.Y, uvBitsY);

            Vector3 decoded = positionMin + new Vector3(position.X, position.Y, position.Z) * positionStep;
            decodedMin = Vector3.Min(decodedMin, decoded);
            decodedMax = Vector3.Max(decodedMax, decoded);
        }

        return new EncodedClusterVertices(blob, decodedMin, decodedMax);
    }

    private static RawAttribute? Find(IReadOnlyList<RawAttribute> attributes, string name)
    {
        RawAttribute? result = null;
        for (int index = 0; index < attributes.Count; index++)
        {
            RawAttribute attribute = attributes[index];
            if (!string.Equals(attribute.Name, name, StringComparison.Ordinal))
                continue;
            if (result is not null)
                throw new InvalidDataException($"Standard vertex input contains duplicate '{name}' attributes.");
            result = attribute;
        }
        return result;
    }

    private static void ValidateAttributes(
        IReadOnlyList<RawAttribute> attributes,
        RawAttribute? normal,
        RawAttribute? tangent,
        RawAttribute? uv0,
        int vertexCount)
    {
        for (int index = 0; index < attributes.Count; index++)
        {
            RawAttribute attribute = attributes[index];
            if (!ReferenceEquals(attribute, normal) &&
                !ReferenceEquals(attribute, tangent) &&
                !ReferenceEquals(attribute, uv0))
            {
                throw new InvalidDataException(
                    $"Standard vertex encoding does not consume '{attribute.Name}'. " +
                    "Select a vertex encoder whose compatibility includes that attribute.");
            }
            int required = checked(vertexCount * attribute.Dimension);
            if (attribute.Dimension <= 0 || attribute.Data.Length < required)
            {
                throw new InvalidDataException(
                    $"Vertex attribute '{attribute.Name}' does not contain {vertexCount} complete values.");
            }
        }
        ValidateDimension(normal, 3);
        ValidateDimension(tangent, 4);
        ValidateDimension(uv0, 2);
    }

    private static void ValidateDimension(RawAttribute? attribute, int minimum)
    {
        if (attribute is not null && attribute.Dimension < minimum)
        {
            throw new InvalidDataException(
                $"Vertex attribute '{attribute.Name}' requires at least {minimum} components.");
        }
    }

    private static int ResolveVertex(uint vertex, int vertexCount)
        => vertex < vertexCount
            ? checked((int)vertex)
            : throw new InvalidDataException(
                $"Cluster vertex {vertex} exceeds the {vertexCount}-vertex source mesh.");

    private static Vector2 ReadVector2(RawAttribute? attribute, int vertex, Vector2 fallback)
    {
        if (attribute is null)
            return fallback;
        int offset = checked(vertex * attribute.Dimension);
        return new Vector2(attribute.Data[offset], attribute.Data[offset + 1]);
    }

    private static Vector3 ReadVector3(RawAttribute? attribute, int vertex, Vector3 fallback)
    {
        if (attribute is null)
            return fallback;
        int offset = checked(vertex * attribute.Dimension);
        Vector3 result = new(
            attribute.Data[offset],
            attribute.Data[offset + 1],
            attribute.Data[offset + 2]);
        RequireFinite(result, $"{attribute.Name}[{vertex}]");
        return result;
    }

    private static Vector4 ReadVector4(RawAttribute? attribute, int vertex, Vector4 fallback)
    {
        if (attribute is null)
            return fallback;
        int offset = checked(vertex * attribute.Dimension);
        Vector4 result = new(
            attribute.Data[offset],
            attribute.Data[offset + 1],
            attribute.Data[offset + 2],
            attribute.Data[offset + 3]);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z) || !float.IsFinite(result.W))
        {
            throw new InvalidDataException($"{attribute.Name}[{vertex}] is not finite.");
        }
        return result;
    }

    private static float QuantizationStep(Vector3 extent)
        => QuantizationStep(MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)));

    private static float QuantizationStep(Vector2 extent)
        => QuantizationStep(MathF.Max(extent.X, extent.Y));

    private static float QuantizationStep(float extent)
    {
        if (!float.IsFinite(extent) || extent < 0)
            throw new InvalidDataException("Vertex encoding extent is invalid.");
        if (extent == 0)
            return 1.0f;
        float value = MathF.Pow(2.0f, MathF.Ceiling(MathF.Log2(extent / 65535.0f)));
        return MathF.Max(value, 1e-12f);
    }

    private static UInt3 Quantize(Vector3 value, Vector3 origin, float step) => new(
        Quantize(value.X, origin.X, step),
        Quantize(value.Y, origin.Y, step),
        Quantize(value.Z, origin.Z, step));

    private static UInt2 Quantize(Vector2 value, Vector2 origin, float step) => new(
        Quantize(value.X, origin.X, step),
        Quantize(value.Y, origin.Y, step));

    private static uint Quantize(float value, float origin, float step)
    {
        float encoded = MathF.Round((value - origin) / step);
        if (!float.IsFinite(encoded) || encoded < 0 || encoded > 65535)
            throw new InvalidDataException($"Vertex component {value} exceeds Standard quantization range.");
        return checked((uint)encoded);
    }

    private static int BitsRequired(uint value)
        => value == 0 ? 0 : 32 - BitOperations.LeadingZeroCount(value);

    private static uint PackBits(int x, int y, int z, int u, int v)
    {
        if ((uint)x > 31 || (uint)y > 31 || (uint)z > 31 || (uint)u > 31 || (uint)v > 31)
            throw new InvalidDataException("Standard vertex component bit width exceeds its blob header.");
        return (uint)x | ((uint)y << 5) | ((uint)z << 10) | ((uint)u << 15) | ((uint)v << 20);
    }

    private static uint PackSnorm3(Vector3 value)
        => EncodeSnorm8(value.X) |
            (EncodeSnorm8(value.Y) << 8) |
            (EncodeSnorm8(value.Z) << 16);

    private static uint PackSnorm4(Vector4 value)
        => PackSnorm3(new Vector3(value.X, value.Y, value.Z)) |
            (EncodeSnorm8(value.W) << 24);

    private static uint EncodeSnorm8(float value)
        => unchecked((byte)(sbyte)Math.Clamp((int)MathF.Round(Math.Clamp(value, -1, 1) * 127), -127, 127));

    private static void RequireFinite(Vector3 value, string subject)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new InvalidDataException($"{subject} is not finite.");
    }

    private static void RequireFinite(Vector2 value, string subject)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new InvalidDataException($"{subject} is not finite.");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = HeaderSize)]
    private struct StandardVertexBlobHeader
    {
        public uint Magic;
        public uint BlobSize;
        public uint VertexCount;
        public uint PackedBits;
        public uint RecordBitStride;
        public uint DataBitOffset;
        public Vector3 PositionOrigin;
        public float PositionStep;
        public Vector2 UvOrigin;
        public float UvStep;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
    }

    private readonly record struct UInt2(uint X, uint Y)
    {
        internal static UInt2 Max(UInt2 left, UInt2 right) => new(
            Math.Max(left.X, right.X),
            Math.Max(left.Y, right.Y));
    }

    private readonly record struct UInt3(uint X, uint Y, uint Z)
    {
        internal static UInt3 Max(UInt3 left, UInt3 right) => new(
            Math.Max(left.X, right.X),
            Math.Max(left.Y, right.Y),
            Math.Max(left.Z, right.Z));
    }

    private ref struct BitWriter
    {
        private readonly Span<byte> _destination;
        private int _bit;

        internal BitWriter(Span<byte> destination, int firstBit)
        {
            _destination = destination;
            _bit = firstBit;
        }

        internal void Write(uint value, int bits)
        {
            if (bits == 0)
                return;
            if (bits is < 0 or > 32 || (bits < 32 && value >= (1u << bits)))
                throw new InvalidDataException("A Standard vertex value does not fit its bit width.");
            for (int bit = 0; bit < bits; bit++, _bit++)
            {
                if ((value & (1u << bit)) != 0)
                    _destination[_bit >> 3] |= checked((byte)(1u << (_bit & 7)));
            }
        }
    }
}
