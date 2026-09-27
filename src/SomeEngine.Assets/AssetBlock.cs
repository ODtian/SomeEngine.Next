using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AssetBlockAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

public interface IAssetBlock<T> where T : AssetBlock, IAssetBlock<T>
{
    static abstract Guid BlockTypeId { get; }

    /// <summary>Reads the block's initial data. Blocks created only in memory need no reader.</summary>
    static virtual ValueTask<T> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"Block {T.BlockTypeId} has no persisted reader.");
}

public interface IAssetBlockWriter
{
    ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default);
}

public abstract class AssetBlock : IDisposable, IAsyncDisposable
{
    private bool _disposed;
    protected PageAccess Access { get; }
    [BinaryIgnore] public abstract Guid BlockId { get; }

    protected AssetBlock(PageAccess? access = null) => Access = access ?? new();

    protected AssetBlock(AssetBlock source, PageAccess? access = null)
    {
        Access = access ?? new();
        lock (source.Access.SyncRoot) source.RequireIdle();
    }

    protected void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Access.RequireIdle();
    }

    public abstract AssetBlock Duplicate();
    public abstract AssetBlock Duplicate(PageAccess access);
    public virtual IEnumerable<Guid> References() => [];
    protected virtual void ValidateContent() { }
    protected virtual void ReleaseContent() { }

    internal void ValidateForWrite() => ValidateContent();

    public void Dispose()
    {
        lock (Access.SyncRoot)
        {
            if (_disposed) return;
            RequireIdle();
            _disposed = true;
            ReleaseContent();
        }
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

public abstract class AssetPageBlock : AssetBlock
{
    private AssetBlockSource? _source;
    [BinaryIgnore] public PageArena Pages { get; }
    [BinaryIgnore] public PageBufferMap Chunks { get; }

    protected AssetPageBlock(PageArena? pages = null, PageAccess? access = null) : base(access)
    {
        Pages = pages ?? PageArena.Shared;
        Chunks = new(Pages, Access);
    }

    protected AssetPageBlock(AssetPageBlock source, PageAccess? access = null) : base(source, access)
    {
        lock (source.Access.SyncRoot)
        {
            source.RequireIdle();
            Pages = source.Pages;
            Chunks = source.Chunks.Duplicate(Access);
            _source = source._source?.Retain();
        }
    }

    protected void AttachSource(AssetBlockSource source)
    {
        lock (Access.SyncRoot)
        {
            RequireIdle();
            if (source.Description.BlockTypeId != BlockId)
                throw new InvalidDataException("The source describes a different block type.");
            if (_source is not null) throw new InvalidOperationException("The block already has a source.");
            _source = source.Retain();
        }
    }

    protected internal AssetBlockSource? RetainSource()
    {
        lock (Access.SyncRoot) { RequireIdle(); return _source?.Retain(); }
    }

    internal IReadOnlyCollection<ulong> ContentKeys
    {
        get
        {
            lock (Access.SyncRoot)
            {
                RequireIdle();
                return _source is null ? Chunks.Keys.ToArray() : Chunks.Keys.Union(_source.Description.ChunkKeys).ToArray();
            }
        }
    }

    public async ValueTask<PageBuffer> AcquireBufferAsync(ulong key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssetBlockSource source;
        lock (Access.SyncRoot)
        {
            RequireIdle();
            if (Chunks.Contains(key)) return Chunks.DuplicateBuffer(key);
            source = _source?.Retain() ?? throw new KeyNotFoundException($"Block chunk {key:X16} has no source.");
        }
        using (source) return await source.AcquireBufferAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AcquireChunkForWriteAsync(ulong key, CancellationToken cancellationToken = default)
    {
        using PageBuffer bytes = await AcquireBufferAsync(key, cancellationToken).ConfigureAwait(false);
        lock (Access.SyncRoot)
        {
            RequireIdle();
            if (!Chunks.Contains(key)) Chunks.Set(key, bytes);
        }
    }

    protected virtual async ValueTask WriteChunksAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        using AssetBlockSource? source = RetainSource();
        if (source is not null)
            foreach (ulong key in source.Description.ChunkKeys)
                if (!Chunks.Contains(key)) await writer.CopyChunkAsync(source, key, cancellationToken).ConfigureAwait(false);
        foreach (ulong key in Chunks.Keys) writer.WriteChunk(key, Chunks);
    }

    protected override void ReleaseContent()
    {
        try { Chunks.Dispose(); }
        finally
        {
            _source?.Dispose();
            _source = null;
            base.ReleaseContent();
        }
    }
}
