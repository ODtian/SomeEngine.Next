using static SomeEngine.Assets.Schema.TextureData;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Schema;

public partial class Texture : IDisposable, IAsyncDisposable
{
    private ChunkRequestScheduler? _scheduler;
    private BinaryDocument? _document;
    private ResidentChunkLease[] _bootstrap = [];
    private int _disposed;

    [SomeEngine.Serialization.BinaryIgnore]
    public ulong Revision { get; private set; } = 1;

    [SomeEngine.Serialization.BinaryIgnore]
    public bool IsStreamed => Volatile.Read(ref _scheduler) is not null;
    [SomeEngine.Serialization.BinaryIgnore]
    public ChunkStreamingMetrics? StreamingMetrics => Volatile.Read(ref _scheduler)?.Metrics;
    [SomeEngine.Serialization.BinaryIgnore]
    public ResidencyBudgetLedger? Residency => Volatile.Read(ref _scheduler)?.Residency;
    [SomeEngine.Serialization.BinaryIgnore]
    public uint BootstrapMipLevel => MipLevelCount == 0 ? 0 : MipLevelCount - 1;
    [SomeEngine.Serialization.BinaryIgnore]
    public bool HasBootstrap => Volatile.Read(ref _bootstrap).Length != 0 ||
        (!IsStreamed && MipTiles is { Count: > 0 } && MipTiles.All(static tile => tile.Payload.HasValue));

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
        _ = RequireMipTile(_content, mipLevel, arrayLayer, face, depthSlice, tileX, tileY);
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
        _ = RequireMipTile(_content, mipLevel, arrayLayer, face, depthSlice, tileX, tileY);
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

