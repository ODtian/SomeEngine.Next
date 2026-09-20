using System.Buffers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class AssetBlockSourceTextTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "someengine-block-source-" + Guid.NewGuid().ToString("N"));

    public AssetBlockSourceTextTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ChangedTexturePixelsCannotSaveOverThePreviousSourceWithAnOldExternalFile()
    {
        using TextureAsset asset = CreateTexture(8, 8);
        TextureBlock pixels = asset.GetBlock<TextureBlock>();
        Dictionary<ulong, string> files = await WritePixelsAsync(pixels);
        string path = Path.Combine(_directory, "paint.texture");
        await SaveAsync(path, asset, files);
        byte[] previous = await File.ReadAllBytesAsync(path);
        ulong key = Assert.Single(pixels.MipTiles).ChunkKey;
        pixels.Chunks.Write(key, 7, new byte[] { 219 });
        asset.Name = "changed";

        await Assert.ThrowsAsync<InvalidDataException>(async () => await SaveAsync(path, asset, files));

        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { "paint.texture", "pixels-0.bin" }, Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task MatchingEditedTexturePixelsSaveAndReadThroughTheirTypedBlock()
    {
        using TextureAsset asset = CreateTexture(8, 8);
        TextureBlock pixels = asset.GetBlock<TextureBlock>();
        Dictionary<ulong, string> files = await WritePixelsAsync(pixels);
        string path = Path.Combine(_directory, "paint.texture");
        await SaveAsync(path, asset, files);
        ulong key = Assert.Single(pixels.MipTiles).ChunkKey;
        pixels.Chunks.Write(key, 7, new byte[] { 219 });
        await WritePixelsAsync(pixels);
        asset.Name = "edited";

        await SaveAsync(path, asset, files);

        using AssetText.SourceReader source = await AssetText.OpenAsync(path);
        source.RequireAsset<TextureAsset>(TextureAsset.FileType);
        Assert.Equal(asset.Id, source.Description.AssetGuid);
        Assert.Equal("edited", source.Description.Name);
        using var restored = new TextureBlock();
        await source.ReadBlockIntoAsync(restored);
        Assert.Equal(pixels.MipTiles.ToArray(), restored.MipTiles.ToArray());
        using PageRange<byte> expected = pixels.Chunks.Retain(key);
        using PageRange<byte> actual = restored.Chunks.Retain(key);
        Assert.Equal(expected.ToArray(), actual.ToArray());
        using SomeEngine.Resources.Texture ready = await restored.CreateAsync(source.Description.Name);
        Assert.Equal(8u, ready.Width);
        Assert.True(ready.HasBootstrap);
    }

    [Fact]
    public async Task CancellationAfterEncodingKeepsThePreviousSourceAndCleansTheTemporaryFile()
    {
        using TextureAsset asset = CreateTexture(8, 8);
        Dictionary<ulong, string> files = await WritePixelsAsync(asset.GetBlock<TextureBlock>());
        string path = Path.Combine(_directory, "paint.texture");
        await SaveAsync(path, asset, files);
        byte[] previous = await File.ReadAllBytesAsync(path);
        asset.Name = "cancelled";
        using var cancellation = new CancellationTokenSource();
        bool encoded = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await AssetText.SaveAsync(path, asset, (writer, copy) =>
            {
                writer.WriteBlock(copy.GetBlock<TextureBlock>(), files);
                encoded = true;
                cancellation.Cancel();
            }, cancellation.Token));

        Assert.True(encoded);
        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { "paint.texture", "pixels-0.bin" }, Directory.GetFiles(_directory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task LazyTexturePayloadIsComparedWithoutLoadingItIntoTheDecodedPageBudget()
    {
        using TextureAsset original = CreateTexture(1024, 1024);
        Dictionary<ulong, string> files = await WritePixelsAsync(original.GetBlock<TextureBlock>());
        string binaryPath = Path.Combine(_directory, "large.texture.asset");
        await original.SaveAsync(binaryPath);
        using var pages = new PageArena(new ResidencyBudgetLedger(new() { DecodedCpuBytes = 128 * 1024 }));
        using TextureAsset asset = await TextureAsset.OpenAsync(binaryPath, pages);
        TextureBlock block = asset.GetBlock<TextureBlock>();
        Assert.Empty(block.Chunks.Keys);
        string sourcePath = Path.Combine(_directory, "large.texture");

        await SaveAsync(sourcePath, asset, files).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(block.Chunks.Keys);
        Assert.Equal(asset.Id, AssetText.Describe(await File.ReadAllBytesAsync(sourcePath)).AssetGuid);
        byte[] previous = await File.ReadAllBytesAsync(sourcePath);
        string pixelsPath = Path.Combine(_directory, files.Values.Single());
        await using (var pixels = new FileStream(pixelsPath, FileMode.Open, FileAccess.Write, FileShare.None))
            await pixels.WriteAsync(new byte[] { 255 });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SaveAsync(sourcePath, asset, files).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(previous, await File.ReadAllBytesAsync(sourcePath));
        Assert.Empty(block.Chunks.Keys);
    }

    [Fact]
    public async Task AllExternalPixelsAreBudgetedTogetherWithPagesAlreadyInUseBeforeReadingAnyPixels()
    {
        using TextureAsset asset = CreateTexture(256, 256, mipCount: 2);
        Dictionary<ulong, string> files = await WritePixelsAsync(asset.GetBlock<TextureBlock>());
        string path = Path.Combine(_directory, "mips.texture");
        await SaveAsync(path, asset, files);
        using AssetText.SourceReader source = await AssetText.OpenAsync(path);
        using var pages = new PageArena(new ResidencyBudgetLedger(new() { DecodedCpuBytes = 96 * 1024 }));
        using var alreadyUsed = new PageBuffer(pages, 32 * 1024);
        using var target = new TextureBlock(pages);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.ReadBlockIntoAsync(target).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Empty(target.Chunks.Keys);
        target.Dispose();
        alreadyUsed.Dispose();
        Assert.Equal(0, pages.Statistics.ActiveBytes);
    }

    [Fact]
    public async Task ExternalPixelsAutomaticallyReclaimOtherAssetsUnusedCachedPages()
    {
        string binaryPath = Path.Combine(_directory, "cached.texture.asset");
        using (TextureAsset original = CreateTexture(256, 256)) await original.SaveAsync(binaryPath);
        using TextureAsset next = CreateTexture(256, 256, mipCount: 2);
        Dictionary<ulong, string> files = await WritePixelsAsync(next.GetBlock<TextureBlock>());
        string sourcePath = Path.Combine(_directory, "incoming.texture");
        await SaveAsync(sourcePath, next, files);
        using var pages = new PageArena(new ResidencyBudgetLedger(new() { DecodedCpuBytes = 96 * 1024 }));
        using TextureAsset cached = await TextureAsset.OpenAsync(binaryPath, pages);
        TextureBlock old = cached.GetBlock<TextureBlock>();
        using (PageBuffer read = await old.AcquireBufferAsync(old.MipTiles[0].ChunkKey))
            Assert.Equal(65536, read.Length);
        Assert.True(pages.Residency.Used(ResidencyClass.DecodedCpu) >= 65536);
        using AssetText.SourceReader source = await AssetText.OpenAsync(sourcePath);
        using var target = new TextureBlock(pages);

        await source.ReadBlockIntoAsync(target).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, target.Chunks.Keys.Count());
        Assert.Equal(65536, target.Chunks.GetLength(target.MipTiles[0].ChunkKey));
        Assert.Equal(16384, target.Chunks.GetLength(target.MipTiles[1].ChunkKey));
        target.Dispose();
        cached.Dispose();
        Assert.Equal(0, pages.Statistics.ActiveBytes);
    }

    [Fact]
    public async Task SynchronousEncodingDoesNotReadExternalFilesButSavingChecksThem()
    {
        using TextureAsset asset = CreateTexture(8, 8);
        ulong key = Assert.Single(asset.GetBlock<TextureBlock>().MipTiles).ChunkKey;
        var files = new Dictionary<ulong, string> { [key] = "not-created.bin" };
        var encoded = new ArrayBufferWriter<byte>();

        AssetText.Write(encoded, asset, (writer, copy) => writer.WriteBlock(copy.GetBlock<TextureBlock>(), files));

        Assert.Equal(asset.Id, AssetText.Describe(encoded.WrittenMemory).AssetGuid);
        string path = Path.Combine(_directory, "missing.texture");
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await SaveAsync(path, asset, files));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private static TextureAsset CreateTexture(uint width, uint height, uint mipCount = 1)
    {
        using var pixels = new TextureBlock
        {
            Width = width, Height = height, Depth = 1, MipLevelCount = mipCount, ArrayLayerCount = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Format = Format.R8UNorm, SampledFormat = Format.R8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        };
        for (uint mip = 0; mip < mipCount; mip++)
        {
            uint mipWidth = Math.Max(1, width >> (int)mip);
            uint mipHeight = Math.Max(1, height >> (int)mip);
            var bytes = new byte[checked((int)(mipWidth * mipHeight))];
            for (int index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index + mip);
            ulong key = TextureTiles.ChunkKey(mip, 0, 0);
            pixels.MipTiles.Add(new TextureMipTile
            {
                MipLevel = mip, Width = mipWidth, Height = mipHeight,
                RowPitch = mipWidth, SlicePitch = (ulong)bytes.Length, DecodedLength = (ulong)bytes.Length, ChunkKey = key,
            });
            pixels.Chunks.Set(key, bytes);
        }
        var asset = new TextureAsset { Name = "paint" };
        try { asset.SetBlock(pixels); return asset; }
        catch { asset.Dispose(); throw; }
    }

    private async Task<Dictionary<ulong, string>> WritePixelsAsync(TextureBlock block)
    {
        var files = new Dictionary<ulong, string>();
        int index = 0;
        foreach (TextureMipTile tile in block.MipTiles)
        {
            string name = $"pixels-{index++}.bin";
            using PageRange<byte> bytes = block.Chunks.Retain(tile.ChunkKey);
            await File.WriteAllBytesAsync(Path.Combine(_directory, name), bytes.ToArray());
            files.Add(tile.ChunkKey, name);
        }
        return files;
    }

    private static ValueTask<AssetEntry> SaveAsync(string path, TextureAsset asset, IReadOnlyDictionary<ulong, string> files)
        => AssetText.SaveAsync(path, asset, (writer, copy) => writer.WriteBlock(copy.GetBlock<TextureBlock>(), files));
}
