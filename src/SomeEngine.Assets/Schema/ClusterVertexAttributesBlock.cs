using System.Buffers.Binary;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A cluster-local stream encoded as little-endian IEEE 754 doubles in vertex/component order.</summary>
[BinarySerializable]
public sealed partial record ClusterVertexAttribute
{
    public string Name { get; init; } = "";
    public int ComponentCount { get; init; }
    public ImmutableArray<int> VertexComponentCounts { get; init; } = [];
    public ImmutableArray<byte> Data { get; init; } = [];
    [BinaryIgnore] public int VertexCount => Data.Length / (ComponentCount * sizeof(double));

    public double ReadComponent(int vertex, int component)
        => BinaryPrimitives.ReadDoubleLittleEndian(Data.AsSpan().Slice(checked((vertex * ComponentCount + component) * sizeof(double)), sizeof(double)));
}

[BinarySerializable]
public sealed partial record ClusterVertexAttributes
{
    public ImmutableArray<byte> Data { get; init; } = [];
    public ImmutableArray<ClusterVertexAttribute> Attributes { get; init; } = [];

    /// <summary>Creates the default material's opaque bytes while retaining every source value.</summary>
    public static ClusterVertexAttributes Encode(IEnumerable<ClusterVertexAttribute> attributes)
    {
        ClusterVertexAttribute[] streams = attributes.ToArray();
        byte[][] values = new byte[streams.Length][];
        uint[] formats = new uint[streams.Length];
        bool[] variableCounts = streams.Select(stream => stream.VertexComponentCounts.Any(count => count != stream.ComponentCount)).ToArray();
        int headerSize = 16 + streams.Length * 28;
        for (int index = 0; index < streams.Length; index++)
        {
            ClusterVertexAttribute stream = streams[index];
            (values[index], formats[index]) = EncodeValues(stream);
            headerSize += (variableCounts[index] ? stream.VertexComponentCounts.Length * 4 : 0)
                + ((System.Text.Encoding.UTF8.GetByteCount(stream.Name) + 3) & ~3);
        }
        byte[] data = new byte[headerSize + values.Sum(value => value.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)streams.Length);
        int extraOffset = 16 + streams.Length * 28, valueOffset = headerSize;
        for (int index = 0; index < streams.Length; index++)
        {
            ClusterVertexAttribute stream = streams[index];
            int standard = stream.Name switch { "NORMAL" => 4, "TANGENT" => 8, "TEXCOORD_0" => 12, _ => 0 };
            if (standard != 0) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(standard), (uint)(index + 1));
            Span<byte> descriptor = data.AsSpan(16 + index * 28, 28);
            byte[] name = System.Text.Encoding.UTF8.GetBytes(stream.Name);
            uint hash = 2166136261;
            foreach (byte value in name) hash = unchecked((hash ^ value) * 16777619);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, hash);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], (uint)stream.ComponentCount);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], (uint)valueOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], variableCounts[index] ? (uint)extraOffset : uint.MaxValue);
            if (variableCounts[index])
                foreach (int count in stream.VertexComponentCounts)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(extraOffset), (uint)count); extraOffset += 4;
                }
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], (uint)extraOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], (uint)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], formats[index]);
            name.CopyTo(data, extraOffset); extraOffset += (name.Length + 3) & ~3;
            values[index].CopyTo(data, valueOffset); valueOffset += values[index].Length;
        }
        return new() { Data = [.. data], Attributes = [.. streams] };
    }

    private static (byte[] Bytes, uint Format) EncodeValues(ClusterVertexAttribute stream)
    {
        if ((stream.Name == "NORMAL" && stream.ComponentCount == 3) || (stream.Name == "TANGENT" && stream.ComponentCount == 4))
        {
            byte[] data = new byte[stream.VertexCount * 4];
            for (int vertex = 0; vertex < stream.VertexCount; vertex++)
                for (int component = 0; component < stream.ComponentCount; component++)
                    data[vertex * 4 + component] = unchecked((byte)(sbyte)MathF.Round(Math.Clamp((float)stream.ReadComponent(vertex, component), -1, 1) * 127));
            return (data, stream.ComponentCount == 3 ? 1u : 2u);
        }
        if (stream.Name == "TEXCOORD_0" && stream.ComponentCount == 2)
        {
            System.Numerics.Vector2 minimum = new(float.MaxValue), maximum = new(float.MinValue);
            for (int vertex = 0; vertex < stream.VertexCount; vertex++)
            {
                var value = new System.Numerics.Vector2((float)stream.ReadComponent(vertex, 0), (float)stream.ReadComponent(vertex, 1));
                minimum = System.Numerics.Vector2.Min(minimum, value); maximum = System.Numerics.Vector2.Max(maximum, value);
            }
            System.Numerics.Vector2 extent = maximum - minimum;
            float range = MathF.Max(extent.X, extent.Y);
            float step = range == 0 ? 1 : MathF.Max(MathF.Pow(2, MathF.Ceiling(MathF.Log2(range / 65535))), 1e-12f);
            byte[] data = new byte[12 + stream.VertexCount * 4];
            BinaryPrimitives.WriteSingleLittleEndian(data, minimum.X);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), minimum.Y);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), step);
            for (int vertex = 0; vertex < stream.VertexCount; vertex++)
                for (int component = 0; component < 2; component++)
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12 + vertex * 4 + component * 2),
                        (ushort)MathF.Round(((float)stream.ReadComponent(vertex, component) - minimum[component]) / step));
            return (data, 3);
        }
        return (stream.Data.ToArray(), 0);
    }
}

/// <summary>All shading attributes in the geometry block's cluster and vertex order.</summary>
[AssetBlock("SomeEngine.Assets.Schema.ClusterVertexAttributesBlock"), BinarySerializable]
[BinaryFields(nameof(VertexIdentity), nameof(Encoding), nameof(Clusters))]
public sealed partial class ClusterVertexAttributesBlock : AssetBlock
{
    public const string EncodingCompatibility = "someengine.cluster.attributes.standard.v1";
    public partial Guid VertexIdentity { get; set; }
    public partial string Encoding { get; set; }
    public partial IList<ClusterVertexAttributes> Clusters { get; set; }

    partial void Initialize() => Encoding = EncodingCompatibility;
}