    public static async ValueTask<Texture> CreateAsync(Texture.Asset asset,
        CancellationToken cancellationToken = default, TextureLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new Texture { _content = asset.CopyValues() };
        result._content.AssetGuid = null;
        BinaryDocument? document = asset.RetainSource();
        if (document is null)
        {
            TextureData.InitializeTiles(result._content);
            _ = ValidateAndGetBootstrapTiles(result);
            return result;
        }
        TextureLoadOptions readOptions = options ?? TextureLoadOptions.Default;
        result._document = document;
        try
        {
            ValidateRoot(result._content);
            result._scheduler = ChunkRequestScheduler.CreateForDocument(document,
                readOptions.DecodedBudgetBytes, readOptions.MaxChunkConcurrency,
                readOptions.MaxQueuedChunkRequests, readOptions.StreamingMetrics, readOptions.Residency);
            result._bootstrap = await AcquireBootstrapAsync(result, readOptions, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads and validates replacement content before applying it at the caller's world update boundary.</summary>
    public async ValueTask UpdateAsync(Texture.Asset asset, CancellationToken cancellationToken = default,
        TextureLoadOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Texture replacement = await CreateAsync(asset, cancellationToken, options).ConfigureAwait(false);
        await UpdateContentAsync(replacement, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpdateContentAsync(Texture replacement, CancellationToken cancellationToken)
    {
        await using (replacement)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task completion = QueueUpdate(replacement);
            using CancellationTokenRegistration cancel = cancellationToken.Register(() =>
                FailDeviceUpdate(replacement, new OperationCanceledException(cancellationToken)));
            await completion.ConfigureAwait(false);
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
        // Creation already validates disk or procedural input using its own representation.
        // A runtime update must not require procedural pixels to resemble a disk root.
        ulong nextRevision = checked(current.Revision + 1);

        ChunkRequestScheduler? previousScheduler = null;
        BinaryDocument? previousDocument = null;
        ResidentChunkLease[] previousBootstrap = [];
        ChunkRequestScheduler? nextScheduler = Interlocked.Exchange(ref replacement._scheduler, null);
        BinaryDocument? nextDocument = Interlocked.Exchange(ref replacement._document, null);
        ResidentChunkLease[] nextBootstrap = Interlocked.Exchange(
            ref replacement._bootstrap,
            []);
        previousScheduler = Interlocked.Exchange(ref current._scheduler, nextScheduler);
        previousDocument = Interlocked.Exchange(ref current._document, nextDocument);
        previousBootstrap = Interlocked.Exchange(
            ref current._bootstrap,
            nextBootstrap);

        current._content = replacement._content;
        current.Revision = nextRevision;

        await CloseContentAsync(previousBootstrap, previousScheduler, previousDocument).ConfigureAwait(false);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var errors = new List<Exception>();
        try { ReleaseDevices(); }
        catch (Exception error) { errors.Add(error); }
        ChunkRequestScheduler? scheduler = Interlocked.Exchange(ref _scheduler, null);
        BinaryDocument? document = Interlocked.Exchange(ref _document, null);
        ResidentChunkLease[] bootstrap = Interlocked.Exchange(ref _bootstrap, []);
        try { await CloseContentAsync(bootstrap, scheduler, document).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Some texture resources could not be closed.", errors);
    }

    private static async ValueTask CloseContentAsync(ResidentChunkLease[] bootstrap,
        ChunkRequestScheduler? scheduler, BinaryDocument? document)
    {
        var errors = new List<Exception>();
        foreach (ResidentChunkLease chunk in bootstrap)
            try { chunk.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        try { if (scheduler is not null) await scheduler.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        try { if (document is not null) await document.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Some texture content could not be closed.", errors);
    }

    private static async ValueTask<ResidentChunkLease[]> AcquireBootstrapAsync(
        Texture root,
        TextureLoadOptions options,
        CancellationToken cancellationToken)
    {
        TextureMipTile[] tiles = ValidateAndGetBootstrapTiles(root);
        long declaredBytes = 0;
        foreach (TextureMipTile tile in tiles)
        {
            if (tile.DecodedLength > long.MaxValue)
                throw new InvalidDataException("Texture Bootstrap decoded length exceeds the supported range.");

            long tileBytes = (long)tile.DecodedLength;
            if (declaredBytes > long.MaxValue - tileBytes)
                throw new InvalidDataException("Texture Bootstrap decoded lengths exceed the supported range.");
            declaredBytes += tileBytes;
        }
        if (declaredBytes > options.DecodedBudgetBytes)
        {
            throw new InvalidDataException(
                $"Texture Bootstrap requires {declaredBytes} decoded bytes, exceeding the " +
                $"configured {options.DecodedBudgetBytes}-byte Texture budget.");
        }

        var leases = new ResidentChunkLease[tiles.Length];
        int acquired = 0;
        try
        {
            for (; acquired < tiles.Length; acquired++)
            {
                TextureMipTile tile = tiles[acquired];
                leases[acquired] = await root.AcquireMipTileAsync(
                        tile.MipLevel,
                        tile.ArrayLayer,
                        tile.Face,
                        tile.DepthSlice,
                        tile.TileX,
                        tile.TileY,
                        new ChunkRequestOptions(Priority: int.MaxValue),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return leases;
        }
        catch
        {
            for (int index = acquired - 1; index >= 0; index--)
                leases[index].Dispose();
            throw;
        }
    }

    private static TextureMipTile[] ValidateAndGetBootstrapTiles(Texture root)
    {
        uint mipLevel = root.MipLevelCount - 1;
        TextureMipTile[] tiles = (root.MipTiles ?? [])
            .Where(tile => tile.MipLevel == mipLevel)
            .ToArray();
        if (tiles.Length == 0)
            throw new InvalidDataException("Texture metadata does not contain its coarsest Bootstrap mip.");

        bool cube = root.SampledDimension is
            SomeEngine.Graphics.TextureViewDimension.Cube or
            SomeEngine.Graphics.TextureViewDimension.CubeArray;
        uint faceCount = cube ? 6u : 1u;
        uint depthCount = root.Dimension == SomeEngine.Graphics.TextureDimension.Texture3D
            ? MipExtent(root.Depth, mipLevel)
            : 1u;
        uint width = MipExtent(root.Width, mipLevel);
        uint height = MipExtent(root.Height, mipLevel);
        ulong requiredArea = checked((ulong)width * height);

        for (uint layer = 0; layer < root.ArrayLayerCount; layer++)
        for (uint face = 0; face < faceCount; face++)
        for (uint depth = 0; depth < depthCount; depth++)
        {
            TextureMipTile[] group = tiles
                .Where(tile => tile.ArrayLayer == layer &&
                    tile.Face == face &&
                    tile.DepthSlice == depth)
                .ToArray();
            if (group.Length == 0)
            {
                throw new InvalidDataException(
                    $"Texture Bootstrap mip {mipLevel} is missing layer {layer}, face {face}, " +
                    $"depth slice {depth}.");
            }

            ulong area = 0;
            for (int index = 0; index < group.Length; index++)
            {
                TextureMipTile tile = group[index];
                ulong tileArea = (ulong)tile.Width * tile.Height;
                if (area > ulong.MaxValue - tileArea)
                    throw new InvalidDataException("Texture Bootstrap tile area exceeds the supported range.");
                area += tileArea;
                for (int previous = 0; previous < index; previous++)
                {
                    TextureMipTile other = group[previous];
                    bool overlap = tile.TileX < other.TileX + other.Width &&
                        other.TileX < tile.TileX + tile.Width &&
                        tile.TileY < other.TileY + other.Height &&
                        other.TileY < tile.TileY + tile.Height;
                    if (overlap)
                        throw new InvalidDataException("Texture Bootstrap tiles overlap.");
                }
            }
            if (area != requiredArea)
                throw new InvalidDataException("Texture Bootstrap tiles do not cover the coarsest mip.");
        }
        return tiles;
    }

    private static void DisposeLeases(ResidentChunkLease[] leases)
    {
        for (int index = leases.Length - 1; index >= 0; index--)
            leases[index].Dispose();
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
