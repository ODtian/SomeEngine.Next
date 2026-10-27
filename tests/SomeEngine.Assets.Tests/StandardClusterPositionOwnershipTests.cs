using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.Geometry.Cluster;
using Xunit;
using StandardClusterPositionEncoder = SomeEngine.Geometry.Cluster.StandardClusterPositionEncoder;

namespace SomeEngine.Assets.Tests;

public sealed class StandardClusterPositionOwnershipTests
{
    [Fact]
    public void SinglePositionRetainsVersionOneBytesAndPadding()
    {
        EncodedClusterPositions result = StandardClusterPositionEncoder.Encode([Vector3.Zero], [0]);
        byte[] expected = Convert.FromHexString("0000000000000000000000000000803F0000000000000000");
        Assert.Equal(expected, result.Data.ToArray());
        Assert.Equal(Vector3.Zero, result.BoundMin);
        Assert.Equal(Vector3.Zero, result.BoundMax);
        Assert.Equal(0, result.RadiusError);
    }

    [Fact]
    public void ReorderedAndRepeatedPositionsRetainVersionOneBytesAndBounds()
    {
        Vector3[] positions = [new(-1, -2, -3), new(1, 2, 3), Vector3.Zero];
        EncodedClusterPositions result = StandardClusterPositionEncoder.Encode(positions, [1, 0, 2, 1]);
        byte[] expected = Convert.FromHexString(
            "000080BF000000C0000040C0000000390040008000C0000000000000002000400060" +
            "0040008000C0");
        Assert.Equal(expected, result.Data.ToArray());
        Assert.Equal(new Vector3(-1, -2, -3), result.BoundMin);
        Assert.Equal(new Vector3(1, 2, 3), result.BoundMax);
        Assert.Equal(BitConverter.SingleToInt32Bits(1f / 8192), BitConverter.SingleToInt32Bits(result.RadiusError));
    }

    [Fact]
    public void PublishedBytesRemainStableAfterInputMutationAndLaterEncoding()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.One];
        uint[] vertices = [0, 1];
        EncodedClusterPositions first = StandardClusterPositionEncoder.Encode(positions, vertices);
        byte[] original = first.Data.ToArray();
        positions[0] = new(-7, -8, -9);
        vertices[1] = 0;
        EncodedClusterPositions second = StandardClusterPositionEncoder.Encode(positions, vertices);
        Assert.Equal(original, first.Data.ToArray());
        Assert.False(first.Data.AsSpan().SequenceEqual(second.Data.AsSpan()));
        Assert.Equal(Vector3.Zero, first.BoundMin);
        Assert.Equal(Vector3.One, first.BoundMax);
    }

    [Fact]
    public void EmptySelectionRetainsHeaderAndSentinelBounds()
    {
        EncodedClusterPositions result = StandardClusterPositionEncoder.Encode([new(float.NaN)], []);
        Assert.False(result.Data.IsDefault);
        Assert.Equal(16, result.Data.Length);
        ReadOnlySpan<byte> bytes = result.Data.AsSpan();
        for (int component = 0; component < 3; component++)
            Assert.Equal(float.MaxValue, BinaryPrimitives.ReadSingleLittleEndian(bytes[(component * 4)..]));
        Assert.True(float.IsNaN(BinaryPrimitives.ReadSingleLittleEndian(bytes[12..])));
        Assert.Equal(new Vector3(float.MaxValue), result.BoundMin);
        Assert.Equal(new Vector3(float.MinValue), result.BoundMax);
        Assert.True(float.IsNaN(result.RadiusError));
    }

    [Fact]
    public void SourceIndexFailuresStillPrecedeQuantizationFailures()
    {
        Vector3[] positions = [new(float.NaN), Vector3.Zero];
        foreach (uint invalid in new[] { 2u, 0x80000000u, uint.MaxValue })
        {
            uint[] vertices = [0, invalid];
            Assert.Throws<IndexOutOfRangeException>(() => StandardClusterPositionEncoder.Encode(positions, vertices));
        }
    }

    [Fact]
    public void SelectedNonfinitePositionsStillThrowOverflow()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Vector3[] positions = [new(value)];
            uint[] vertices = [0];
            Assert.Throws<OverflowException>(() => StandardClusterPositionEncoder.Encode(positions, vertices));
        }
    }
}
