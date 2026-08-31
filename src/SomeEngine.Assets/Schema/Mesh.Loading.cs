using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public partial class Mesh : IDisposable, IAsyncDisposable
{
    private MeshPayloadSource? _payloadSource;

    [SomeEngine.Serialization.BinaryIgnore]
    public ulong Revision { get; private set; } = 1;

    [SomeEngine.Serialization.BinaryIgnore]
    public bool IsStreamed => Volatile.Read(ref _payloadSource) is not null;

    internal bool TryRetainPayloadSource(out MeshPayloadSource? source)
    {
        MeshPayloadSource? current = Volatile.Read(ref _payloadSource);
        if (current is null)
        {
            source = null;
            return false;
        }

        source = current.Retain();
        return true;
    }

    internal bool TryBorrowPayloadSource(out MeshPayloadSource? source)
    {
        source = Volatile.Read(ref _payloadSource);
        return source is not null;
    }

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();

        MeshPayloadSource? next = Interlocked.Exchange(ref replacement._payloadSource, null);
        MeshPayloadSource? previous = Interlocked.Exchange(ref current._payloadSource, next);
        current.AssetGuid = replacement.AssetGuid;
        current.Name = replacement.Name;
        current.Bounds = replacement.Bounds;
        current.VertexStride = replacement.VertexStride;
        current.PayloadKey = replacement.PayloadKey;
        current.PayloadLength = replacement.PayloadLength;
        current.Payload = replacement.Payload;
        current.BvhOffset = replacement.BvhOffset;
        current.PageDigests = replacement.PageDigests;
        current.BvhLength = replacement.BvhLength;
        current.BvhSha256 = replacement.BvhSha256;
        current.QuantOrigin = replacement.QuantOrigin;
        current.QuantStep = replacement.QuantStep;
        current.Regions = replacement.Regions;
        current.Revision = checked(current.Revision + 1);
        previous?.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
        => Interlocked.Exchange(ref _payloadSource, null)?.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
