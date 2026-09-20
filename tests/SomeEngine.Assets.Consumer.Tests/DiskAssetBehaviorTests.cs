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
        using var original = new SomeEngine.Assets.Schema.Texture.Asset
        {
            Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Format = SomeEngine.Graphics.Format.R8UNorm, SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = [new SomeEngine.Assets.Schema.TextureMipTile { Width = 1, Height = 1, Payload = new byte[] { 1 } }],
        };
        await original.SaveAsync(path);
        using var file = await SomeEngine.Assets.Schema.Texture.Asset.OpenAsync(path);
        var shape = SomeEngine.Assets.Schema.Texture.ReadDescription(file);
        Assert.Equal(1u, shape.Width);
        Assert.Equal(1u, shape.Height);
        Assert.Equal(1u, shape.MipLevelCount);
        var tile = Assert.Single(SomeEngine.Assets.Schema.Texture.ReadMipTiles(file));
        Assert.Null(tile.Payload);
        Assert.NotEqual(0ul, tile.ChunkKey);
        Assert.Equal(1u, Assert.Single(SomeEngine.Assets.Schema.Texture.ReadMipTiles(file)).Width);
    }

    [Fact]
    public async Task OpeningFileDoesNotConstructTheDomainObject()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 17, [1, 2, 3]);
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        DiskProbe.ResetConstructionCount();

        await using DiskProbe.Asset asset = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));

        Assert.Equal(id, asset.Id.Value);
        Assert.Equal("probe-17", asset.Name);
        Assert.Equal(17, asset.Number);
        Assert.Equal(0, DiskProbe.ConstructionCount);
    }

    [Fact]
    public async Task SameLoaderSeesNewFileWhileTheOldOpenKeepsItsContent()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        await using DiskProbe.Asset first = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));
        Guid firstVersion = first.SourceVersion;

        files.Save(id, 2, [2, 3]);
        await using DiskProbe.Asset second = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));

        Assert.NotSame(first, second);
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.NotEqual(firstVersion, second.SourceVersion);
        await using DiskProbe oldObject = DiskProbe.Create(first);
        await using DiskProbe newObject = DiskProbe.Create(second);
        Assert.Equal(new byte[] { 1 }, await oldObject.ReadBytesAsync());
        Assert.Equal(new byte[] { 2, 3 }, await newObject.ReadBytesAsync());
    }

    [Fact]
    public async Task DirectFileOpenReportsTheContentVersion()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using DiskProbe.Asset first = await DiskProbe.Asset.OpenAsync(files.Path);
        Guid firstVersion = first.SourceVersion;
        Assert.NotEqual(Guid.Empty, firstVersion);

        files.Save(id, 2, [2]);
        await using DiskProbe.Asset second = await DiskProbe.Asset.OpenAsync(files.Path);

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
        Task<DiskProbe.Asset> canceled = DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id), cancellation.Token).AsTask();
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<DiskProbe.Asset> other = DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id)).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        storage.Continue.TrySetResult();
        await using DiskProbe.Asset second = await other.WaitAsync(TimeSpan.FromSeconds(15));
        await using DiskProbe.Asset third = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));

        Assert.Equal(1, storage.OpenCount);
        Assert.NotSame(second, third);
        await second.DisposeAsync();
        Assert.Equal(7, third.Number);
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
        DiskProbe.Asset first = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));
        DiskProbe.Asset second = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));
        await using DiskProbe objectA = DiskProbe.Create(first);
        await using DiskProbe objectB = DiskProbe.Create(second);

        await first.DisposeAsync();
        await second.DisposeAsync();
        await loader.DisposeAsync();

        Assert.False(storage.WasClosed);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await objectA.ReadBytesAsync());
        await objectA.DisposeAsync();
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await objectB.ReadBytesAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id)));
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
        await using DiskProbe.Asset first = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));

        files.Save(id, 2, [2]);
        Assert.Equal(length, new FileInfo(files.Path).Length);
        File.SetLastWriteTimeUtc(files.Path, timestamp);
        await using DiskProbe.Asset second = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.NotEqual(first.SourceVersion, second.SourceVersion);
    }

    [Fact]
    public async Task PackedBytesRemainReadableAfterStorageIsClosed()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 5, [3, 1, 4, 1, 5]);
        string packPath = System.IO.Path.Combine(files.Directory, "test.pack");
        var builder = new AssetPackBuilder().AddAsset(id, DiskProbe.Asset.FileType,
            File.ReadAllBytes(files.Path), DiskProbe.Asset.SchemaHash);
        await using (var output = new FileStream(packPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            await builder.WriteAsync(output);
        AssetPack pack = await AssetPack.OpenAsync(new MemoryRangeSource(File.ReadAllBytes(packPath)), ownsSource: true);
        var storage = new AssetPackStorage([pack]);
        var loader = new AssetLoader(storage);
        DiskProbe.Asset asset = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));
        await using DiskProbe value = DiskProbe.Create(asset);
        await asset.DisposeAsync();
        await loader.DisposeAsync();
        await storage.DisposeAsync();

        Assert.Equal(new byte[] { 3, 1, 4, 1, 5 }, await value.ReadBytesAsync());
    }

    [Fact]
    public async Task FailureClosingOneSourceDoesNotSkipOtherSources()
    {
        using var files = new TestFiles();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        files.Save(firstId, 1, [1]);
        using (DiskProbe.Asset secondFile = new DiskProbe.Asset(new(secondId)) {
            Name = "second", Number = 2,
            PayloadKey = 1, PayloadLength = 1, Bytes = new byte[] { 2 },
        })
            files.Project.RegisterAsset(await secondFile.SaveAsync(System.IO.Path.Combine(files.Directory, "second.probe.asset")));
        await using var storage = new ClosingStorage(files.Project.CreateStorage(), firstId);
        var loader = new AssetLoader(storage);
        DiskProbe.Asset first = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(firstId));
        DiskProbe.Asset second = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(secondId));
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
            await OtherProbe.Asset.OpenAsync(loader, new AssetId<OtherProbe>(id)));

        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public async Task IncorrectSuffixDoesNotReplaceTheDestination()
    {
        using var files = new TestFiles();
        string destination = System.IO.Path.Combine(files.Directory, "bad.other.asset");
        byte[] original = [11, 22, 33, 44];
        File.WriteAllBytes(destination, original);
        using var asset = new DiskProbe.Asset { Name = "invalid-output" };
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
        using var first = await DiskProbe.Asset.OpenAsync(loader, new(id));
        using var second = await DiskProbe.Asset.OpenAsync(loader, new(id));
        Guid version = first.SourceVersion;
        using var branch = first.Duplicate();
        first.Number = 99;
        Assert.Equal(99, first.Number);
        Assert.Equal(17, branch.Number);
        Assert.Equal(17, second.Number);
        Assert.Equal(version, first.SourceVersion);
        using var reopened = await DiskProbe.Asset.OpenAsync(files.Path);
        Assert.Equal(17, reopened.Number);
    }

    [Fact]
    public async Task ByteWritesSeparateTheInputAndRetainedReaders()
    {
        using var files = new TestFiles();
        byte[] bytes = [1, 2, 3];
        using var asset = new DiskProbe.Asset { Number = 17, Name = "edited", PayloadKey = 1, Bytes = bytes };
        using var branch = asset.Duplicate();
        using var reader = asset.ReadBytes()!;
        using (var edit = asset.WriteBytes()) edit.Span[1] = 8;
        await asset.SaveAsync(files.Path);
        using var opened = await DiskProbe.Asset.OpenAsync(files.Path);
        using var contents = await opened.ReadBytesAsync();
        Assert.Equal(new byte[] { 1, 8, 3 }, contents.Memory.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, branch.Bytes!.Value.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.Memory.ToArray());
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
        using (DiskProbe.Asset otherFile = new DiskProbe.Asset(new(secondId))
        {
            Name = "other", Number = 2, PayloadKey = 1, PayloadLength = 1, Bytes = new byte[] { 2 },
        })
            files.Project.RegisterAsset(await otherFile.SaveAsync(System.IO.Path.Combine(files.Directory, "other.probe.asset")));
        var storage = new PausingStorage(files.Project.CreateStorage());
        storage.Continue.TrySetResult();
        await using var loader = new AssetLoader(storage, new()
        {
            MaxCachedFiles = maxFiles, MaxCachedRootBytes = rootBytes,
        });
        await using DiskProbe.Asset first = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(firstId));
        await using DiskProbe original = DiskProbe.Create(first);
        await using DiskProbe.Asset second = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(secondId));
        await using DiskProbe.Asset reopened = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(firstId));

        Assert.Equal(3, storage.OpenCount);
        await loader.DisposeAsync();
        Assert.Equal(new byte[] { 1 }, await original.ReadBytesAsync());
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task StorageWithoutVersionsDoesNotCreateAnUnchangingCacheEntry()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using var loader = new AssetLoader(new UnversionedStorage(files.Project.CreateStorage()));
        await using DiskProbe.Asset first = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));
        files.Save(id, 2, [2]);
        await using DiskProbe.Asset second = await DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id));

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.NotEqual(first.SourceVersion, second.SourceVersion);
    }

    [Fact]
    public async Task FileReadsRespectTheConfiguredConcurrencyLimit()
    {
        using var files = new TestFiles();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        files.Save(firstId, 1, [1]);
        using (DiskProbe.Asset secondFile = new DiskProbe.Asset(new(secondId)) {
            Name = "other", Number = 2, PayloadKey = 1, PayloadLength = 1, Bytes = new byte[] { 2 },
        })
            files.Project.RegisterAsset(await secondFile.SaveAsync(System.IO.Path.Combine(files.Directory, "other.probe.asset")));
        var storage = new PausingStorage(files.Project.CreateStorage());
        await using var loader = new AssetLoader(storage, new() { MaxConcurrentReads = 1 });
        Task<DiskProbe.Asset> first = DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(firstId)).AsTask();
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<DiskProbe.Asset> second = DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(secondId)).AsTask();
        Assert.Equal(1, storage.OpenCount);

        storage.Continue.TrySetResult();
        await using DiskProbe.Asset firstFile = await first;
        await using DiskProbe.Asset secondFileRead = await second;
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
        Task<DiskProbe.Asset> reading = DiskProbe.Asset.OpenAsync(loader, new AssetId<DiskProbe>(id)).AsTask();
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
        using var file = await DiskProbe.Asset.OpenAsync(files.Path);
        Guid sourceVersion = file.SourceVersion;
        file.Number = 99;
        AssetEntry saved = await file.SaveAsync(System.IO.Path.Combine(files.Directory, "copy.probe.asset"));
        using var copy = await DiskProbe.Asset.OpenAsync(saved.Path);
        Assert.Equal(99, copy.Number);
        Assert.Equal(saved.Version, copy.SourceVersion);
        Assert.Equal(sourceVersion, file.SourceVersion);
        Assert.NotEqual(sourceVersion, saved.Version);
        using var contents = await copy.ReadBytesAsync();
        Assert.Equal(bytes, contents.Memory.ToArray());
    }

    [Fact]
    public async Task CorruptChunkCopyDoesNotReplaceExistingDestination()
    {
        using var files = new TestFiles();
        files.Save(Guid.NewGuid(), 9, [1, 2, 3]);
        byte[] corrupt = File.ReadAllBytes(files.Path);
        await using (DiskProbe.Asset file = await DiskProbe.Asset.OpenAsync(files.Path))
        await using (BinaryDocument document = file.RetainSource()!)
        {
            BinaryChunkEntry? chunk = await document.FindChunkAsync(1);
            corrupt[checked((int)chunk!.Value.Offset)] ^= 0x40;
        }
        string brokenPath = System.IO.Path.Combine(files.Directory, "broken.probe.asset");
        File.WriteAllBytes(brokenPath, corrupt);
        string destination = System.IO.Path.Combine(files.Directory, "destination.probe.asset");
        byte[] original = [7, 8, 9];
        File.WriteAllBytes(destination, original);
        await using DiskProbe.Asset broken = await DiskProbe.Asset.OpenAsync(brokenPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => broken.SaveAsync(destination).AsTask());
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Empty(System.IO.Directory.EnumerateFiles(files.Directory, "*.tmp"));
    }

    [Fact]
    public async Task RetainingAndDuplicatingAnAssetDoesNotDecodeItsFieldsAgain()
    {
        using var files = new TestFiles();
        string path = System.IO.Path.Combine(files.Directory, "counted.counted.asset");
        using var created = new CountedAssetValue.Asset { Value = new(7) };
        await created.SaveAsync(path);
        CountedNumber.Reads = 0;
        using var opened = await CountedAssetValue.Asset.OpenAsync(path);
        Assert.Equal(1, CountedNumber.Reads);
        using var copy = opened.Duplicate();
        await using var source = opened.RetainSource();
        copy.Value = new(8);
        await copy.SaveAsync(System.IO.Path.Combine(files.Directory, "copy.counted.asset"));
        Assert.Equal(1, CountedNumber.Reads);
        Assert.Equal(7, opened.Value.Number);
        Assert.Equal(8, copy.Value.Number);
    }

    [Fact]
    public void AByteEditMustFinishBeforeTheAssetCanBeDuplicated()
    {
        using var asset = new DiskProbe.Asset { Bytes = new byte[] { 1 } };
        var edit = asset.WriteBytes();
        Assert.Throws<InvalidOperationException>(() => asset.Duplicate());
        edit.Span[0] = 2;
        edit.Dispose();
        using var copy = asset.Duplicate();
        Assert.Equal(2, copy.Bytes!.Value.Span[0]);
    }

    private sealed class TestFiles : IDisposable
    {
        internal TestFiles()
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "someengine-assets-consumer-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Project = new AssetProject(Directory, Array.Empty<IAssetImporter>());
            Path = System.IO.Path.Combine(Directory, "test.probe.asset");
        }

        internal string Directory { get; }
        internal string Path { get; }
        internal AssetProject Project { get; }

        internal void Save(Guid id, int number, byte[] bytes)
        {
            using DiskProbe.Asset asset = new DiskProbe.Asset(new(id)) {
                Name = "probe-" + number,
                Number = number,
                PayloadKey = 1,
                PayloadLength = checked((ulong)bytes.Length),
                Bytes = bytes,
            };
            Project.RegisterAsset((asset).SaveAsync(global::System.IO.Path.Combine(Project.RootPath, Path)).AsTask().GetAwaiter().GetResult());
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
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

[Asset(".probe.asset")]
public sealed partial class DiskProbe : IAsyncDisposable
{
    private static int _constructionCount;
    private BinaryDocument? _document;
    private ulong _payloadKey;
    public DiskProbe() => Interlocked.Increment(ref _constructionCount);
    private DiskProbe(BinaryDocument document, ulong payloadKey) : this() { _document = document; _payloadKey = payloadKey; }
    public string Name { get; set; } = "";
    public int Number { get; set; }
    public ulong PayloadKey { get; set; }
    public ulong PayloadLength { get; set; }
    [BinaryIgnore, BinaryChunk(nameof(PayloadKey), nameof(PayloadLength))]
    public ReadOnlyMemory<byte>? Bytes { get; set; }
    public static int ConstructionCount => Volatile.Read(ref _constructionCount);
    public static void ResetConstructionCount() => Interlocked.Exchange(ref _constructionCount, 0);
    public static DiskProbe Create(Asset asset)
    {
        using var copy = asset.Duplicate();
        return new(copy.RetainSource() ?? throw new InvalidOperationException("This probe needs a saved file."), copy.PayloadKey);
    }
    public static ValueTask<DiskProbe> CreateAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create(asset));
    }
    public async ValueTask<byte[]> ReadBytesAsync()
    {
        BinaryDocument document = Volatile.Read(ref _document) ?? throw new ObjectDisposedException(nameof(DiskProbe));
        using var chunk = await document.AcquireChunkAsync(_payloadKey);
        return chunk.Memory.ToArray();
    }
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _document, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
}

[Asset(".other.asset")]
public sealed partial class OtherProbe
{
    public string Name { get; set; } = "";
    public int Number { get; set; }
}

[Asset(".counted.asset")]
public sealed partial class CountedAssetValue
{
    public string Name { get; set; } = "";
    public CountedNumber Value { get; set; }
}

[BinaryContract]
public readonly record struct CountedNumber(int Number) : IBinaryContract<CountedNumber>
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
