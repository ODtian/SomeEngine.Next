using SomeEngine.Assets;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets.Consumer.Tests;

// This assembly deliberately has no engine friend access. Its file type and object type are
// ordinary downstream declarations, so these tests also compile the external extension API.
public sealed class DiskAssetBehaviorTests
{
    [Fact]
    public async Task ExternalConsumerCanInspectBuiltInTextureFilesWithoutCreatingAnObject()
    {
        using var files = new TestFiles();
        string path = System.IO.Path.Combine(files.Directory, "texture.texture.asset");
        using var pixels = new SomeEngine.Assets.Schema.TextureBlock
        {
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Format = SomeEngine.Graphics.Format.R8UNorm, SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = [new SomeEngine.Resources.TextureMipTile { Width = 1, Height = 1, ChunkKey = SomeEngine.Resources.TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 1 }],
        };
        pixels.SetBytes(pixels.MipTiles[0].ChunkKey, new byte[] { 1 });
        using var original = new SomeEngine.Assets.Schema.TextureAsset();
        original.SetBlock(pixels);
        await original.SaveAsync(path);
        using var file = await SomeEngine.Assets.Schema.TextureAsset.OpenAsync(path);
        var content = await file.GetBlockAsync<SomeEngine.Assets.Schema.TextureBlock>();
        var shape = content.GetDescription(file.Name);
        Assert.Equal(1u, shape.Width);
        Assert.Equal(1u, shape.Height);
        Assert.Equal(1u, shape.MipLevelCount);
        var tile = Assert.Single(content.MipTiles);
        Assert.Empty(content.Chunks.Keys);
        Assert.NotEqual(0ul, tile.ChunkKey);
        Assert.Equal(1u, Assert.Single(content.MipTiles).Width);
    }

