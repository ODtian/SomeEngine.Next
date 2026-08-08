namespace SomeEngine.Graphics;

public sealed class SparseResources : DeviceCapability
{
    private readonly Format[] _supportedTexture2DFormats;
    private readonly Format[] _supportedTexture3DFormats;

    internal SparseResources(
        Device device,
        uint tier,
        uint tileSizeInBytes,
        bool bufferSupported,
        ReadOnlySpan<Format> supportedTexture2DFormats,
        ReadOnlySpan<Format> supportedTexture3DFormats,
        uint maximumMappingsPerCall)
        : base(device)
    {
        Tier = tier;
        TileSizeInBytes = tileSizeInBytes;
        BufferSupported = bufferSupported;
        _supportedTexture2DFormats = supportedTexture2DFormats.ToArray();
        _supportedTexture3DFormats = supportedTexture3DFormats.ToArray();
        MaximumMappingsPerCall = maximumMappingsPerCall;
    }

    public uint Tier { get; }
    public uint TileSizeInBytes { get; }
    public bool BufferSupported { get; }
    public bool Texture2DSupported => _supportedTexture2DFormats.Length != 0;
    public bool Texture3DSupported => _supportedTexture3DFormats.Length != 0;
    public ReadOnlySpan<Format> SupportedTexture2DFormats => _supportedTexture2DFormats;
    public ReadOnlySpan<Format> SupportedTexture3DFormats => _supportedTexture3DFormats;
    public uint MaximumMappingsPerCall { get; }
}

public readonly record struct SparseTileShape(uint Width, uint Height, uint Depth);

public readonly record struct SparsePackedMipInfo(
    uint StandardMipLevelCount,
    uint PackedMipLevelCount,
    uint PackedMipTileOffset,
    uint PackedMipTileCount);

public readonly record struct SparseResourceInfo(
    SparseTileShape TileShape,
    ulong TotalTileCount,
    SparsePackedMipInfo PackedMips,
    ulong Alignment);

public readonly record struct SparseTileCoordinate(
    uint X,
    uint Y,
    uint Z,
    uint Subresource);

public readonly record struct SparseTileRegion(
    SparseTileCoordinate Start,
    uint Width,
    uint Height,
    uint Depth,
    uint TileCount,
    bool Boxed);

public enum SparseMappingType : byte
{
    Mapped,
    Reused,
    Unmapped,
}

public readonly record struct SparseMappingDesc(
    Resource Resource,
    SparseTileRegion ResourceTiles,
    SparseMappingType Type,
    Heap? Heap,
    ulong HeapTileOffset);

public readonly record struct SparseMappingCopyDesc(
    Resource Destination,
    SparseTileCoordinate DestinationStart,
    Resource Source,
    SparseTileCoordinate SourceStart,
    SparseTileRegion Region);
