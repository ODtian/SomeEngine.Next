using System.Buffers;
using SomeEngine.Graphics;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using GraphicsViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Resources.Tests;

public sealed class TextureTests
{
    [Fact]
    public async Task ObserverFailureFinishesTheContentUpdateAndNotifiesRemainingObservers()
    {
        using var texture = SinglePixel(3);
        var replacement = SinglePixel(9);
        int notified = 0;
        texture.Changed += _ => throw new InvalidOperationException("observer failure");
        texture.Changed += _ => notified++;
        await Assert.ThrowsAsync<AggregateException>(() =>
            texture.UpdateAsync(replacement).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(texture.IsUpdating);
        Assert.True(replacement.IsDisposed);
        Assert.Equal(1, notified);
        Assert.Equal(2ul, texture.Revision);
        Assert.True(texture.TryRetainTile(texture.MipTiles[0].ChunkKey, out PageRange<byte>? bytes));
        using (bytes) Assert.Equal(new byte[] { 9, 9, 9, 9 }, bytes.Sequence.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedPixelsStayOnTheSameResourceAndNotifyEveryObserver(bool failingObserver)
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        TextureMipTile fine = Tile(0, 4), coarse = Tile(1, 2);
        using var initial = new PageBufferMap(arena);
        initial.Set(coarse.ChunkKey, new byte[16]);
        using var texture = Create([fine, coarse], initial);
        Assert.False(texture.TryRetainTile(fine.ChunkKey, out _));
        ulong revision = texture.Revision;
        int notifications = 0;
        if (failingObserver) texture.Changed += _ => throw new InvalidOperationException("observer failure");
        texture.Changed += _ => notifications++;
        using var additional = new PageBufferMap(arena);
        additional.Set(fine.ChunkKey, Enumerable.Repeat((byte)9, 64).ToArray());

        if (failingObserver)
            Assert.Throws<AggregateException>(() => texture.AddPixels(additional, revision));
        else texture.AddPixels(additional, revision);
        Assert.Equal(1, notifications);
        Assert.Equal(revision, texture.Revision);
        Assert.True(texture.TryRetainTile(fine.ChunkKey, out PageRange<byte>? bytes));
        using (bytes) Assert.Equal(Enumerable.Repeat((byte)9, 64), bytes.Sequence.ToArray());

        texture.AddPixels(additional, revision);
        Assert.Equal(1, notifications);
    }

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
    public void ReadyPixelsSurviveClosedInputsAndStayChargedUntilTheLastReadEnds()
    {
        var budget = new ResidencyBudgetLedger();
        using var arena = new PageArena(budget);
        TextureMipTile fine = Tile(0, 4);
        TextureMipTile coarse = Tile(1, 2);
        using var pixels = new PageBufferMap(arena);
        pixels.Set(coarse.ChunkKey, Enumerable.Repeat((byte)7, 16).ToArray());
        using var texture = Create([fine, coarse], pixels);
        pixels.Dispose();
        arena.Dispose();
        Assert.True(texture.HasBootstrap);
        Assert.False(texture.TryRetainTile(fine.ChunkKey, out _));
        Assert.True(texture.TryRetainTile(coarse.ChunkKey, out PageRange<byte>? read));
        using (read)
        {
            texture.Dispose();
            Assert.Equal(Enumerable.Repeat((byte)7, 16), read.Sequence.ToArray());
            Assert.Equal(256, budget.Used(ResidencyClass.DecodedCpu));
        }
        Assert.Equal(0, budget.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public async Task ExplicitPixelsExtendARevisionAndOldReadsSurviveContentReplacement()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        TextureMipTile fine = Tile(0, 4), coarse = Tile(1, 2);
        using var initial = new PageBufferMap(arena);
        initial.Set(coarse.ChunkKey, Enumerable.Repeat((byte)7, 16).ToArray());
        using var texture = Create([fine, coarse], initial);
        ulong revision = texture.Revision;
        using var additional = new PageBufferMap(arena);
        additional.Set(fine.ChunkKey, Enumerable.Repeat((byte)9, 64).ToArray());
        additional.Set(coarse.ChunkKey, new byte[16]);
        texture.AddPixels(additional, revision);
        Assert.Equal(revision, texture.Revision);
        Assert.True(texture.TryRetainTile(fine.ChunkKey, out PageRange<byte>? fineRead));
        using (fineRead) Assert.Equal(Enumerable.Repeat((byte)9, 64), fineRead.Sequence.ToArray());
        Assert.True(texture.TryRetainTile(coarse.ChunkKey, out PageRange<byte>? oldRead));
        using (oldRead)
        {
            Texture replacement = Texture.Create(new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                    Format.R8G8B8A8UNorm, TextureUsages.Sampled),
                [new TexturePixels(Tile(0, 1), new byte[] { 1, 2, 3, 4 })], GraphicsViewDimension.Texture2D, pages: arena);
            await texture.UpdateAsync(replacement);
            Assert.True(replacement.IsDisposed);
            Assert.Equal(revision + 1, texture.Revision);
            Assert.Equal(1u, texture.Width);
            Assert.Equal(Enumerable.Repeat((byte)7, 16), oldRead.Sequence.ToArray());
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
        Assert.True(original.TryRetainTile(original.MipTiles[0].ChunkKey, out PageRange<byte>? pixels));
        using (pixels) Assert.Equal(new byte[] { 3, 3, 3, 3 }, pixels.Sequence.ToArray());
    }

    [Fact]
    public void InvalidCubeLayerCountsAndSampleCountsRemainRejected()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        Assert.Throws<ArgumentException>(() => Texture.Create(new(TextureDimension.Texture2D,
                1, 1, 1, 1, 7, 1, Format.R8G8B8A8UNorm, TextureUsages.Sampled),
            [new TexturePixels(Tile(0, 1), new byte[4])], GraphicsViewDimension.Cube, pages: arena));
        Assert.Throws<ArgumentException>(() => Texture.Create(new(TextureDimension.Texture2D,
                1, 1, 1, 1, 1, 4, Format.R8G8B8A8UNorm, TextureUsages.Sampled),
            [new TexturePixels(Tile(0, 1), new byte[4])], GraphicsViewDimension.Texture2D, pages: arena));
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    private static Texture Create(TextureMipTile[] tiles, PageBufferMap pixels)
        => Texture.Create(new(TextureDimension.Texture2D, 4, 4, 1, 2, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled), tiles, pixels, GraphicsViewDimension.Texture2D);

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(false, 2u)]
    [InlineData(true, 1u)]
    public void CubeFacesArrayLayersAndVolumeSlicesRetainTheirOwnPixels(bool volume, uint cubes)
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
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
            2, 2, depth, 1, layers, 1, Format.R8UNorm, TextureUsages.Sampled), inputs, view, pages: arena);
        Assert.Equal(layers, texture.GetDescription().ArrayLayerCount);
        Assert.Equal(depth, texture.Depth);
        Assert.Equal(view, texture.SampledDimension);
        for (int index = 0; index < inputs.Count; index++)
        {
            TextureMipTile tile = TextureTiles.Describe(inputs[index].Tile, 4);
            Assert.True(texture.TryRetainTile(tile.ChunkKey, out PageRange<byte>? bytes));
            using (bytes) Assert.Equal(inputs[index].Bytes.ToArray(), bytes.Sequence.ToArray());
        }
        texture.Dispose();
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
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
