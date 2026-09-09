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
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        string path = Path.Combine(directory.FullName, "assets", "Textures", "default_white.texture.asset");
        await using Asset<SomeEngine.Assets.Schema.Texture> file =
            await AssetLoader.OpenFileAsync<SomeEngine.Assets.Schema.Texture>(path);
        var shape = SomeEngine.Assets.Schema.Texture.ReadDescription(file);
        Assert.Equal(1u, shape.Width);
        Assert.Equal(1u, shape.Height);
        Assert.Equal(1u, shape.MipLevelCount);
        var tile = Assert.Single(SomeEngine.Assets.Schema.Texture.ReadMipTiles(file));
        Assert.Null(tile.Payload);
        Assert.NotEqual(0ul, tile.ChunkKey);
        tile.Width = 99;
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

        await using Asset<DiskProbe> asset = await loader.OpenAsync(new AssetId<DiskProbe>(id));

        Assert.Equal(id, asset.Id.Value);
        Assert.Equal("probe-17", asset.Name);
        Assert.Equal(17, asset.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.Equal(0, DiskProbe.ConstructionCount);
    }

    [Fact]
    public async Task SameLoaderSeesNewFileWhileTheOldOpenKeepsItsContent()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        await using Asset<DiskProbe> first = await loader.OpenAsync(new AssetId<DiskProbe>(id));
        Guid firstVersion = first.Version;

        files.Save(id, 2, [2, 3]);
        await using Asset<DiskProbe> second = await loader.OpenAsync(new AssetId<DiskProbe>(id));

        Assert.NotSame(first, second);
        Assert.Equal(1, first.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.Equal(2, second.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.NotEqual(firstVersion, second.Version);
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
        await using Asset<DiskProbe> first = await AssetLoader.OpenFileAsync<DiskProbe>(files.Path);
        Guid firstVersion = first.Version;
        Assert.NotEqual(Guid.Empty, firstVersion);

        files.Save(id, 2, [2]);
        await using Asset<DiskProbe> second = await AssetLoader.OpenFileAsync<DiskProbe>(files.Path);

        Assert.NotEqual(Guid.Empty, second.Version);
        Assert.NotEqual(firstVersion, second.Version);
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
        Task<Asset<DiskProbe>> canceled = loader.OpenAsync(new AssetId<DiskProbe>(id), cancellation.Token).AsTask();
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<Asset<DiskProbe>> other = loader.OpenAsync(new AssetId<DiskProbe>(id)).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);
        storage.Continue.TrySetResult();
        await using Asset<DiskProbe> second = await other.WaitAsync(TimeSpan.FromSeconds(15));
        await using Asset<DiskProbe> third = await loader.OpenAsync(new AssetId<DiskProbe>(id));

        Assert.Equal(1, storage.OpenCount);
        Assert.NotSame(second, third);
        await second.DisposeAsync();
        Assert.Equal(7, third.GetFieldsGenerated<DiskProbeFields>().Number);
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
        Asset<DiskProbe> first = await loader.OpenAsync(new AssetId<DiskProbe>(id));
        Asset<DiskProbe> second = await loader.OpenAsync(new AssetId<DiskProbe>(id));
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
            await loader.OpenAsync(new AssetId<DiskProbe>(id)));
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
        await using Asset<DiskProbe> first = await loader.OpenAsync(new AssetId<DiskProbe>(id));

        files.Save(id, 2, [2]);
        Assert.Equal(length, new FileInfo(files.Path).Length);
        File.SetLastWriteTimeUtc(files.Path, timestamp);
        await using Asset<DiskProbe> second = await loader.OpenAsync(new AssetId<DiskProbe>(id));

        Assert.Equal(1, first.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.Equal(2, second.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.NotEqual(first.Version, second.Version);
    }

    [Fact]
    public async Task PackedBytesRemainReadableAfterStorageIsClosed()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 5, [3, 1, 4, 1, 5]);
        string packPath = System.IO.Path.Combine(files.Directory, "test.pack");
        var builder = new AssetPackBuilder().AddAsset(id, Asset<DiskProbe>.FileType,
            File.ReadAllBytes(files.Path), Asset<DiskProbe>.SchemaHash);
        await using (var output = new FileStream(packPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            await builder.WriteAsync(output);
        AssetPack pack = await AssetPack.OpenAsync(new MemoryRangeSource(File.ReadAllBytes(packPath)), ownsSource: true);
        var storage = new AssetPackStorage([pack]);
        var loader = new AssetLoader(storage);
        Asset<DiskProbe> asset = await loader.OpenAsync(new AssetId<DiskProbe>(id));
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
        using (Asset<DiskProbe> secondFile = new DiskProbeFields
        {
            AssetGuid = secondId.ToString(), Name = "second", Number = 2,
            PayloadKey = 1, PayloadLength = 1, Bytes = new byte[] { 2 },
        }.ToAsset())
            files.Project.CreateAsset(System.IO.Path.Combine(files.Directory, "second.probe.asset"), secondFile);
        await using var storage = new ClosingStorage(files.Project.CreateStorage(), firstId);
        var loader = new AssetLoader(storage);
        Asset<DiskProbe> first = await loader.OpenAsync(new AssetId<DiskProbe>(firstId));
        Asset<DiskProbe> second = await loader.OpenAsync(new AssetId<DiskProbe>(secondId));
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
            await loader.OpenAsync(new AssetId<WrongWriterProbe>(id)));

        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public void IncorrectWriterRootDoesNotReplaceTheDestination()
    {
        using var files = new TestFiles();
        string destination = System.IO.Path.Combine(files.Directory, "bad.badprobe.asset");
        byte[] original = [11, 22, 33, 44];
        File.WriteAllBytes(destination, original);
        using Asset<WrongWriterProbe> asset = new WrongWriterFields
        {
            AssetGuid = Guid.NewGuid().ToString(),
            Name = "invalid-writer",
        }.ToAsset();

        Assert.Throws<InvalidDataException>(() => AssetWriter.Write(asset, destination));
        Assert.Equal(original, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task EditingReturnedFieldsDoesNotChangeAnyOpenedFile()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 17, [1, 2, 3]);
        await using var loader = new AssetLoader(files.Project.CreateStorage());
        await using Asset<DiskProbe> first = await loader.OpenAsync(new AssetId<DiskProbe>(id));
        await using Asset<DiskProbe> second = await loader.OpenAsync(new AssetId<DiskProbe>(id));
        Guid version = first.Version;

        first.GetFieldsGenerated<DiskProbeFields>().Number = 99;
        await using BinaryDocument<DiskProbeFields> document = first.OpenDocumentGenerated<DiskProbeFields>()!;
        document.Root.Number = 88;
        await using BinaryDocument<DiskProbeFields> anotherDocument = document.Duplicate();

        Assert.Equal(17, anotherDocument.Root.Number);
        Assert.Equal(17, first.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.Equal(17, second.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.Equal(version, first.Version);
    }

    [Fact]
    public async Task CapturingFileFieldsDoesNotBorrowMutableInput()
    {
        using var files = new TestFiles();
        byte[] bytes = [1, 2, 3];
        var fields = new DiskProbeFields
        {
            Number = 17, Name = "captured", PayloadKey = 1, PayloadLength = 3, Bytes = bytes,
        };
        using Asset<DiskProbe> asset = fields.ToAsset();
        fields.Number = 99;
        bytes[0] = 9;
        asset.GetFieldsGenerated<DiskProbeFields>().Bytes.Span[1] = 8;
        await AssetWriter.WriteAsync(asset, files.Path);

        await using Asset<DiskProbe> opened = await AssetLoader.OpenFileAsync<DiskProbe>(files.Path);
        Assert.Equal(17, opened.GetFieldsGenerated<DiskProbeFields>().Number);
        await using DiskProbe value = DiskProbe.Create(opened);
        Assert.Equal(new byte[] { 1, 2, 3 }, await value.ReadBytesAsync());
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
        using (Asset<DiskProbe> otherFile = new DiskProbeFields
        {
            Name = "other", Number = 2, PayloadKey = 1, PayloadLength = 1, Bytes = new byte[] { 2 },
        }.ToAsset(secondId))
            files.Project.CreateAsset(System.IO.Path.Combine(files.Directory, "other.probe.asset"), otherFile);
        var storage = new PausingStorage(files.Project.CreateStorage());
        storage.Continue.TrySetResult();
        await using var loader = new AssetLoader(storage, new()
        {
            MaxCachedFiles = maxFiles, MaxCachedRootBytes = rootBytes,
        });
        await using Asset<DiskProbe> first = await loader.OpenAsync(new AssetId<DiskProbe>(firstId));
        await using DiskProbe original = DiskProbe.Create(first);
        await using Asset<DiskProbe> second = await loader.OpenAsync(new AssetId<DiskProbe>(secondId));
        await using Asset<DiskProbe> reopened = await loader.OpenAsync(new AssetId<DiskProbe>(firstId));

        Assert.Equal(3, storage.OpenCount);
        await loader.DisposeAsync();
        Assert.Equal(new byte[] { 1 }, await original.ReadBytesAsync());
        Assert.Equal(2, second.GetFieldsGenerated<DiskProbeFields>().Number);
    }

    [Fact]
    public async Task StorageWithoutVersionsDoesNotCreateAnUnchangingCacheEntry()
    {
        using var files = new TestFiles();
        Guid id = Guid.NewGuid();
        files.Save(id, 1, [1]);
        await using var loader = new AssetLoader(new UnversionedStorage(files.Project.CreateStorage()));
        await using Asset<DiskProbe> first = await loader.OpenAsync(new AssetId<DiskProbe>(id));
        files.Save(id, 2, [2]);
        await using Asset<DiskProbe> second = await loader.OpenAsync(new AssetId<DiskProbe>(id));

        Assert.Equal(1, first.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.Equal(2, second.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.NotEqual(first.Version, second.Version);
    }

    [Fact]
    public async Task FileReadsRespectTheConfiguredConcurrencyLimit()
    {
        using var files = new TestFiles();
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        files.Save(firstId, 1, [1]);
        using (Asset<DiskProbe> secondFile = new DiskProbeFields
        {
            Name = "other", Number = 2, PayloadKey = 1, PayloadLength = 1, Bytes = new byte[] { 2 },
        }.ToAsset(secondId))
            files.Project.CreateAsset(System.IO.Path.Combine(files.Directory, "other.probe.asset"), secondFile);
        var storage = new PausingStorage(files.Project.CreateStorage());
        await using var loader = new AssetLoader(storage, new() { MaxConcurrentReads = 1 });
        Task<Asset<DiskProbe>> first = loader.OpenAsync(new AssetId<DiskProbe>(firstId)).AsTask();
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Task<Asset<DiskProbe>> second = loader.OpenAsync(new AssetId<DiskProbe>(secondId)).AsTask();
        Assert.Equal(1, storage.OpenCount);

        storage.Continue.TrySetResult();
        await using Asset<DiskProbe> firstFile = await first;
        await using Asset<DiskProbe> secondFileRead = await second;
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
        Task<Asset<DiskProbe>> reading = loader.OpenAsync(new AssetId<DiskProbe>(id)).AsTask();
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
    public async Task WritingAnOpenedFilePreservesItsContentsAndVersion()
    {
        using var files = new TestFiles();
        files.Save(Guid.NewGuid(), 31, Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray());
        await using Asset<DiskProbe> file = await AssetLoader.OpenFileAsync<DiskProbe>(files.Path);
        string copyPath = System.IO.Path.Combine(files.Directory, "copy.probe.asset");
        file.GetFieldsGenerated<DiskProbeFields>().Number = 99;
        await AssetWriter.WriteAsync(file, copyPath);
        await using Asset<DiskProbe> copy = await AssetLoader.OpenFileAsync<DiskProbe>(copyPath);
        Assert.Equal(file.Version, copy.Version);
        Assert.Equal(31, copy.GetFieldsGenerated<DiskProbeFields>().Number);
        Assert.Equal(File.ReadAllBytes(files.Path), File.ReadAllBytes(copyPath));
    }

    [Fact]
    public async Task CorruptChunkCopyDoesNotReplaceExistingDestination()
    {
        using var files = new TestFiles();
        files.Save(Guid.NewGuid(), 9, [1, 2, 3]);
        byte[] corrupt = File.ReadAllBytes(files.Path);
        await using (Asset<DiskProbe> file = await AssetLoader.OpenFileAsync<DiskProbe>(files.Path))
        await using (BinaryDocument<DiskProbeFields> document = file.OpenDocumentGenerated<DiskProbeFields>()!)
        {
            BinaryChunkEntry? chunk = await document.FindChunkAsync(1);
            corrupt[checked((int)chunk!.Value.Offset)] ^= 0x40;
        }
        string brokenPath = System.IO.Path.Combine(files.Directory, "broken.probe.asset");
        File.WriteAllBytes(brokenPath, corrupt);
        string destination = System.IO.Path.Combine(files.Directory, "destination.probe.asset");
        byte[] original = [7, 8, 9];
        File.WriteAllBytes(destination, original);
        await using Asset<DiskProbe> broken = await AssetLoader.OpenFileAsync<DiskProbe>(brokenPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => AssetWriter.WriteAsync(broken, destination).AsTask());
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Empty(System.IO.Directory.EnumerateFiles(files.Directory, "*.tmp"));
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
            using Asset<DiskProbe> asset = new DiskProbeFields
            {
                AssetGuid = id.ToString(),
                Name = "probe-" + number,
                Number = number,
                PayloadKey = 1,
                PayloadLength = checked((ulong)bytes.Length),
                Bytes = bytes,
            }.ToAsset();
            Project.CreateAsset(Path, asset);
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

public sealed class DiskProbe : IAsyncDisposable
{
    private static int _constructionCount;
    private BinaryDocument<DiskProbeFields>? _document;

    private DiskProbe(BinaryDocument<DiskProbeFields> document)
    {
        _document = document;
        Interlocked.Increment(ref _constructionCount);
    }

    public static int ConstructionCount => Volatile.Read(ref _constructionCount);
    public static void ResetConstructionCount() => Interlocked.Exchange(ref _constructionCount, 0);

    public static DiskProbe Create(Asset<DiskProbe> asset)
        => new(asset.OpenDocumentGenerated<DiskProbeFields>()
            ?? throw new InvalidOperationException("This probe needs a saved file."));

    public async ValueTask<byte[]> ReadBytesAsync()
    {
        BinaryDocument<DiskProbeFields> document = Volatile.Read(ref _document)
            ?? throw new ObjectDisposedException(nameof(DiskProbe));
        using var chunk = await document.AcquireChunkAsync(document.Root.PayloadKey);
        return chunk.Memory.ToArray();
    }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _document, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
}

[BinaryContract]
[Asset(typeof(DiskProbe), ".probe.asset")]
public sealed partial class DiskProbeFields
{
    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public int Number { get; set; }
    public ulong PayloadKey { get; set; }
    public ulong PayloadLength { get; set; }
    [BinaryIgnore]
    public Memory<byte> Bytes { get; set; }

    internal static BinaryDocumentWriter CreateWriter(DiskProbeFields fields)
        => BinaryDocumentWriter.Create(fields).AddChunk(fields.PayloadKey, fields.Bytes);
}

public sealed class WrongWriterProbe;

[BinaryContract]
[Asset(typeof(WrongWriterProbe), ".badprobe.asset")]
public sealed partial class WrongWriterFields
{
    public string? AssetGuid { get; set; }
    public string? Name { get; set; }

    internal static BinaryDocumentWriter CreateWriter(WrongWriterFields fields)
        => BinaryDocumentWriter.Create(new DifferentFileFields { Number = 123 });
}

[BinaryContract]
public sealed partial class DifferentFileFields
{
    public int Number { get; set; }
}