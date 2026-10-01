using System.Buffers;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using GraphicsViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Resources.Tests;

public sealed class TextureTests
{
    [Fact]
    public void TileKeysKeepTheirPersistedIdentityAcrossNamespaceMoves()
    {
        ulong expected = SomeEngine.Serialization.BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Texture.Mips.1.Layers.2.Faces.0.DepthSlices.0.Tiles.0.0");
        Assert.Equal(expected, TextureTiles.ChunkKey(1, 2, 0, 0, 0, 0));
        Assert.NotEqual(expected, TextureTiles.ChunkKey(1, 2, 0));
        Assert.Equal(TextureTiles.ChunkKey(1, 0, 0, 0, 2, 0), TextureTiles.ChunkKey(1, 2, 0));
    }

    [Fact]
    public void ReadyPixelsSurviveTextureDisposal()
    {
        TextureMipTile fine = Tile(0, 4);
        TextureMipTile coarse = Tile(1, 2);
        var pixels = new List<TexturePixels>();
        pixels.Add(new(coarse, Enumerable.Repeat((byte)7, 16).ToArray()));
        using var texture = Create([fine, coarse], pixels);
        Assert.True(texture.HasBootstrap);
        Assert.False(texture.TryGetTile(fine.ChunkKey, out _));
        Assert.True(texture.TryGetTile(coarse.ChunkKey, out ReadOnlyMemory<byte> read));
                {
            texture.Dispose();
            Assert.Equal(Enumerable.Repeat((byte)7, 16), read.ToArray());
        }
    }

    [Fact]
    public async Task ExplicitPixelsExtendARevisionAndOldReadsSurviveContentReplacement()
    {
        TextureMipTile fine = Tile(0, 4), coarse = Tile(1, 2);
        var initial = new List<TexturePixels>();
        initial.Add(new(coarse, Enumerable.Repeat((byte)7, 16).ToArray()));
        using var texture = Create([fine, coarse], initial);
        ulong revision = texture.Revision;
        var additional = new List<TexturePixels>();
        additional.Add(new(fine, Enumerable.Repeat((byte)9, 64).ToArray()));
        additional.Add(new(coarse, new byte[16]));
        texture.AddPixels(additional, revision);
        Assert.Equal(revision, texture.Revision);
        Assert.True(texture.TryGetTile(fine.ChunkKey, out ReadOnlyMemory<byte> fineRead));
        Assert.Equal(Enumerable.Repeat((byte)9, 64), fineRead.ToArray());
        Assert.True(texture.TryGetTile(coarse.ChunkKey, out ReadOnlyMemory<byte> oldRead));
                {
            Texture replacement = Texture.Create(new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                    Format.R8G8B8A8UNorm, TextureUsages.Sampled),
                [new TexturePixels(Tile(0, 1), new byte[] { 1, 2, 3, 4 })], GraphicsViewDimension.Texture2D);
            await texture.UpdateAsync(replacement);
            Assert.True(replacement.IsDisposed);
            Assert.Equal(revision + 1, texture.Revision);
            Assert.Equal(1u, texture.Width);
            Assert.Equal(Enumerable.Repeat((byte)7, 16), oldRead.ToArray());
            Assert.Throws<InvalidOperationException>(() => texture.AddPixels(additional, revision));
        }
    }

    [Fact]
    public async Task CanceledReplacementClosesOnlyTheReplacement()
    {
        using var original = SinglePixel(3);
        var replacement = SinglePixel(9);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.UpdateAsync(replacement, new(true)).AsTask());
        Assert.True(replacement.IsDisposed);
        Assert.False(original.IsDisposed);
        Assert.Equal(1ul, original.Revision);
        Assert.True(original.TryGetTile(original.MipTiles[0].ChunkKey, out ReadOnlyMemory<byte> pixels));
        Assert.Equal(new byte[] { 3, 3, 3, 3 }, pixels.ToArray());
    }

    [Fact]
    public void InvalidCubeLayerCountsAndSampleCountsRemainRejected()
    {
        Assert.Throws<ArgumentException>(() => Texture.Create(new(TextureDimension.Texture2D,
                1, 1, 1, 1, 7, 1, Format.R8G8B8A8UNorm, TextureUsages.Sampled),
            [new TexturePixels(Tile(0, 1), new byte[4])], GraphicsViewDimension.Cube));
        Assert.Throws<ArgumentException>(() => Texture.Create(new(TextureDimension.Texture2D,
                1, 1, 1, 1, 1, 4, Format.R8G8B8A8UNorm, TextureUsages.Sampled),
            [new TexturePixels(Tile(0, 1), new byte[4])], GraphicsViewDimension.Texture2D));
    }

    private static Texture Create(TextureMipTile[] tiles, IReadOnlyList<TexturePixels> pixels)
        => Texture.Create(new(TextureDimension.Texture2D, 4, 4, 1, 2, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled), tiles, pixels, GraphicsViewDimension.Texture2D);

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(false, 2u)]
    [InlineData(true, 1u)]
    public void CubeFacesArrayLayersAndVolumeSlicesRetainTheirOwnPixels(bool volume, uint cubes)
    {
        uint depth = volume ? 2u : 1u;
        uint faces = volume ? 1u : 6u;
        uint layers = volume ? 1u : cubes * 6;
        GraphicsViewDimension view = volume ? GraphicsViewDimension.Texture3D
            : cubes == 1 ? GraphicsViewDimension.Cube : GraphicsViewDimension.CubeArray;
        var inputs = new List<TexturePixels>();
        for (uint layer = 0; layer < cubes; layer++)
        for (uint face = 0; face < faces; face++)
        for (uint slice = 0; slice < depth; slice++)
            inputs.Add(new(new TextureMipTile
            {
                Width = 2, Height = 2, ArrayLayer = layer, Face = face, DepthSlice = slice,
                RowPitch = 2, SlicePitch = 4,
            }, Enumerable.Repeat(checked((byte)(inputs.Count + 1)), 4).ToArray()));
        using var texture = Texture.Create(new(volume ? TextureDimension.Texture3D : TextureDimension.Texture2D,
            2, 2, depth, 1, layers, 1, Format.R8UNorm, TextureUsages.Sampled), inputs, view);
        Assert.Equal(layers, texture.GetDescription().ArrayLayerCount);
        Assert.Equal(depth, texture.Depth);
        Assert.Equal(view, texture.SampledDimension);
        for (int index = 0; index < inputs.Count; index++)
        {
            TextureMipTile tile = TextureTiles.Describe(inputs[index].Tile, 4);
            Assert.True(texture.TryGetTile(tile.ChunkKey, out ReadOnlyMemory<byte> bytes));
            Assert.Equal(inputs[index].Bytes.ToArray(), bytes.ToArray());
        }
        texture.Dispose();
    }
    private static Texture SinglePixel(byte value)
        => Texture.Create(new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R8G8B8A8UNorm, TextureUsages.Sampled),
            [new TexturePixels(Tile(0, 1), Enumerable.Repeat(value, 4).ToArray())], GraphicsViewDimension.Texture2D);
    private static TextureMipTile Tile(uint mip, uint size) => TextureTiles.Describe(new TextureMipTile
    {
        MipLevel = mip, Width = size, Height = size, RowPitch = size * 4, SlicePitch = size * size * 4,
    }, size * size * 4);
}
