using SomeEngine.Serialization;

namespace SomeEngine.Assets;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetBlockAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

public interface IAssetBlock<T> where T : AssetBlock, IAssetBlock<T>
{
    static abstract Guid BlockTypeId { get; }
    /// <summary>Whether this block is included when its asset is saved.</summary>
    static virtual bool Persistent => true;
    static virtual ValueTask<T> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"Block {T.BlockTypeId} has no persisted reader.");
}

public interface IAssetBlockWriter
{
    ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default);
}

/// <summary>Business data and its explicit file access. Managed values have ordinary GC lifetimes.</summary>
public abstract class AssetBlock : IDisposable, IAsyncDisposable
{
    private AssetBlockSource? _source;
    private bool _disposed;
    protected object SyncRoot { get; }
    [BinaryIgnore] public abstract Guid BlockId { get; }
    [BinaryIgnore] public Guid DataVersion { get; private set; } = Guid.NewGuid();
    /// <summary>Locally supplied immutable bytes. Values must not be modified after assignment.</summary>
    private readonly Dictionary<ulong, ReadOnlyMemory<byte>> _chunks = [];
    [BinaryIgnore] public IReadOnlyDictionary<ulong, ReadOnlyMemory<byte>> Chunks => _chunks;

    protected AssetBlock(object? syncRoot = null) => SyncRoot = syncRoot ?? new();

    protected AssetBlock(AssetBlock source, object? syncRoot = null)
    {
        SyncRoot = syncRoot ?? new();
        lock (source.SyncRoot)
        {
            source.RequireIdle();
            DataVersion = source.DataVersion;
            foreach (var chunk in source.Chunks) _chunks.Add(chunk.Key, chunk.Value);
            _source = source._source?.Retain();
        }
    }

    protected void RequireIdle() => ObjectDisposedException.ThrowIf(_disposed, this);
    protected void MarkChanged() => DataVersion = Guid.NewGuid();
    public abstract AssetBlock Duplicate();
    public abstract AssetBlock Duplicate(object syncRoot);
    public virtual IEnumerable<Guid> References() => [];
    protected virtual void ValidateContent() { }
    internal void ValidateForWrite() => ValidateContent();

    protected void AttachSource(AssetBlockSource source)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            if (source.Description.BlockTypeId != BlockId)
                throw new InvalidDataException("The source describes a different block type.");
            if (_source is not null) throw new InvalidOperationException("The block already has a source.");
            _source = source.Retain();
            DataVersion = source.Version;
        }
    }

    protected internal AssetBlockSource? RetainSource()
    {
        lock (SyncRoot) { RequireIdle(); return _source?.Retain(); }
    }

    internal IReadOnlyCollection<ulong> ContentKeys
    {
        get
        {
            lock (SyncRoot)
            {
                RequireIdle();
                return _source is null ? Chunks.Keys.ToArray() : Chunks.Keys.Union(_source.Description.ChunkKeys).ToArray();
            }
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(ulong key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssetBlockSource source;
        lock (SyncRoot)
        {
            RequireIdle();
            if (Chunks.TryGetValue(key, out var bytes)) return bytes;
            source = _source?.Retain() ?? throw new KeyNotFoundException($"Block chunk {key:X16} has no source.");
        }
        using (source) return await source.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public void SetBytes(ulong key, ReadOnlyMemory<byte> bytes)
    {
        lock (SyncRoot) { RequireIdle(); _chunks[key] = bytes; MarkChanged(); }
    }

    public bool RemoveBytes(ulong key)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            if (!_chunks.Remove(key)) return false;
            MarkChanged();
            return true;
        }
    }

    protected virtual async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        using AssetBlockSource? source = RetainSource();
        if (source is not null)
            foreach (ulong key in source.Description.ChunkKeys)
                if (!Chunks.ContainsKey(key)) await writer.CopyChunkAsync(source, key, cancellationToken).ConfigureAwait(false);
        foreach (var chunk in Chunks) writer.WriteChunk(chunk.Key, chunk.Value);
    }

    protected virtual void ReleaseContent()
    {
        _chunks.Clear();
        _source?.Dispose();
        _source = null;
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseContent();
        }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
