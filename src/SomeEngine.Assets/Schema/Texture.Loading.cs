using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Schema;

public partial class Texture : IDisposable, IAsyncDisposable
{
    private ChunkRequestScheduler? _scheduler;
    private BinaryDocument<Texture>? _document;
    private int _disposed;

    [SomeEngine.Serialization.BinaryIgnore]
    public ulong Revision { get; private set; } = 1;

    [SomeEngine.Serialization.BinaryIgnore]
    public bool IsStreamed => Volatile.Read(ref _scheduler) is not null;
    [SomeEngine.Serialization.BinaryIgnore]
    public ChunkStreamingMetrics? StreamingMetrics => Volatile.Read(ref _scheduler)?.Metrics;
    [SomeEngine.Serialization.BinaryIgnore]
    public ResidencyBudgetLedger? Residency => Volatile.Read(ref _scheduler)?.Residency;

    public ValueTask<ResidentChunkLease> AcquireMipTileAsync(
        uint mipLevel,
        uint tileX,
        uint tileY,
        ChunkRequestOptions options = default,
        CancellationToken cancellationToken = default)
        => AcquireMipTileAsync(
            mipLevel,
            arrayLayer: 0,
            face: 0,
            depthSlice: 0,
            tileX,
            tileY,
            options,
            cancellationToken);

    public ValueTask<ResidentChunkLease> AcquireMipTileAsync(
        uint mipLevel,
        uint arrayLayer,
        uint face,
        uint depthSlice,
        uint tileX,
        uint tileY,
        ChunkRequestOptions options = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ChunkRequestScheduler scheduler = Volatile.Read(ref _scheduler)
            ?? throw new InvalidOperationException("Texture has no streamed document source.");
        ulong key = MipTileChunkKey(mipLevel, arrayLayer, face, depthSlice, tileX, tileY);
        _ = RequireMipTile(this, mipLevel, arrayLayer, face, depthSlice, tileX, tileY);
        return scheduler.AcquireAsync(key, options, cancellationToken);
    }

    public bool TryAcquireResidentMipTile(
        uint mipLevel,
        uint arrayLayer,
        uint face,
        uint depthSlice,
        uint tileX,
        uint tileY,
        out ResidentChunkLease lease)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ChunkRequestScheduler? scheduler = Volatile.Read(ref _scheduler);
        if (scheduler is null)
        {
            lease = default;
            return false;
        }
        _ = RequireMipTile(this, mipLevel, arrayLayer, face, depthSlice, tileX, tileY);
        return scheduler.TryAcquireResident(
            MipTileChunkKey(
                mipLevel,
                arrayLayer,
                face,
                depthSlice,
                tileX,
                tileY),
            out lease);
    }

    internal static async ValueTask<Texture> LoadAssetAsync(
        AssetLoadContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TextureLoadOptions options = context.GetOptions(TextureLoadOptions.Default);
        BinaryDocument<Texture> document = await context
            .OpenAsync<Texture>()
            .ConfigureAwait(false);
        Texture root = document.Root;
        ValidateRoot(root);

        ChunkRequestScheduler? scheduler = null;
        try
        {
            scheduler = ChunkRequestScheduler.CreateForDocument(
                document,
                options.DecodedBudgetBytes,
                options.MaxChunkConcurrency,
                options.MaxQueuedChunkRequests,
                options.StreamingMetrics,
                options.Residency);
            root._document = document;
            root._scheduler = scheduler;
            scheduler = null;
            return context.Transfer(document, root);
        }
        catch
        {
            if (scheduler is not null)
                await scheduler.DisposeAsync().ConfigureAwait(false);
            root._document = null;
            root._scheduler = null;
            throw;
        }
    }

    internal static async ValueTask ApplyReloadAsync(
        Texture current,
        Texture replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRoot(replacement);

        ChunkRequestScheduler? nextScheduler = Interlocked.Exchange(ref replacement._scheduler, null);
        BinaryDocument<Texture>? nextDocument = Interlocked.Exchange(ref replacement._document, null);
        ChunkRequestScheduler? previousScheduler = Interlocked.Exchange(ref current._scheduler, nextScheduler);
        BinaryDocument<Texture>? previousDocument = Interlocked.Exchange(ref current._document, nextDocument);

        current.AssetGuid = replacement.AssetGuid;
        current.Name = replacement.Name;
        current.Dimension = replacement.Dimension;
        current.Width = replacement.Width;
        current.Height = replacement.Height;
        current.Depth = replacement.Depth;
        current.MipLevelCount = replacement.MipLevelCount;
        current.ArrayLayerCount = replacement.ArrayLayerCount;
        current.Format = replacement.Format;
        current.SampledFormat = replacement.SampledFormat;
        current.SampledDimension = replacement.SampledDimension;
        current.MipTiles = replacement.MipTiles;
        current.Revision = checked(current.Revision + 1);

        if (previousScheduler is not null)
            await previousScheduler.DisposeAsync().ConfigureAwait(false);
        if (previousDocument is not null)
            await previousDocument.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ChunkRequestScheduler? scheduler = Interlocked.Exchange(ref _scheduler, null);
        BinaryDocument<Texture>? document = Interlocked.Exchange(ref _document, null);
        if (scheduler is not null)
            await scheduler.DisposeAsync().ConfigureAwait(false);
        if (document is not null)
            await document.DisposeAsync().ConfigureAwait(false);
    }
}

public readonly record struct TextureLoadOptions
{
    public TextureLoadOptions(
        long decodedBudgetBytes,
        ResidencyBudgetLedger? residency = null,
        int maxChunkConcurrency = 4,
        int maxQueuedChunkRequests = 4096,
        ChunkStreamingMetrics? streamingMetrics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodedBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunkConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxQueuedChunkRequests);
        DecodedBudgetBytes = decodedBudgetBytes;
        Residency = residency;
        MaxChunkConcurrency = maxChunkConcurrency;
        MaxQueuedChunkRequests = maxQueuedChunkRequests;
        StreamingMetrics = streamingMetrics;
    }

    public static TextureLoadOptions Default { get; } = new(256L * 1024 * 1024);

    public long DecodedBudgetBytes { get; }
    public ResidencyBudgetLedger? Residency { get; }
    public int MaxChunkConcurrency { get; }
    public int MaxQueuedChunkRequests { get; }
    public ChunkStreamingMetrics? StreamingMetrics { get; }
}
