using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Job;
using Texture = SomeEngine.Resources.Texture;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Assets.Tests;

public sealed class AssetDataSharingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-AssetDataSharing-" + Guid.NewGuid().ToString("N"));
    public AssetDataSharingTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task CompressedFileReadCompletesWithZeroWorkers()
    {
        AssetEntry entry = await SaveCompressedTextureAsync("zero-worker", 11);
        using TextureAsset asset = await TextureAsset.OpenAsync(entry.Path);
        TextureBlock block = asset.GetBlock<TextureBlock>();
        ulong key = asset.BlockDescriptions.Single().GetStorageKey(block.MipTiles[0].ChunkKey);
        await using AssetFile file = await AssetFile.OpenAsync(entry.Path, TextureAsset.WireType);
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        try
        {
            byte[] pixels = await file.ReadBytesAsync(key).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new byte[] { 11, 11, 11, 11 }, pixels);
        }
        finally { JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 }); }
    }

    [Fact]
    public async Task CompressedPixelsRemainSharedWhenOneWaiterCancelsAndTheAssetCloses()
    {
        AssetEntry entry = await SaveCompressedTextureAsync("compressed", 9);
        var storage = await CreateStorageAsync(entry);
        await using var loader = new AssetLoader(storage);
        using TextureAsset asset = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        TextureBlock block = asset.GetBlock<TextureBlock>();
        storage.Source.Hold = true;
        using var cancellation = new CancellationTokenSource();
        Task<Texture> first = block.CreateAsync(cancellationToken: cancellation.Token).AsTask();
        await storage.Source.Started.Task;
        Task<Texture> second = block.CreateAsync().AsTask();
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await asset.DisposeAsync();
        }
        finally { storage.Source.Continue.TrySetResult(); }
        Texture texture = await second;
        Assert.True(texture.TryGetTile(texture.MipTiles[0].ChunkKey, out ReadOnlyMemory<byte> bytes));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, bytes.ToArray());
    }

    [Fact]
    public async Task InvalidCompressedPixelsCompleteBothSharedWaitersWithTheDecodeFailure()
    {
        AssetEntry entry = await SaveCompressedTextureAsync("invalid-compressed", 3);
        long offset;
        await using (BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(entry.Path),
            TextureAsset.WireType, ownsSource: true))
        {
            using TextureAsset asset = await TextureAsset.OpenAsync(entry.Path);
            TextureBlock block = asset.GetBlock<TextureBlock>();
            ulong key = asset.BlockDescriptions.Single().GetStorageKey(block.MipTiles[0].ChunkKey);
            offset = (await document.FindChunkAsync(key))!.Value.Offset;
        }
        using (FileStream file = File.Open(entry.Path, FileMode.Open, FileAccess.Write))
        {
            file.Position = offset;
            file.WriteByte(0xff);
        }
        var storage = await CreateStorageAsync(entry);
        await using var loader = new AssetLoader(storage);
        using TextureAsset failed = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        TextureBlock pixels = failed.GetBlock<TextureBlock>();
        storage.Source.Hold = true;
        Task<Texture> first = pixels.CreateAsync().AsTask();
        await storage.Source.Started.Task;
        Task<Texture> second = pixels.CreateAsync().AsTask();
        storage.Source.Continue.TrySetResult();
        await Assert.ThrowsAsync<InvalidDataException>(() => first);
        await Assert.ThrowsAsync<InvalidDataException>(() => second);
    }

    private async Task<AssetEntry> SaveCompressedTextureAsync(string name, byte value)
    {
        AssetEntry entry = await SaveTextureAsync(name, value);
        using TextureAsset asset = await TextureAsset.OpenAsync(entry.Path);
        TextureBlock block = asset.GetBlock<TextureBlock>();
        ulong pixelKey = asset.BlockDescriptions.Single().GetStorageKey(block.MipTiles[0].ChunkKey);
        await using BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(entry.Path),
            TextureAsset.WireType, ownsSource: true);
        var header = new AssetHeader();
        document.ReadRootInto(header);
        BinaryDocumentWriter writer = BinaryDocumentWriter.Create(header);
        foreach (BinaryWireTypeDescriptor type in document.TypeCatalog) writer.AddContract(type);
        await foreach (BinaryChunkEntry chunk in document.EnumerateChunksAsync())
        {
            if (chunk.Key == pixelKey)
                writer.AddChunk(chunk.Key, await document.CopyChunkAsync(new(chunk.Key, chunk.DecodedLength)),
                    chunk.TypeFingerprint, ChunkCompression.Brotli, chunk.Alignment, chunk.Ordinal);
            else
                await writer.AddChunkAsync(chunk.Key, document);
        }
        string path = Path.Combine(_directory, name + "-brotli.texture.asset");
        await using FileStream destination = File.Create(path);
        await writer.WriteAsync(destination, generation: entry.Version);
        return entry with { Path = path };
    }

    [Fact]
    public async Task RepeatedCreationsAndBlockBranchesShareTheFinalArray()
    {
        AssetEntry entry = await SaveTextureAsync("shared", 7);
        using TextureAsset asset = await TextureAsset.OpenAsync(entry.Path);
        TextureBlock block = asset.GetBlock<TextureBlock>();
        Texture first = await block.CreateAsync();
        using TextureBlock branch = block.Duplicate();
        Texture second = await branch.CreateAsync();
        Assert.True(first.TryGetTile(block.MipTiles[0].ChunkKey, out var firstBytes));
        Assert.True(second.TryGetTile(block.MipTiles[0].ChunkKey, out var secondBytes));
        Assert.True(firstBytes.Equals(secondBytes));
        asset.Dispose();
        branch.Dispose();
        Assert.True(second.TryGetTile(second.MipTiles[0].ChunkKey, out _));
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, secondBytes.ToArray());
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, firstBytes.ToArray());
    }

    [Fact]
    public async Task CancelledWaiterAndClosedAssetDoNotStopTheOtherCreation()
    {
        AssetEntry entry = await SaveTextureAsync("concurrent", 9);
        var storage = await CreateStorageAsync(entry);
        await using var loader = new AssetLoader(storage);
        using TextureAsset asset = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        TextureBlock block = asset.GetBlock<TextureBlock>();
        storage.Source.Hold = true;
        using var cancelled = new CancellationTokenSource();
        Task<Texture> first = block.CreateAsync(cancellationToken: cancelled.Token).AsTask();
        await storage.Source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Texture> second = block.CreateAsync().AsTask();
        try
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await asset.DisposeAsync();
            Assert.False(second.IsCompleted);
        }
        finally { storage.Source.Continue.TrySetResult(); }
        Texture ready = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, storage.Source.DataReads);
        Assert.True(ready.TryGetTile(ready.MipTiles[0].ChunkKey, out var bytes));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, bytes.ToArray());
    }

    [Fact]
    public async Task FailedDataReadCanBeRetriedAndItsSuccessIsShared()
    {
        AssetEntry entry = await SaveTextureAsync("retry", 3);
        var storage = await CreateStorageAsync(entry);
        await using var loader = new AssetLoader(storage);
        using TextureAsset asset = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        TextureBlock block = asset.GetBlock<TextureBlock>();
        storage.Source.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => block.CreateAsync().AsTask());
        Texture first = await block.CreateAsync();
        Texture second = await block.CreateAsync();
        Assert.Equal(2, storage.Source.DataReads);
        Assert.True(first.TryGetTile(first.MipTiles[0].ChunkKey, out var left));
        Assert.True(second.TryGetTile(second.MipTiles[0].ChunkKey, out var right));
        Assert.True(left.Equals(right));
    }

    [Fact]
    public async Task CacheRetainsUnusedPixelsUntilItsReferencesAreCleared()
    {
        AssetEntry entry = await SaveTextureAsync("collect", 5);
        var storage = await CreateStorageAsync(entry);
        var cache = new AssetByteCache(1024);
        await using var loader = new AssetLoader(storage, new() { ByteCache = cache });
        using TextureAsset asset = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        TextureBlock block = asset.GetBlock<TextureBlock>();
        WeakReference<byte[]> bytes = LoadAndRelease(block);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.True(IsAlive(bytes));
        Assert.Equal(1, cache.CachedItems);
        Assert.InRange(cache.CachedBytes + cache.IndexBytes, 1, cache.BudgetBytes);
        cache.Clear();
        for (int index = 0; index < 5 && IsAlive(bytes); index++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        Assert.False(IsAlive(bytes));
        Texture reloaded = await block.CreateAsync();
        Assert.Equal(2, storage.Source.DataReads);
        Assert.True(reloaded.TryGetTile(reloaded.MipTiles[0].ChunkKey, out var pixels));
        Assert.Equal(new byte[] { 5, 5, 5, 5 }, pixels.ToArray());
        GC.KeepAlive(block);
    }

    [Fact]
    public async Task CacheBudgetReleasePreservesConsumerArraysAndAllowsRereading()
    {
        AssetEntry entry = await SaveTextureAsync("budget", 6);
        var storage = await CreateStorageAsync(entry);
        var cache = new AssetByteCache(1024);
        await using var loader = new AssetLoader(storage, new() { ByteCache = cache });
        using TextureAsset asset = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        TextureBlock block = asset.GetBlock<TextureBlock>();
        Texture first = await block.CreateAsync();
        Texture second = await block.CreateAsync();
        Assert.Equal(1, storage.Source.DataReads);
        cache.SetBudget(0);
        Assert.Equal(0, cache.CachedBytes + cache.IndexBytes);
        Texture third = await block.CreateAsync();
        Texture fourth = await block.CreateAsync();
        Assert.Equal(3, storage.Source.DataReads);
        Assert.True(first.TryGetTile(first.MipTiles[0].ChunkKey, out var held));
        Assert.Equal(new byte[] { 6, 6, 6, 6 }, held.ToArray());
        Assert.Equal(0, cache.CachedItems);
    }

    [Fact]
    public async Task ClearingTheCacheDuringAReadKeepsTheResultForItsWaiter()
    {
        AssetEntry entry = await SaveTextureAsync("clear-read", 4);
        var storage = await CreateStorageAsync(entry);
        var cache = new AssetByteCache(1024);
        await using var loader = new AssetLoader(storage, new() { ByteCache = cache });
        using TextureAsset asset = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        storage.Source.Hold = true;
        Task<Texture> read = asset.GetBlock<TextureBlock>().CreateAsync().AsTask();
        await storage.Source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cache.Clear();
        storage.Source.Continue.TrySetResult();
        Texture texture = await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(texture.TryGetTile(texture.MipTiles[0].ChunkKey, out var bytes));
        Assert.Equal(new byte[] { 4, 4, 4, 4 }, bytes.ToArray());
        Assert.Equal(0, cache.CachedItems);
    }

    [Fact]
    public async Task ReopeningTheSameFileVersionReusesTheCachedArray()
    {
        AssetEntry entry = await SaveTextureAsync("reopen", 8);
        var storage = await CreateStorageAsync(entry);
        var cache = new AssetByteCache(1024);
        await using var loader = new AssetLoader(storage, new() { ByteCache = cache });
        ReadOnlyMemory<byte> held;
        using (TextureAsset asset = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid)))
        {
            Texture texture = await asset.GetBlock<TextureBlock>().CreateAsync();
            Assert.True(texture.TryGetTile(texture.MipTiles[0].ChunkKey, out held));
        }
        using TextureAsset reopened = await TextureAsset.OpenAsync(loader, new(entry.AssetGuid));
        Texture replacement = await reopened.GetBlock<TextureBlock>().CreateAsync();
        Assert.True(replacement.TryGetTile(replacement.MipTiles[0].ChunkKey, out var bytes));
        Assert.True(held.Equals(bytes));
        Assert.Equal(1, storage.Source.DataReads);
    }

    [Fact]
    public async Task SharedBudgetEvictsTheLeastRecentlyUsedAsset()
    {
        var cache = new AssetByteCache(4096);
        AssetEntry[] entries = [await SaveTextureAsync("lru-first", 1), await SaveTextureAsync("lru-second", 2),
            await SaveTextureAsync("lru-third", 3)];
        var storage = await Task.WhenAll(entries.Select(CreateStorageAsync));
        await using var firstLoader = new AssetLoader(storage[0], new() { ByteCache = cache });
        await using var secondLoader = new AssetLoader(storage[1], new() { ByteCache = cache });
        await using var thirdLoader = new AssetLoader(storage[2], new() { ByteCache = cache });
        using TextureAsset first = await TextureAsset.OpenAsync(firstLoader, new(entries[0].AssetGuid));
        using TextureAsset second = await TextureAsset.OpenAsync(secondLoader, new(entries[1].AssetGuid));
        using TextureAsset third = await TextureAsset.OpenAsync(thirdLoader, new(entries[2].AssetGuid));
        Texture heldFirst = await first.GetBlock<TextureBlock>().CreateAsync();
        Texture heldSecond = await second.GetBlock<TextureBlock>().CreateAsync();
        Texture heldThird = await third.GetBlock<TextureBlock>().CreateAsync();
        Assert.Equal(3, cache.CachedItems);
        cache.SetBudget(cache.IndexBytes + cache.CachedBytes * 2 / 3);
        Assert.Equal(2, cache.CachedItems);
        Texture touchSecond = await second.GetBlock<TextureBlock>().CreateAsync();
        Texture rereadFirst = await first.GetBlock<TextureBlock>().CreateAsync();
        Texture secondAgain = await second.GetBlock<TextureBlock>().CreateAsync();
        Assert.Equal(1, storage[1].Source.DataReads);
        Texture rereadThird = await third.GetBlock<TextureBlock>().CreateAsync();
        Assert.Equal(2, storage[0].Source.DataReads);
        Assert.Equal(2, storage[2].Source.DataReads);
        Assert.InRange(cache.CachedBytes + cache.IndexBytes, 1, cache.BudgetBytes);
        Assert.True(heldFirst.TryGetTile(heldFirst.MipTiles[0].ChunkKey, out var bytes));
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, bytes.ToArray());
    }

    [Fact]
    public async Task OldBlockCannotSupplyTilesToANewContentVersionWithTheSameShape()
    {
        AssetEntry before = await SaveTextureAsync("before", 1, twoMips: true);
        AssetEntry after = await SaveTextureAsync("after", 2, twoMips: true, id: before.AssetGuid);
        using TextureAsset oldAsset = await TextureAsset.OpenAsync(before.Path);
        using TextureAsset newAsset = await TextureAsset.OpenAsync(after.Path);
        TextureBlock oldBlock = oldAsset.GetBlock<TextureBlock>(), newBlock = newAsset.GetBlock<TextureBlock>();
        Texture texture = await oldBlock.CreateAsync();
        await newBlock.UpdateAsync(texture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldBlock.LoadMipAsync(texture, 0).AsTask());
        await newBlock.LoadMipAsync(texture, 0);
        Assert.True(texture.TryGetTile(newBlock.MipTiles[0].ChunkKey, out var bytes));
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, bytes.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> bytes) => bytes.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<byte[]> LoadAndRelease(TextureBlock block)
    {
        Texture texture = block.CreateAsync().AsTask().GetAwaiter().GetResult();
        Assert.True(texture.TryGetTile(texture.MipTiles[0].ChunkKey, out var pixels));
        Assert.True(MemoryMarshal.TryGetArray(pixels, out ArraySegment<byte> data));
        return new(data.Array!);
    }

    private async Task<AssetEntry> SaveTextureAsync(string name, byte value, bool twoMips = false, Guid id = default)
    {
        using var asset = new TextureAsset(id) { Name = name };
        TextureBlock block = asset.GetBlock<TextureBlock>();
        block.Width = block.Height = 2;
        block.Depth = block.ArrayLayerCount = 1;
        block.MipLevelCount = twoMips ? 2u : 1u;
        block.Dimension = TextureDimension.Texture2D;
        block.Format = block.SampledFormat = Format.R8UNorm;
        block.SampledDimension = TextureViewDimension.Texture2D;
        for (uint mip = 0; mip < block.MipLevelCount; mip++)
        {
            uint size = 2u >> (int)mip;
            byte[] bytes = Enumerable.Repeat(value, checked((int)(size * size))).ToArray();
            var tile = TextureTiles.Describe(new TextureMipTile
            {
                MipLevel = mip, Width = size, Height = size, RowPitch = size, SlicePitch = size * size,
            }, bytes.Length);
            block.MipTiles.Add(tile);
            block.SetBytes(tile.ChunkKey, bytes);
        }
        return await asset.SaveAsync(Path.Combine(_directory, name + ".texture.asset"));
    }

    private static async Task<TestStorage> CreateStorageAsync(AssetEntry entry)
    {
        using TextureAsset asset = await TextureAsset.OpenAsync(entry.Path);
        TextureBlock block = asset.GetBlock<TextureBlock>();
        ulong key = asset.BlockDescriptions.Single().GetStorageKey(block.MipTiles[0].ChunkKey);
        await using BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(entry.Path), TextureAsset.WireType, ownsSource: true);
        BinaryChunkEntry chunk = (await document.FindChunkAsync(key))!.Value;
        return new(entry, new TestSource(await File.ReadAllBytesAsync(entry.Path), chunk.Offset));
    }

    private sealed class TestStorage(AssetEntry entry, TestSource source) : IAssetStorage
    {
        internal TestSource Source => source;
        public bool TryFind(Guid id, out AssetEntry found) { found = entry; return id == entry.AssetGuid; }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry selected, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRangeSource>(source);
    }

    private sealed class TestSource(byte[] bytes, long dataOffset) : IRangeSource
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Hold;
        internal bool FailNext;
        internal int DataReads;
        public long Length => bytes.Length;
        public string Generation => "test-data";
        public bool LeasesAreImmutable => true;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset == dataOffset)
            {
                Interlocked.Increment(ref DataReads);
                Started.TrySetResult();
                if (Hold) await Continue.Task.WaitAsync(cancellationToken);
                if (FailNext) { FailNext = false; throw new IOException("Requested read failure."); }
            }
            bytes.AsMemory(checked((int)offset), destination.Length).CopyTo(destination);
        }
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(RangeLease.Borrow(bytes.AsMemory(checked((int)offset), length)));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
