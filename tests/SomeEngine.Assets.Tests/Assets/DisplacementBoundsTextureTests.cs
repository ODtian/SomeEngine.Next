using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class DisplacementBoundsTextureTests
{
    [Fact]
    public void CreatesExactConservativeHierarchyForOddDimensions()
    {
        float[] samples = Enumerable.Range(0, 15).Select(static value => (float)value).ToArray();
        TextureData texture = DisplacementBoundsTexture.Create(
            Guid.Empty,
            "displacement bounds",
            width: 5,
            height: 3,
            samples,
            out Vector2 sourceRange);

        Assert.Equal(new Vector2(0, 14), sourceRange);
        Assert.Equal(SomeEngine.Graphics.Format.R32G32Float, texture.Format);
        Assert.Equal(SomeEngine.Graphics.Format.R32G32Float, texture.SampledFormat);
        Assert.Equal(3u, texture.MipLevelCount);

        TextureMipTile[] tiles = Assert.IsAssignableFrom<IList<TextureMipTile>>(texture.MipTiles).ToArray();
        Assert.Collection(
            tiles,
            tile =>
            {
                Assert.Equal((5u, 3u), (tile.Width, tile.Height));
                ReadOnlySpan<Vector2> ranges = Ranges(tile);
                Assert.Equal(new Vector2(0, 0), ranges[0]);
                Assert.Equal(new Vector2(14, 14), ranges[^1]);
            },
            tile =>
            {
                Assert.Equal((2u, 1u), (tile.Width, tile.Height));
                ReadOnlySpan<Vector2> ranges = Ranges(tile);
                Assert.Equal(new Vector2(0, 12), ranges[0]);
                Assert.Equal(new Vector2(2, 14), ranges[1]);
            },
            tile =>
            {
                Assert.Equal((1u, 1u), (tile.Width, tile.Height));
                Assert.Equal(new Vector2(0, 14), Ranges(tile)[0]);
            });
    }

    [Fact]
    public void RejectsNonFiniteSourceSamples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DisplacementBoundsTexture.Create(
                Guid.Empty,
                "invalid displacement bounds",
                2,
                1,
                [0.0f, float.NaN],
                out _));
    }

    private static ReadOnlySpan<Vector2> Ranges(TextureMipTile tile) =>
        MemoryMarshal.Cast<byte, Vector2>(
            Assert.IsType<Memory<byte>>(tile.Payload).Span);
}
