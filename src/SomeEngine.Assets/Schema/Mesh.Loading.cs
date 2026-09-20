using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public partial class Mesh : IDisposable, IAsyncDisposable
{
    private readonly object _contentGate = new();
    private MeshPayloadSource? _payloadSource;
    private MeshData? _data;
    private readonly List<(WeakReference<MeshData> Data, MeshPayloadSource Source)> _retiredSources = [];
    private bool _disposed;

    public event Action<Mesh>? Changed;

    /// <summary>Returns the current immutable content. Reload publishes a different data object.</summary>
    public MeshData CaptureData()
    {
        lock (_contentGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _data ??= new MeshData(this, _payloadSource);
        }
    }

    internal bool TryRetainPayloadSource(MeshData data, out MeshPayloadSource? source)
    {
        lock (_contentGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            source = data.Source?.Retain();
            return source is not null;
        }
    }

    [SomeEngine.Serialization.BinaryIgnore]
    public ulong Revision { get; private set; } = 1;

    [SomeEngine.Serialization.BinaryIgnore]
    public bool IsStreamed => Volatile.Read(ref _payloadSource) is not null;

    internal void AttachPayloadSource(MeshPayloadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Interlocked.CompareExchange(ref _payloadSource, source, null) is not null)
            throw new InvalidOperationException("Mesh already owns a streamed payload source.");
    }

    internal static async ValueTask<Mesh> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BinaryDocument<Mesh> document = await context
            .OpenAsync<Mesh>()
            .ConfigureAwait(false);
        Mesh root = await OpenStreamedAsync(document, cancellationToken).ConfigureAwait(false);
        return context.Transfer(document, root);
    }

    internal static ValueTask ApplyReloadAsync(
        Mesh current,
        Mesh replacement,
        AssetUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();

        lock (current._contentGate)
        {
            ObjectDisposedException.ThrowIf(current._disposed, current);
            ulong revision = checked(current.Revision + 1);
            current.CollectUnusedSources();
            MeshPayloadSource? previous = current._payloadSource;
            if (previous is not null)
            {
                if (current._data is { } data)
                    current._retiredSources.Add((new WeakReference<MeshData>(data), previous));
            }
            current._payloadSource = Interlocked.Exchange(ref replacement._payloadSource,
                current._data is null ? previous : null);
            current.AssetGuid = replacement.AssetGuid;
            current.Name = replacement.Name;
            current.Bounds = replacement.Bounds;
            current.VertexEncoding = replacement.VertexEncoding;
            current.PayloadKey = replacement.PayloadKey;
            current.PayloadLength = replacement.PayloadLength;
            current.Payload = replacement.Payload;
            current.BvhOffset = replacement.BvhOffset;
            current.PageDigests = replacement.PageDigests;
            current.BvhLength = replacement.BvhLength;
            current.BvhSha256 = replacement.BvhSha256;
            current.Regions = replacement.Regions;
            current.MaterialGuids = replacement.MaterialGuids;
            current.Revision = revision;
            current._data = null;
        }
        update.Accept();
        AssetUpdate.Notify(current.Changed, current);
        return ValueTask.CompletedTask;
    }

    private void CollectUnusedSources()
    {
        for (int index = _retiredSources.Count - 1; index >= 0; index--)
        {
            var source = _retiredSources[index];
            if (source.Data.TryGetTarget(out _)) continue;
            source.Source.Dispose();
            _retiredSources.RemoveAt(index);
        }
    }

    public void Dispose()
    {
        lock (_contentGate)
        {
            _disposed = true;
            _payloadSource?.Dispose();
            _payloadSource = null;
            _data = null;
            for (int index = _retiredSources.Count - 1; index >= 0; index--)
            {
                _retiredSources[index].Source.Dispose();
                _retiredSources.RemoveAt(index);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