    [Fact]
    public async Task OpeningFileDoesNotConstructTheDomainObject()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 17, [1, 2, 3]);
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        DiskProbe.ResetConstructionCount();

        await using DiskProbeAsset asset = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));

        Assert.Equal(id, asset.Id);
        Assert.Equal("probe-17", asset.Name);
        Assert.Equal(17, (await asset.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(0, DiskProbe.ConstructionCount);
    }

    [Fact]
    public async Task SameLoaderSeesNewFileWhileTheOldOpenKeepsItsContent()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        await using DiskProbeAsset first = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));
        Guid firstVersion = first.SourceVersion;

        files.Save(id, 2, [2, 3]);
        await using DiskProbeAsset second = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));

        Assert.NotSame(first, second);
        Assert.Equal(1, (await first.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(2, (await second.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.NotEqual(firstVersion, second.SourceVersion);
        await using DiskProbe oldObject = await (await first.GetBlockAsync<DiskProbeBlock>()).CreateAsync();
        await using DiskProbe newObject = await (await second.GetBlockAsync<DiskProbeBlock>()).CreateAsync();
        Assert.Equal(new byte[] { 1 }, oldObject.ReadBytes());
        Assert.Equal(new byte[] { 2, 3 }, newObject.ReadBytes());
    }

    [Fact]
    public async Task DirectFileOpenReportsTheContentVersion()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using DiskProbeAsset first = await DiskProbeAsset.OpenAsync(files.Path);
        Guid firstVersion = first.SourceVersion;
        Assert.NotEqual(Guid.Empty, firstVersion);

        files.Save(id, 2, [2]);
        await using DiskProbeAsset second = await DiskProbeAsset.OpenAsync(files.Path);

        Assert.NotEqual(Guid.Empty, second.SourceVersion);
        Assert.NotEqual(firstVersion, second.SourceVersion);
    }

    [Fact]
    public async Task CancelingOneOpenDoesNotCancelTheSharedRead()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 7, [7]);
        var storage = new PausingStorage(files.Project.CreateStorage());
        await using var loader = new AssetLoader(storage);
        using var cancellation = new CancellationTokenSource();
        Task<DiskProbeAsset> canceled = DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id), cancellation.Token).AsTask();
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<DiskProbeAsset> other = DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id)).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        storage.Continue.TrySetResult();
        await using DiskProbeAsset second = await other.WaitAsync(TimeSpan.FromSeconds(15));
        await using DiskProbeAsset third = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));

        Assert.Equal(1, storage.OpenCount);
        Assert.NotSame(second, third);
        await second.DisposeAsync();
        Assert.Equal(7, (await third.GetBlockAsync<DiskProbeBlock>()).Number);
    }

    [Fact]
    public async Task ObjectsKeepTheirFileBytesAfterAssetsAndLoaderClose()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 4, [9, 8, 7, 6]);
        var storage = new PausingStorage(files.Project.CreateStorage());
        storage.Continue.TrySetResult();
        var loader = new AssetLoader(storage);
        DiskProbeAsset first = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));
        DiskProbeAsset second = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));
        await using DiskProbe objectA = await (await first.GetBlockAsync<DiskProbeBlock>()).CreateAsync();
        await using DiskProbe objectB = await (await second.GetBlockAsync<DiskProbeBlock>()).CreateAsync();

        await first.DisposeAsync();
        await second.DisposeAsync();
        await loader.DisposeAsync();

        Assert.False(storage.WasClosed);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, objectA.ReadBytes());
        await objectA.DisposeAsync();
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, objectB.ReadBytes());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id)));
    }

    [Fact]
    public async Task FileContentChangesAreDetectedEvenWhenLengthAndTimestampMatch()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        DateTime timestamp = File.GetLastWriteTimeUtc(files.Path);
        long length = new FileInfo(files.Path).Length;
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        await using DiskProbeAsset first = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));

        files.Save(id, 2, [2]);
        Assert.Equal(length, new FileInfo(files.Path).Length);
        File.SetLastWriteTimeUtc(files.Path, timestamp);
        await using DiskProbeAsset second = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));

        Assert.Equal(1, (await first.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(2, (await second.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.NotEqual(first.SourceVersion, second.SourceVersion);
    }

    [Fact]
    public async Task PackedBytesRemainReadableAfterStorageIsClosed()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 5, [3, 1, 4, 1, 5]);
        string packPath = System.IO.Path.Combine(files.Directory, "test.pack");
        var builder = new AssetPackBuilder().AddAsset(id, DiskProbeAsset.FileType,
            File.ReadAllBytes(files.Path), DiskProbeAsset.SchemaHash);
        await using (var output = new FileStream(packPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            await builder.WriteAsync(output);
        AssetPack pack = await AssetPack.OpenAsync(new MemoryRangeSource(File.ReadAllBytes(packPath)), ownsSource: true);
        var storage = new AssetPackStorage([pack]);
        var loader = new AssetLoader(storage);
        DiskProbeAsset asset = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));
        Assert.True(storage.TryFind(id, out AssetEntry packed));
        Assert.Equal(pack.Generation, packed.Version);
        Assert.NotEqual(packed.Version, asset.SourceVersion);
        await using DiskProbe value = await (await asset.GetBlockAsync<DiskProbeBlock>()).CreateAsync();
        await asset.DisposeAsync();
        await loader.DisposeAsync();
        await storage.DisposeAsync();

        Assert.Equal(new byte[] { 3, 1, 4, 1, 5 }, value.ReadBytes());
    }

    [Fact]
    public async Task FailureClosingOneSourceDoesNotSkipOtherSources()
    {
        using var files = new TestFiles();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        files.Save(firstId, 1, [1]);
        using (DiskProbeAsset secondFile = CreateAsset(secondId, "second", 2, [2]))
        {
            files.Project.RegisterAsset(await secondFile.SaveAsync(System.IO.Path.Combine(files.Directory, "second.probe.asset")));
        }
        await using var storage = new ClosingStorage(files.Project.CreateStorage(), firstId);
        var loader = new AssetLoader(storage);
        DiskProbeAsset first = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(firstId));
        DiskProbeAsset second = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(secondId));
        await first.DisposeAsync();
        await second.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () => await loader.DisposeAsync());

        Assert.Contains(firstId, storage.Closed);
        Assert.Contains(secondId, storage.Closed);
    }

    [Fact]
    public async Task IncorrectAssetTypeIsRejectedBeforeOpeningBytes()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 11, [11]);
        var storage = new PausingStorage(files.Project.CreateStorage());
        storage.Continue.TrySetResult();
        await using var loader = new AssetLoader(storage);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await OtherProbeAsset.OpenAsync(loader, new AssetId<OtherProbeAsset>(id)));

        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task IncorrectSuffixDoesNotReplaceTheDestination()
    {
        using var files = new TestFiles();
        string destination = System.IO.Path.Combine(files.Directory, "bad.other.asset");
        byte[] original = [11, 22, 33, 44];
        File.WriteAllBytes(destination, original);
        using var asset = new DiskProbeAsset { Name = "invalid-output" };
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.SaveAsync(destination).AsTask());
        Assert.Equal(original, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task EditingOneAssetDoesNotChangeAnotherOpenOrItsSource()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 17, [1, 2, 3]);
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        using var first = await DiskProbeAsset.OpenAsync(loader, new(id));
        using var second = await DiskProbeAsset.OpenAsync(loader, new(id));
        Guid version = first.SourceVersion;
        using var branch = first.Duplicate();
        (await first.GetBlockAsync<DiskProbeBlock>()).Number = 99;
        Assert.Equal(99, (await first.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(17, (await branch.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(17, (await second.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(version, first.SourceVersion);
        using var reopened = await DiskProbeAsset.OpenAsync(files.Path);
        Assert.Equal(17, (await reopened.GetBlockAsync<DiskProbeBlock>()).Number);
    }

    [Fact]
    public async Task ByteWritesSeparateTheInputAndRetainedReaders()
    {
        using var files = new TestFiles();
        byte[] bytes = [1, 2, 3];
        using var asset = CreateAsset(Guid.NewGuid(), "edited", 17, bytes);
        using var branch = asset.Duplicate();
        var reader = asset.GetBlock<DiskProbeBlock>().Chunks[1];
        byte[] edited = asset.GetBlock<DiskProbeBlock>().Chunks[1].ToArray();
        edited[1] = 8;
        asset.GetBlock<DiskProbeBlock>().SetBytes(1, edited);
        await asset.SaveAsync(files.Path);
        using var opened = await DiskProbeAsset.OpenAsync(files.Path);
        var contents = await (await opened.GetBlockAsync<DiskProbeBlock>()).ReadBytesAsync(1);
        var contentRead = contents;
        Assert.Equal(new byte[] { 1, 8, 3 }, contentRead.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        var branchRead = branch.GetBlock<DiskProbeBlock>().Chunks[1];
        Assert.Equal(new byte[] { 1, 2, 3 }, branchRead.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ToArray());
    }

    [Theory]
    [InlineData(1, long.MaxValue)]
    [InlineData(128, 0L)]
    public async Task CacheEvictionDoesNotInvalidateExistingObjects(int maxFiles, long rootBytes)
    {
        using var files = new TestFiles();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        files.Save(firstId, 1, [1]);
        using (DiskProbeAsset otherFile = CreateAsset(secondId, "other", 2, [2]))
        {
            files.Project.RegisterAsset(await otherFile.SaveAsync(System.IO.Path.Combine(files.Directory, "other.probe.asset")));
        }
        var storage = new PausingStorage(files.Project.CreateStorage());
        storage.Continue.TrySetResult();
        await using var loader = new AssetLoader(storage, new()
        {
            MaxCachedFiles = maxFiles, MaxCachedRootBytes = rootBytes,
        });
        await using DiskProbeAsset first = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(firstId));
        await using DiskProbe original = await (await first.GetBlockAsync<DiskProbeBlock>()).CreateAsync();
        await using DiskProbeAsset second = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(secondId));
        await using DiskProbeAsset reopened = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(firstId));

        Assert.Equal(3, storage.OpenCount);
        await loader.DisposeAsync();
        Assert.Equal(new byte[] { 1 }, original.ReadBytes());
        Assert.Equal(2, (await second.GetBlockAsync<DiskProbeBlock>()).Number);
    }

    [Fact]
    public async Task StorageWithoutVersionsDoesNotCreateAnUnchangingCacheEntry()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using var loader = new AssetLoader(new UnversionedStorage(files.Project.CreateStorage()));
        await using DiskProbeAsset first = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));
        files.Save(id, 2, [2]);
        await using DiskProbeAsset second = await DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id));

        Assert.Equal(1, (await first.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(2, (await second.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.NotEqual(first.SourceVersion, second.SourceVersion);
    }

    [Fact]
    public async Task FileReadsRespectTheConfiguredConcurrencyLimit()
    {
        using var files = new TestFiles();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        files.Save(firstId, 1, [1]);
        using (DiskProbeAsset secondFile = CreateAsset(secondId, "other", 2, [2]))
        {
            files.Project.RegisterAsset(await secondFile.SaveAsync(System.IO.Path.Combine(files.Directory, "other.probe.asset")));
        }
        var storage = new PausingStorage(files.Project.CreateStorage());
        await using var loader = new AssetLoader(storage, new() { MaxConcurrentReads = 1 });
        Task<DiskProbeAsset> first = DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(firstId)).AsTask();
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<DiskProbeAsset> second = DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(secondId)).AsTask();
        Assert.Equal(1, storage.OpenCount);

        storage.Continue.TrySetResult();
        await using DiskProbeAsset firstFile = await first;
        await using DiskProbeAsset secondFileRead = await second;
        Assert.Equal(2, storage.OpenCount);
    }

    [Fact]
    public async Task ClosingLoaderCancelsQueuedReadsAndCanBeAwaitedMoreThanOnce()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        var storage = new PausingStorage(files.Project.CreateStorage());
        var loader = new AssetLoader(storage);
        Task<DiskProbeAsset> reading = DiskProbeAsset.OpenAsync(loader, new AssetId<DiskProbeAsset>(id)).AsTask();
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task firstClose = loader.DisposeAsync().AsTask();
        Task secondClose = loader.DisposeAsync().AsTask();
        await Task.WhenAll(firstClose, secondClose).WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
        Assert.False(storage.WasClosed);
    }

    private sealed class UnversionedStorage(IAssetStorage source) : IAssetStorage
    {
        public bool TryFind(Guid id, out AssetEntry entry)
        {
            bool found = source.TryFind(id, out entry);
            entry = entry with { Version = Guid.Empty };
            return found;
        }
        public ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
        {
            if (!source.TryFind(entry.AssetGuid, out AssetEntry current))
                throw new KeyNotFoundException();
            return source.OpenAsync(current, cancellationToken);
        }
    }

    [Fact]
    public async Task SavingAnEditedFileWritesItsValuesAndKeepsTheSourceVersion()
    {
        using var files = new TestFiles();
        byte[] bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
        files.Save(Guid.NewGuid(), 31, bytes);
        using var file = await DiskProbeAsset.OpenAsync(files.Path);
        Guid sourceVersion = file.SourceVersion;
        (await file.GetBlockAsync<DiskProbeBlock>()).Number = 99;
        AssetEntry saved = await file.SaveAsync(System.IO.Path.Combine(files.Directory, "copy.probe.asset"));
        using var copy = await DiskProbeAsset.OpenAsync(saved.Path);
        Assert.Equal(99, (await copy.GetBlockAsync<DiskProbeBlock>()).Number);
        Assert.Equal(saved.Version, copy.SourceVersion);
        Assert.Equal(sourceVersion, file.SourceVersion);
        Assert.NotEqual(sourceVersion, saved.Version);
        var contents = await (await copy.GetBlockAsync<DiskProbeBlock>()).ReadBytesAsync(1);
        var contentRead = contents;
        Assert.Equal(bytes, contentRead.ToArray());
    }

    [Fact]
    public async Task CorruptChunkCopyDoesNotReplaceExistingDestination()
    {
        using var files = new TestFiles();
        files.Save(Guid.NewGuid(), 9, [1, 2, 3]);
        byte[] corrupt = File.ReadAllBytes(files.Path);
        using (AssetFile document = await AssetFile.OpenAsync(files.Path, DiskProbeAsset.WireType))
        {
            var header = new AssetHeader();
            document.ReadRootInto(header);
            using var source = new AssetBlockSource(document, Assert.Single(header.BlockDescriptions));
            BinaryChunkEntry chunk = await source.DescribeChunkAsync(1);
            corrupt[checked((int)chunk.Offset)] ^= 0x40;
        }
        string brokenPath = System.IO.Path.Combine(files.Directory, "broken.probe.asset");
        File.WriteAllBytes(brokenPath, corrupt);
        string destination = System.IO.Path.Combine(files.Directory, "destination.probe.asset");
        byte[] original = [7, 8, 9];
        File.WriteAllBytes(destination, original);
        await using DiskProbeAsset broken = await DiskProbeAsset.OpenAsync(brokenPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => broken.SaveAsync(destination).AsTask());
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Empty(System.IO.Directory.EnumerateFiles(files.Directory, "*.tmp"));
    }

    [Fact]
    public async Task RetainingAndDuplicatingAnAssetDoesNotDecodeItsFieldsAgain()
    {
        using var files = new TestFiles();
        string path = System.IO.Path.Combine(files.Directory, "counted.counted.asset");
        using var created = new CountedAsset();
        using var content = new CountedBlock { Value = new(7) };
        created.SetBlock(content);
        await created.SaveAsync(path);
        CountedNumber.Reads = 0;
        using var opened = await CountedAsset.OpenAsync(path);
        Assert.Equal(0, CountedNumber.Reads);
        CountedBlock openedContent = await opened.GetBlockAsync<CountedBlock>();
        Assert.Equal(1, CountedNumber.Reads);
        using var copy = opened.Duplicate();
        using var source = opened.Duplicate();
        copy.GetBlock<CountedBlock>().Value = new(8);
        await copy.SaveAsync(System.IO.Path.Combine(files.Directory, "copy.counted.asset"));
        Assert.Equal(1, CountedNumber.Reads);
        Assert.Equal(7, openedContent.Value.Number);
        Assert.Equal(8, copy.GetBlock<CountedBlock>().Value.Number);
    }

    [Fact]
    public void ReplacingBytesKeepsExistingBranchesUnchanged()
    {
        using var asset = CreateAsset(Guid.NewGuid(), "sample", 0, [1]);
        using var before = asset.Duplicate();
        asset.GetBlock<DiskProbeBlock>().SetBytes(1, new byte[] { 2 });
        using var after = asset.Duplicate();
        Assert.Equal(new byte[] { 1 }, before.GetBlock<DiskProbeBlock>().Chunks[1].ToArray());
        Assert.Equal(new byte[] { 2 }, after.GetBlock<DiskProbeBlock>().Chunks[1].ToArray());
    }

    private sealed class TestFiles : IDisposable
    {
        internal TestFiles()
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "someengine-assets-consumer-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Project = new AssetProject(Directory, []);
            Path = System.IO.Path.Combine(Directory, "test.probe.asset");
        }

        internal string Directory { get; }
        internal string Path { get; }
        internal AssetProject Project { get; }

        internal void Save(Guid id, int number, byte[] bytes)
        {
            using DiskProbeAsset asset = CreateAsset(id, "probe-" + number, number, bytes);
            Project.RegisterAsset((asset).SaveAsync(global::System.IO.Path.Combine(Project.RootPath, Path)).AsTask().GetAwaiter().GetResult());
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private static DiskProbeAsset CreateAsset(Guid id, string name, int number, byte[] bytes)
    {
        var asset = new DiskProbeAsset(id) { Name = name };
        try
        {
            using var content = new DiskProbeBlock { Number = number, PayloadKey = 1, PayloadLength = checked((ulong)bytes.Length) };
            content.SetBytes(1, bytes);
            asset.SetBlock(content);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }

    private sealed class ClosingStorage(IAssetStorage source, Guid failId) : IAssetStorage, IAsyncDisposable
    {
        private readonly List<ClosingSource> _sources = [];
        internal HashSet<Guid> Closed { get; } = [];

        public bool TryFind(Guid id, out AssetEntry entry) => source.TryFind(id, out entry);

        public async ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
        {
            IRangeSource bytes = await source.OpenAsync(entry, cancellationToken);
            var result = new ClosingSource(bytes, () =>
            {
                Closed.Add(entry.AssetGuid);
                if (entry.AssetGuid == failId)
                    throw new InvalidOperationException("Injected source close failure.");
            });
            _sources.Add(result);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            // Fixture cleanup must release handles even when the product failed the assertion.
            foreach (ClosingSource bytes in _sources)
            {
                try { await bytes.DisposeAsync(); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private sealed class ClosingSource(IRangeSource source, Action closed) : IRangeSource
    {
        private int _closed;
        public long Length => source.Length;
        public string Generation => source.Generation;
        public bool LeasesAreImmutable => source.LeasesAreImmutable;
        public bool RetainsResidentBacking => source.RetainsResidentBacking;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
            => source.ReadExactlyAsync(offset, destination, cancellationToken);
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => source.AcquireAsync(offset, length, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { await source.DisposeAsync(); }
            finally { closed(); }
        }
    }

    private sealed class PausingStorage(IAssetStorage source) : IAssetStorage, IAsyncDisposable
    {
        private int _openCount;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal bool WasClosed { get; private set; }

        public bool TryFind(Guid id, out AssetEntry entry) => source.TryFind(id, out entry);

        public async ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCount);
            Started.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return await source.OpenAsync(entry, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            WasClosed = true;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class DiskProbe : IAsyncDisposable
{
    private static int _constructionCount;
    private ReadOnlyMemory<byte>? _bytes;
    public DiskProbe(ReadOnlyMemory<byte> bytes)
    {
        _bytes = bytes;
        Interlocked.Increment(ref _constructionCount);
    }
    public static int ConstructionCount => Volatile.Read(ref _constructionCount);
    public static void ResetConstructionCount() => Interlocked.Exchange(ref _constructionCount, 0);
    public byte[] ReadBytes()
    {
        var read = _bytes ?? throw new ObjectDisposedException(nameof(DiskProbe));
        return read.ToArray();
    }
    public ValueTask DisposeAsync()
    {
        _bytes = null;
        return ValueTask.CompletedTask;
    }
}

[Asset(".probe.asset", SchemaName = "SomeEngine.Assets.Consumer.Tests.DiskProbeAsset"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class DiskProbeAsset : Asset
{
    public new System.Collections.Immutable.ImmutableArray<AssetBlockDescription> BlockDescriptions
    { get => base.BlockDescriptions; private set => base.BlockDescriptions = value; }
}

[AssetBlock("SomeEngine.Assets.Consumer.Tests.DiskProbeBlock"), BinarySerializable, BinaryFields(nameof(Number), nameof(PayloadKey), nameof(PayloadLength))]
public sealed partial class DiskProbeBlock : AssetBlock
{
    public partial int Number { get; set; }
    public partial ulong PayloadKey { get; set; }
    public partial ulong PayloadLength { get; set; }
    public async ValueTask<DiskProbe> CreateAsync(CancellationToken cancellationToken = default)
    {
        using var copy = Duplicate();
        var bytes = await copy.ReadBytesAsync(copy.PayloadKey, cancellationToken);
        if ((ulong)bytes.Length != copy.PayloadLength) throw new InvalidDataException("Probe payload length differs.");
        return new DiskProbe(bytes);
    }
}

[Asset(".other.asset", SchemaName = "SomeEngine.Assets.Consumer.Tests.OtherProbeAsset"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class OtherProbeAsset : Asset
{
    public new System.Collections.Immutable.ImmutableArray<AssetBlockDescription> BlockDescriptions
    { get => base.BlockDescriptions; private set => base.BlockDescriptions = value; }
}
[AssetBlock("SomeEngine.Assets.Consumer.Tests.OtherProbeBlock"), BinarySerializable, BinaryFields(nameof(Number))]
public sealed partial class OtherProbeBlock : AssetBlock
{
    public partial int Number { get; set; }
}

[Asset(".counted.asset", SchemaName = "SomeEngine.Assets.Consumer.Tests.CountedAsset"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class CountedAsset : Asset
{
    public new System.Collections.Immutable.ImmutableArray<AssetBlockDescription> BlockDescriptions
    { get => base.BlockDescriptions; private set => base.BlockDescriptions = value; }
}
[AssetBlock("SomeEngine.Assets.Consumer.Tests.CountedBlock"), BinarySerializable, BinaryFields(nameof(Value))]
public sealed partial class CountedBlock : AssetBlock
{
    public partial CountedNumber Value { get; set; }
}
[BinarySerializable]
public readonly record struct CountedNumber(int Number) : IBinarySerializable<CountedNumber>
{
    public static int Reads;
    public static Guid TypeId => new("E6E799DD-6FBC-4399-BABD-EB6DC90B016B");
    public static ulong SchemaFingerprint => 0x4E554D4245520001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;
    public static void Write(ref BinaryDataWriter writer, CountedNumber value) => writer.WriteInt32(value.Number);
    public static void ValidateView(ref BinaryViewReader reader) => reader.ReadInt32();
    public static CountedNumber Read(ref BinaryDataReader reader)
    {
        Interlocked.Increment(ref Reads);
        return new(reader.ReadInt32());
    }
}
