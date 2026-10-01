using SomeEngine.Assets;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ReadingSample : IDisposable
{
    public static int Created;
    public ReadingSample() : this("sample", 0) { }
    public ReadingSample(string name, int number) { Name = name; Number = number; Interlocked.Increment(ref Created); }
    public string Name { get; set; } = "sample";
    public int Number { get; set; }
    public bool IsDisposed { get; private set; }
    public void Update(string name, int number)
    {
        if (number < 0) throw new InvalidDataException("A sample number must be nonnegative.");
        Name = name;
        Number = number;
    }
    public void Dispose() => IsDisposed = true;
}

[Asset(".reading.asset", SchemaName = "SomeEngine.Assets.Tests.Assets.ReadingSampleAsset"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class ReadingSampleAsset : Asset
{
    public new System.Collections.Immutable.ImmutableArray<AssetBlockDescription> BlockDescriptions
    { get => base.BlockDescriptions; private set => base.BlockDescriptions = value; }
    partial void Initialize() => Name = "sample";
}

[AssetBlock("SomeEngine.Assets.Tests.Assets.ReadingSampleBlock"), BinarySerializable, BinaryFields(nameof(Number), nameof(Dependency))]
public sealed partial class ReadingSampleBlock : AssetBlock
{
    public partial int Number { get; set; }
    public partial AssetId<ReadingSampleAsset> Dependency { get; set; }
    public ReadingSample Create(string name)
    {
        using var copy = Duplicate();
        return new(name, copy.Number);
    }
    public void Update(ReadingSample value, string name)
    {
        using var copy = Duplicate();
        value.Update(name, copy.Number);
    }
}

internal sealed class AssetTestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SomeEngine-AssetTests-" + Guid.NewGuid().ToString("N"));
    internal AssetTestDirectory() => Directory.CreateDirectory(Path);
    internal string File(string name) => System.IO.Path.Combine(Path, name);
    internal string WriteSample(Guid id, int number, string name = "sample")
    {
        string path = File(name + ".reading.asset");
        using var asset = new ReadingSampleAsset(id) { Name = name };
        using var content = new ReadingSampleBlock { Number = number };
        asset.SetBlock(content);
        asset.SaveAsync(path).AsTask().GetAwaiter().GetResult();
        return path;
    }
    public void Dispose() => Directory.Delete(Path, true);
}

internal sealed class ReadingStorage : IAssetStorage, IAsyncDisposable
{
    private readonly Dictionary<Guid, (AssetEntry Entry, byte[] Bytes)> _files = [];
    private readonly TaskCompletionSource _readGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Opens;
    internal int RangesClosed;
    internal int StorageClosed;
    internal ReadingStorage(bool block = false) { if (!block) _readGate.SetResult(); }
    internal void ReleaseReads() => _readGate.TrySetResult();
    internal void Publish(Guid id, string path, string? type = null, ulong? schema = null, bool corrupt = false)
    {
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        var source = new MemoryRangeSource(bytes);
        Guid version = SomeEngine.Serialization.Containers.BinaryDocument.ReadVersionAsync(source).AsTask().GetAwaiter().GetResult();
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (corrupt) bytes[0] ^= 0xFF;
        _files[id] = (new AssetEntry(id, type ?? ReadingSampleAsset.FileType,
            schema ?? ReadingSampleAsset.SchemaHash, version), bytes);
    }
    public bool TryFind(Guid id, out AssetEntry entry)
    {
        if (_files.TryGetValue(id, out var file)) { entry = file.Entry; return true; }
        entry = default; return false;
    }
    public ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
    {
        var file = _files[entry.AssetGuid];
        if (entry != file.Entry) throw new IOException("File version changed.");
        Interlocked.Increment(ref Opens);
        return ValueTask.FromResult<IRangeSource>(new Bytes(this, file.Bytes));
    }
    public ValueTask DisposeAsync() { Interlocked.Increment(ref StorageClosed); return ValueTask.CompletedTask; }
    private sealed class Bytes(ReadingStorage storage, byte[] bytes) : IRangeSource
    {
        private readonly MemoryRangeSource _source = new(bytes);
        private int _closed;
        public long Length => _source.Length;
        public string Generation => _source.Generation;
        public bool LeasesAreImmutable => true;
        public bool RetainsResidentBacking => true;
        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
        {
            storage.ReadStarted.TrySetResult();
            await storage._readGate.Task.WaitAsync(cancellationToken);
            return await _source.AcquireAsync(offset, length, cancellationToken);
        }
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
            => _source.ReadExactlyAsync(offset, destination, cancellationToken);
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Interlocked.Increment(ref storage.RangesClosed);
            return _source.DisposeAsync();
        }
    }
}
