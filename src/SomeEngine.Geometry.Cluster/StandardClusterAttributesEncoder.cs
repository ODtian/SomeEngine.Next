using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SomeEngine.Geometry.Cluster;

public static class StandardClusterAttributesEncoder
{
    private const int StandardHeaderSize = 128; // 16-byte header, three 28-byte descriptors, padded names.

    internal static int GetStandardByteCount(int vertexCount) => StandardHeaderSize + 12 + vertexCount * 12;

    // The vertex encoder supplies a fresh, zero-initialized suffix of its final blob.
    internal static void EncodeStandard(ReadOnlySpan<StandardClusterAttributes> attributes,
        ReadOnlySpan<uint> vertices, Span<byte> data)
    {
        int normalOffset = StandardHeaderSize;
        int tangentOffset = normalOffset + vertices.Length * 4;
        int texCoordOffset = tangentOffset + vertices.Length * 4;
        BinaryPrimitives.WriteUInt32LittleEndian(data, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data[12..], 3);
        WriteDescriptor(data, 0, "NORMAL"u8, 3, normalOffset, uint.MaxValue, 100, 1);
        WriteDescriptor(data, 1, "TANGENT"u8, 4, tangentOffset, uint.MaxValue, 108, 2);
        WriteDescriptor(data, 2, "TEXCOORD_0"u8, 2, texCoordOffset, uint.MaxValue, 116, 3);

        System.Numerics.Vector2 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        foreach (uint vertex in vertices)
        {
            System.Numerics.Vector2 source = attributes[(int)vertex].TexCoord;
            var value = new System.Numerics.Vector2(SourceFloat(source.X), SourceFloat(source.Y));
            minimum = System.Numerics.Vector2.Min(minimum, value);
            maximum = System.Numerics.Vector2.Max(maximum, value);
        }
        System.Numerics.Vector2 extent = maximum - minimum;
        float range = MathF.Max(extent.X, extent.Y);
        float step = range == 0 ? 1 : MathF.Max(MathF.Pow(2, MathF.Ceiling(MathF.Log2(range / 65535))), 1e-12f);
        BinaryPrimitives.WriteSingleLittleEndian(data[texCoordOffset..], minimum.X);
        BinaryPrimitives.WriteSingleLittleEndian(data[(texCoordOffset + 4)..], minimum.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data[(texCoordOffset + 8)..], step);
        for (int vertex = 0; vertex < vertices.Length; vertex++)
        {
            StandardClusterAttributes value = attributes[(int)vertices[vertex]];
            data[normalOffset + vertex * 4] = EncodeSnorm8(value.Normal.X);
            data[normalOffset + vertex * 4 + 1] = EncodeSnorm8(value.Normal.Y);
            data[normalOffset + vertex * 4 + 2] = EncodeSnorm8(value.Normal.Z);
            data[tangentOffset + vertex * 4] = EncodeSnorm8(value.Tangent.X);
            data[tangentOffset + vertex * 4 + 1] = EncodeSnorm8(value.Tangent.Y);
            data[tangentOffset + vertex * 4 + 2] = EncodeSnorm8(value.Tangent.Z);
            data[tangentOffset + vertex * 4 + 3] = EncodeSnorm8(value.Tangent.W);
            BinaryPrimitives.WriteUInt16LittleEndian(data[(texCoordOffset + 12 + vertex * 4)..],
                (ushort)MathF.Round((value.TexCoord.X - minimum.X) / step));
            BinaryPrimitives.WriteUInt16LittleEndian(data[(texCoordOffset + 14 + vertex * 4)..],
                (ushort)MathF.Round((value.TexCoord.Y - minimum.Y) / step));
        }
    }

    // The generic float -> double -> float source path quiets signaling NaNs.
    private static float SourceFloat(float value) => float.IsNaN(value)
        ? BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(value) | 0x00400000)
        : value;

    private static byte EncodeSnorm8(float value)
        => unchecked((byte)(sbyte)MathF.Round(Math.Clamp(value, -1, 1) * 127));

    private static void WriteDescriptor(Span<byte> data, int index, ReadOnlySpan<byte> name,
        int componentCount, int valueOffset, uint componentCountsOffset, int nameOffset, uint format)
    {
        Span<byte> descriptor = data.Slice(16 + index * 28, 28);
        uint hash = 2166136261;
        foreach (byte value in name) hash = unchecked((hash ^ value) * 16777619);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, hash);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], (uint)componentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], (uint)valueOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], componentCountsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], (uint)nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], (uint)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], format);
        name.CopyTo(data[nameOffset..]);
    }

    /// <summary>Creates the default material's opaque bytes while retaining every source value.</summary>
    public static byte[] Encode(IEnumerable<ClusterVertexAttributeData> attributes)
        => Encode(attributes, EncodeValues);

    public static byte[] EncodeSourceValues(IEnumerable<ClusterVertexAttributeData> attributes)
        => Encode(attributes, static stream => (stream.Data.ToArray(), 0u));

    private static byte[] Encode(IEnumerable<ClusterVertexAttributeData> attributes,
        Func<ClusterVertexAttributeData, (byte[] Bytes, uint Format)> encodeValues)
    {
        ClusterVertexAttributeData[] streams = attributes.ToArray();
        byte[][] values = new byte[streams.Length][];
        uint[] formats = new uint[streams.Length];
        bool[] variableCounts = streams.Select(stream => stream.VertexComponentCounts.Any(count => count != stream.ComponentCount)).ToArray();
        int headerSize = 16 + streams.Length * 28;
        for (int index = 0; index < streams.Length; index++)
        {
            ClusterVertexAttributeData stream = streams[index];
            (values[index], formats[index]) = encodeValues(stream);
            headerSize += (variableCounts[index] ? stream.VertexComponentCounts.Length * 4 : 0)
                + ((System.Text.Encoding.UTF8.GetByteCount(stream.Name) + 3) & ~3);
        }
        byte[] data = new byte[headerSize + values.Sum(value => value.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)streams.Length);
        int extraOffset = 16 + streams.Length * 28, valueOffset = headerSize;
        for (int index = 0; index < streams.Length; index++)
        {
            ClusterVertexAttributeData stream = streams[index];
            int standard = stream.Name switch { "NORMAL" => 4, "TANGENT" => 8, "TEXCOORD_0" => 12, _ => 0 };
            if (standard != 0) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(standard), (uint)(index + 1));
            byte[] name = System.Text.Encoding.UTF8.GetBytes(stream.Name);
            uint componentCountsOffset = variableCounts[index] ? (uint)extraOffset : uint.MaxValue;
            if (variableCounts[index])
                foreach (int count in stream.VertexComponentCounts)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(extraOffset), (uint)count); extraOffset += 4;
                }
            WriteDescriptor(data, index, name, stream.ComponentCount, valueOffset,
                componentCountsOffset, extraOffset, formats[index]);
            extraOffset += (name.Length + 3) & ~3;
            values[index].CopyTo(data, valueOffset); valueOffset += values[index].Length;
        }
        return data;
    }

    private static (byte[] Bytes, uint Format) EncodeValues(ClusterVertexAttributeData stream)
    {
        if ((stream.Name == "NORMAL" && stream.ComponentCount == 3) || (stream.Name == "TANGENT" && stream.ComponentCount == 4))
        {
            byte[] data = new byte[stream.VertexCount * 4];
            for (int vertex = 0; vertex < stream.VertexCount; vertex++)
                for (int component = 0; component < stream.ComponentCount; component++)
                    data[vertex * 4 + component] = EncodeSnorm8((float)stream.ReadComponent(vertex, component));
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
