using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public partial class Mesh : IDisposable, IAsyncDisposable
{
    private readonly object _contentGate = new();
    private MeshPayloadSource? _payloadSource;
    private int _disposed;

    public ulong Revision { get; private set; } = 1;
    public bool IsStreamed => Volatile.Read(ref _payloadSource) is not null;

    internal bool TryRetainPayloadSource(out MeshPayloadSource? source)
    {
        lock (_contentGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            source = _payloadSource?.Retain();
            return source is not null;
        }
    }

    internal bool TryBorrowPayloadSource(out MeshPayloadSource? source)
    {
        lock (_contentGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            source = _payloadSource;
            return source is not null;
        }
    }

    internal void AttachPayloadSource(MeshPayloadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_contentGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_payloadSource is not null)
                throw new InvalidOperationException("Mesh already has a streamed payload source.");
            _payloadSource = source;
        }
    }

    public static async ValueTask<Mesh> CreateAsync(Mesh.Asset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        MeshData fields = asset.CopyValues();
        fields.ValidateMaterialReferences(asset.SourcePath);
        if (fields.Payload is { } payload)
        {
            MeshData.PopulatePayloadIntegrity(fields, payload.Span);
            fields.PayloadKey = MeshData.PayloadChunkKey;
            fields.PayloadLength = checked((ulong)payload.Length);
            fields.AssetGuid = null;
            return new Mesh { _content = fields };
        }
        BinaryDocument? document = asset.RetainSource();
        if (document is null)
        {
            fields.Dispose();
            throw new InvalidDataException("Mesh asset has no payload.");
        }
        return await MeshData.OpenStreamedAsync(document, fields, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes this mesh at the caller's world update boundary; existing page reads retain their bytes.</summary>
    public async ValueTask UpdateAsync(Mesh.Asset asset, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using Mesh replacement = await CreateAsync(asset, cancellationToken).ConfigureAwait(false);
        await ApplyReloadAsync(this, replacement, cancellationToken).ConfigureAwait(false);
    }

    internal static Mesh FromFields(MeshData fields)
    {
        MeshData content = fields.CopyFields();
        content.AssetGuid = null;
        return new Mesh { _content = content };
    }

    internal static ValueTask ApplyReloadAsync(Mesh current, Mesh replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        MeshPayloadSource? previous;
        lock (current._contentGate)
        {
            ObjectDisposedException.ThrowIf(current._disposed != 0, current);
            ulong revision = checked(current.Revision + 1);
            MeshPayloadSource? next = Interlocked.Exchange(ref replacement._payloadSource, null);
            previous = Interlocked.Exchange(ref current._payloadSource, next);
            current._content.Dispose();
            current._content = replacement._content;
            replacement._content = new();
            current.Revision = revision;
        }
        previous?.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        MeshPayloadSource? source;
        lock (_contentGate)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            source = Interlocked.Exchange(ref _payloadSource, null);
        }
        try { source?.Dispose(); }
        finally { _content.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
