using SomeEngine.Assets;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ReadingSample : IDisposable
{
    public static int Created;
    public ReadingSample(string name, int number) { Name = name; Number = number; Interlocked.Increment(ref Created); }
    public string Name { get; private set; }
    public int Number { get; private set; }
    public bool IsDisposed { get; private set; }
    public static ReadingSample Create(Asset<ReadingSample> asset)
    {
        ReadingSampleData fields = asset.GetFieldsGenerated<ReadingSampleData>();
        return new(fields.Name, fields.Number);
    }
    public void Update(Asset<ReadingSample> asset)
    {
        ReadingSampleData fields = asset.GetFieldsGenerated<ReadingSampleData>();
        if (fields.Number < 0) throw new InvalidDataException("A sample number must be nonnegative.");
        Name = fields.Name;
        Number = fields.Number;
    }
    public void Dispose() => IsDisposed = true;
}

[Asset(typeof(ReadingSample), ".reading.asset")]
[BinaryContract]
public sealed partial class ReadingSampleData
{
    public string? AssetGuid { get; set; }
    public string Name { get; set; } = "sample";
    public int Number { get; set; }
    public Guid Dependency { get; set; }
    internal IReadOnlyList<Guid> GetDependencies(string path) => Dependency == Guid.Empty ? [] : [Dependency];
}

internal sealed class AssetTestDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SomeEngine-AssetTests-" + Guid.NewGuid().ToString("N"));
    internal AssetTestDirectory() => Directory.CreateDirectory(Path);
    internal string File(string name) => System.IO.Path.Combine(Path, name);
    internal string WriteSample(Guid id, int number, string name = "sample")
    {
        string path = File(name + ".reading.asset");
        using Asset<ReadingSample> asset = new ReadingSampleData { AssetGuid = id.ToString(), Name = name, Number = number }.ToAsset();
        AssetWriter.Write(asset, path);
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
        if (corrupt) bytes[0] ^= 0xFF;
        _files[id] = (new AssetEntry(id, type ?? Asset<ReadingSample>.FileType,
            schema ?? Asset<ReadingSample>.SchemaHash, Guid.NewGuid()), bytes);
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
