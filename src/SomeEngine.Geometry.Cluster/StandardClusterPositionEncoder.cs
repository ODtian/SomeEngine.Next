using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;

namespace SomeEngine.Geometry.Cluster;

public readonly record struct EncodedClusterPositions(ImmutableArray<byte> Data,
    Vector3 BoundMin, Vector3 BoundMax, float RadiusError);

public static class StandardClusterPositionEncoder
{
    public static EncodedClusterPositions Encode(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> vertices)
    {
        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        foreach (uint vertex in vertices)
        {
            minimum = Vector3.Min(minimum, positions[(int)vertex]);
            maximum = Vector3.Max(maximum, positions[(int)vertex]);
        }
        Vector3 extent = maximum - minimum;
        float range = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        float step = range == 0 ? 1 : MathF.Max(MathF.Pow(2, MathF.Ceiling(MathF.Log2(range / 65535))), 1e-12f);
        byte[] data = new byte[(16 + vertices.Length * 6 + 3) & ~3];
        BinaryPrimitives.WriteSingleLittleEndian(data, minimum.X);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), minimum.Y);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), minimum.Z);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), step);
        Vector3 decodedMinimum = minimum, decodedMaximum = maximum;
        for (int vertex = 0; vertex < vertices.Length; vertex++)
        {
            Vector3 value = (positions[(int)vertices[vertex]] - minimum) / step;
            ushort x = checked((ushort)MathF.Round(value.X));
            ushort y = checked((ushort)MathF.Round(value.Y));
            ushort z = checked((ushort)MathF.Round(value.Z));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16 + vertex * 6), x);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18 + vertex * 6), y);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20 + vertex * 6), z);
            Vector3 decoded = minimum + new Vector3(x, y, z) * step;
            decodedMinimum = Vector3.Min(decodedMinimum, decoded);
            decodedMaximum = Vector3.Max(decodedMaximum, decoded);
        }
        return new([.. data], decodedMinimum, decodedMaximum, range == 0 ? 0 : step);
    }
}
