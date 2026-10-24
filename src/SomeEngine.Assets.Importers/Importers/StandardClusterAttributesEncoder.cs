using System.Buffers.Binary;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static class StandardClusterAttributesEncoder
{
    /// <summary>Creates the default material's opaque bytes while retaining every source value.</summary>
    internal static ClusterVertexAttributes Encode(IEnumerable<ClusterVertexAttribute> attributes)
        => Encode(attributes, EncodeValues);

    internal static ClusterVertexAttributes EncodeSourceValues(IEnumerable<ClusterVertexAttribute> attributes)
        => Encode(attributes, static stream => (stream.Data.ToArray(), 0u));

    private static ClusterVertexAttributes Encode(IEnumerable<ClusterVertexAttribute> attributes,
        Func<ClusterVertexAttribute, (byte[] Bytes, uint Format)> encodeValues)
    {
        ClusterVertexAttribute[] streams = attributes.ToArray();
        byte[][] values = new byte[streams.Length][];
        uint[] formats = new uint[streams.Length];
        bool[] variableCounts = streams.Select(stream => stream.VertexComponentCounts.Any(count => count != stream.ComponentCount)).ToArray();
        int headerSize = 16 + streams.Length * 28;
        for (int index = 0; index < streams.Length; index++)
        {
            ClusterVertexAttribute stream = streams[index];
            (values[index], formats[index]) = encodeValues(stream);
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
