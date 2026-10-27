using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SomeEngine.Geometry.Cluster;
using Xunit;
using StandardClusterVertexEncoder = SomeEngine.Geometry.Cluster.StandardClusterVertexEncoder;
using StandardClusterAttributesEncoder = SomeEngine.Geometry.Cluster.StandardClusterAttributesEncoder;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Assets.Tests;

public sealed class TypedStandardClusterVertexEncoderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(256)]
    public void TypedAndRawPublicEncodersHaveIdenticalBytesAndBounds(int count)
    {
        var positions = new Vector3[count + 5];
        var attributes = new StandardClusterAttributes[count + 7];
        for (int index = 0; index < positions.Length; index++)
            positions[index] = new(index * 0.125f - 6, index % 7 - 3, index % 11 * 0.25f);
        for (int index = 0; index < attributes.Length; index++)
            attributes[index] = new(new(index % 9 * 0.25f - 1, -0.5f, 0.5f),
                new(-1, index % 5 * 0.5f, 0.25f, index % 2 == 0 ? -1 : 1),
                new(index * 0.03125f - 2, index % 13 * 0.0625f));
        uint[] vertices = Enumerable.Range(0, count).Select(index => (uint)((index * 17 + 3) % positions.Length)).ToArray();
        AssertEquivalent(positions, attributes, vertices);
    }

    [Fact]
    public void SelectionOrderAndDuplicatesIgnoreUnselectedInvalidValues()
    {
        Vector3[] positions = [new(float.NaN), new(2, 3, 4), new(6, 5, 4), new(-2, -3, -4)];
        StandardClusterAttributes[] attributes =
        [
            new(new(float.NaN), new(float.NaN), new(float.NaN)),
            new(Vector3.UnitX, new(0, 1, 0, 1), new(0.25f, 0.75f)),
            new(Vector3.UnitY, new(1, 0, 0, -1), new(-0.5f, 0.5f)),
            new(Vector3.UnitZ, new(0, 1, 0, -1), new(1, 2)),
        ];
        AssertEquivalent(positions, attributes, [3, 1, 3, 2, 1]);
        AssertEquivalent(positions, attributes, [2, 2, 2]);
        AssertEquivalent(positions, attributes[..2], [1, 1]);
        AssertEquivalent([], [], []);
    }

    [Fact]
    public void SourceFloatBitPatternsMatchTheRawFloatDoubleFloatPath()
    {
        uint[] bits =
        [
            0x00000000, 0x80000000, 0x00000001, 0x007fffff, 0x00800000,
            0x3f000000, 0xbf000000, 0x3f800000, 0xbf800000, 0x7f7fffff,
            0xff7fffff, 0x7f800000, 0xff800000, 0x7fc00000, 0xffc12345,
            0x7f800001, 0xff800001, 0x7fbfffff,
        ];
        foreach (uint pattern in bits)
        {
            float value = BitConverter.Int32BitsToSingle(unchecked((int)pattern));
            StandardClusterAttributes[] attributes =
            [
                new(new(value), new(value), new(value)),
                new(Vector3.Zero, Vector4.Zero, Vector2.Zero),
            ];
            Vector3[] positions = [Vector3.Zero, Vector3.One];
            AssertEquivalent(positions, attributes, [0]);
            AssertEquivalent(positions, attributes, [0, 1]);
            AssertEquivalent(positions, attributes, [1, 0]);
            attributes[0] = attributes[0] with { TexCoord = new(value, 0.25f) };
            attributes[1] = attributes[1] with { TexCoord = new(0.75f, value) };
            AssertEquivalent(positions, attributes, [0, 1]);
            AssertEquivalent(positions, attributes, [1, 0]);
        }
    }

    [Fact]
    public void TypedSourceIndexFailuresPrecedePositionQuantizationFailures()
    {
        AssertIndexFailure([], [default], [0]);
        AssertIndexFailure([Vector3.Zero], [], [0]);
        AssertIndexFailure([Vector3.Zero], [default], [1]);
        AssertIndexFailure([Vector3.Zero], [default], [0x80000000]);
        AssertIndexFailure([Vector3.Zero], [default], [uint.MaxValue]);
        AssertIndexFailure([new(float.NaN)], [], [0]);
        AssertIndexFailure([new(float.NaN)], [default, default], [0, 1]);
    }

    [Fact]
    public void InvalidPositionQuantizationStillThrowsOverflow()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Vector3[] positions = [new(value)];
            StandardClusterAttributes[] attributes = [default];
            uint[] vertices = [0];
            IClusterVertexEncoder<StandardClusterAttributes> typed = StandardClusterVertexEncoder.Instance;
            Assert.Throws<OverflowException>(() => typed.Encode(positions, attributes, vertices));
            Assert.Throws<OverflowException>(() => EncodeRaw(positions, attributes, vertices));
        }
    }

    [Fact]
    public void CanonicalVertexStillUsesVersionOneGoldenBytes()
    {
        Vector3[] positions = [Vector3.Zero];
        StandardClusterAttributes[] attributes = [new(Vector3.UnitY, new(1, 0, 0, -1), new(0.25f, 0.75f))];
        uint[] vertices = [0];
        EncodedClusterVertices encoded = StandardClusterVertexEncoder.Instance.Encode(positions, attributes, vertices);
        byte[] expected = Convert.FromHexString(
            "0000000000000000000000000000803F00000000000000000300000001000000" +
            "02000000030000009299323C0300000080000000FFFFFFFF6400000006000000" +
            "01000000E24294990400000084000000FFFFFFFF6C0000000700000002000000" +
            "FCB14A440200000088000000FFFFFFFF740000000A000000030000004E4F524D" +
            "414C000054414E47454E5400544558434F4F52445F300000007F00007F000081" +
            "0000803E0000403F0000803F00000000");
        Assert.Equal(expected, encoded.Blob.ToArray());
        Assert.Equal(24, encoded.PositionBytes);
        Assert.Equal(Vector3.Zero, encoded.BoundMin);
        Assert.Equal(Vector3.Zero, encoded.BoundMax);
    }

    [Fact]
    public void RawCustomAttributesKeepTheirDescriptorOrderAndDoublePayload()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.One];
        uint[] vertices = [1, 0, 1];
        string name = "CUSTOM_α";
        RawAttribute[] attributes =
        [
            new("POSITION", [0, 0, 0, 1, 1, 1], 3, GeometryValueType.Float32, 3, false),
            new(name, [0.25f, -0.5f, 1.5f, -2.5f], 2, GeometryValueType.Float32, 2, false),
            new("_MATERIAL_INDEX", [0, 1], 1, GeometryValueType.Float32, 1, false),
        ];
        IClusterVertexEncoder raw = StandardClusterVertexEncoder.Instance;
        EncodedClusterVertices encoded = raw.Encode(positions, attributes, vertices);
        ReadOnlySpan<byte> data = encoded.Blob.Span[encoded.PositionBytes..];
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        ReadOnlySpan<byte> descriptor = data.Slice(16, 28);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[24..]));
        int nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]);
        int nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[20..]);
        Assert.Equal(name, Encoding.UTF8.GetString(data.Slice(nameOffset, nameLength)));
        int valueOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
        double[] expected = [1.5, -2.5, 0.25, -0.5, 1.5, -2.5];
        Assert.Equal(valueOffset + expected.Length * sizeof(double), data.Length);
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], BinaryPrimitives.ReadDoubleLittleEndian(data[(valueOffset + index * sizeof(double))..]));
    }

    [Fact]
    public void CustomVariableComponentCountsAndSourceValuesKeepVersionOneGoldenBytes()
    {
        byte[] values = new byte[4 * sizeof(double)];
        double[] components = [1.25, -2.5, 3.75, -4];
        for (int index = 0; index < components.Length; index++)
            BinaryPrimitives.WriteDoubleLittleEndian(values.AsSpan(index * sizeof(double)), components[index]);
        ClusterVertexAttributeData[] streams = [new("CUSTOM_α", 2, [1, 3], [.. values])];
        byte[] expected = Convert.FromHexString(
            "01000000000000000000000000000000D253543F02000000400000002C000000" +
            "3400000009000000000000000100000003000000435553544F4D5FCEB1000000" +
            "000000000000F43F00000000000004C00000000000000E4000000000000010C0");
        Assert.Equal(expected, StandardClusterAttributesEncoder.Encode(streams));
        Assert.Equal(expected, StandardClusterAttributesEncoder.EncodeSourceValues(streams));
    }

    private static void AssertEquivalent(Vector3[] positions, StandardClusterAttributes[] attributes, uint[] vertices)
    {
        IClusterVertexEncoder<StandardClusterAttributes> typed = StandardClusterVertexEncoder.Instance;
        IClusterVertexEncoder raw = StandardClusterVertexEncoder.Instance;
        Assert.Equal("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1", typed.Compatibility);
        Assert.Equal(raw.Compatibility, typed.Compatibility);
        EncodedClusterVertices expected = EncodeRaw(positions, attributes, vertices);
        EncodedClusterVertices actual = typed.Encode(positions, attributes, vertices);
        Assert.Equal(expected.Blob.ToArray(), actual.Blob.ToArray());
        Assert.Equal(expected.PositionBytes, actual.PositionBytes);
        AssertVectorBits(expected.BoundMin, actual.BoundMin);
        AssertVectorBits(expected.BoundMax, actual.BoundMax);
        EncodedClusterPositions separate = StandardClusterPositionEncoder.Encode(positions, vertices);
        Assert.Equal(separate.Data.Length, actual.PositionBytes);
        Assert.Equal(separate.Data.ToArray(), actual.Blob.Span[..actual.PositionBytes].ToArray());
    }

    private static void AssertVectorBits(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
    }

    private static void AssertIndexFailure(Vector3[] positions, StandardClusterAttributes[] attributes, uint[] vertices)
    {
        IClusterVertexEncoder<StandardClusterAttributes> typed = StandardClusterVertexEncoder.Instance;
        Assert.Throws<IndexOutOfRangeException>(() => typed.Encode(positions, attributes, vertices));
    }

    private static EncodedClusterVertices EncodeRaw(Vector3[] positions, StandardClusterAttributes[] attributes, uint[] vertices)
    {
        var normals = new float[attributes.Length * 3];
        var tangents = new float[attributes.Length * 4];
        var texCoords = new float[attributes.Length * 2];
        for (int index = 0; index < attributes.Length; index++)
        {
            StandardClusterAttributes value = attributes[index];
            normals[index * 3] = value.Normal.X;
            normals[index * 3 + 1] = value.Normal.Y;
            normals[index * 3 + 2] = value.Normal.Z;
            tangents[index * 4] = value.Tangent.X;
            tangents[index * 4 + 1] = value.Tangent.Y;
            tangents[index * 4 + 2] = value.Tangent.Z;
            tangents[index * 4 + 3] = value.Tangent.W;
            texCoords[index * 2] = value.TexCoord.X;
            texCoords[index * 2 + 1] = value.TexCoord.Y;
        }
        RawAttribute[] raw =
        [
            new("NORMAL", normals, 3, GeometryValueType.Float32, 3, false),
            new("TANGENT", tangents, 4, GeometryValueType.Float32, 4, false),
            new("TEXCOORD_0", texCoords, 2, GeometryValueType.Float32, 2, false),
        ];
        return ((IClusterVertexEncoder)StandardClusterVertexEncoder.Instance).Encode(positions, raw, vertices);
    }
}
