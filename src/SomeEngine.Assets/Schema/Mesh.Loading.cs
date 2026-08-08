using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Schema;

public partial class Mesh : IDisposable, IAsyncDisposable
{
    private MeshPayloadSource? _payloadSource;

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

    public void Dispose()
        => Interlocked.Exchange(ref _payloadSource, null)?.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
