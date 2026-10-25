using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class DisplacementBoundsTextureTests
{
    [Fact]
    public async Task DisplacementMipRangesSurviveSaveAndOpen()
    {
        float[] samples = Enumerable.Range(0, 8192).Select(static value => (float)value).ToArray();
        using TextureAsset asset = DisplacementBoundsTexture.Create(Guid.NewGuid(), "displacement mips",
            128, 64, samples, out Vector2 sourceRange);
        Assert.Equal(new Vector2(0, 8191), sourceRange);
        using var directory = new TemporaryDirectory();
        string path = directory.File("displacement.texture.asset");
        await asset.SaveAsync(path);
        using TextureAsset reopened = await TextureAsset.OpenAsync(path);
        TextureBlock pixels = reopened.GetBlock<TextureBlock>();
        Texture texture = await pixels.CreateAsync(firstMip: 0);
        float[] maxima = [0, 129, 387, 903, 1935, 3999, 8127, 8191];
        Assert.Equal((uint)maxima.Length, texture.MipLevelCount);
        foreach (TextureMipTile tile in texture.MipTiles)
        {
            Assert.True(texture.TryGetTile(tile.ChunkKey, out ReadOnlyMemory<byte> bytes));
            Assert.Equal(new Vector2(0, maxima[tile.MipLevel]), MemoryMarshal.Read<Vector2>(bytes.Span));
        }
    }

    [Fact]
    public void CreatesExactConservativeHierarchyForOddDimensions()
    {
        float[] samples = Enumerable.Range(0, 15).Select(static value => (float)value).ToArray();
        using TextureAsset texture = DisplacementBoundsTexture.Create(
            Guid.Empty,
            "displacement bounds",
            width: 5,
            height: 3,
            samples,
            out Vector2 sourceRange);

        Assert.Equal(new Vector2(0, 14), sourceRange);
        Assert.Equal(SomeEngine.Graphics.Format.R32G32Float, texture.GetBlock<TextureBlock>().Format);
        Assert.Equal(SomeEngine.Graphics.Format.R32G32Float, texture.GetBlock<TextureBlock>().SampledFormat);
        Assert.Equal(3u, texture.GetBlock<TextureBlock>().MipLevelCount);

        TextureMipTile[] tiles = Assert.IsAssignableFrom<IList<TextureMipTile>>(texture.GetBlock<TextureBlock>().MipTiles).ToArray();
        Assert.Collection(
            tiles,
            tile =>
            {
                Assert.Equal((5u, 3u), (tile.Width, tile.Height));
                ReadOnlySpan<Vector2> ranges = Ranges(texture, tile);
                Assert.Equal(new Vector2(0, 0), ranges[0]);
                Assert.Equal(new Vector2(14, 14), ranges[^1]);
            },
            tile =>
            {
                Assert.Equal((2u, 1u), (tile.Width, tile.Height));
                ReadOnlySpan<Vector2> ranges = Ranges(texture, tile);
                Assert.Equal(new Vector2(0, 12), ranges[0]);
                Assert.Equal(new Vector2(2, 14), ranges[1]);
            },
            tile =>
            {
                Assert.Equal((1u, 1u), (tile.Width, tile.Height));
                Assert.Equal(new Vector2(0, 14), Ranges(texture, tile)[0]);
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

    private static Vector2[] Ranges(TextureAsset texture, TextureMipTile tile)
    {
        var bytes = texture.GetBlock<TextureBlock>().Chunks[tile.ChunkKey];
        return MemoryMarshal.Cast<byte, Vector2>(bytes.ToArray()).ToArray();
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("SomeEngine-Displacement-");
        public string File(string name) => Path.Combine(_directory.FullName, name);
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
