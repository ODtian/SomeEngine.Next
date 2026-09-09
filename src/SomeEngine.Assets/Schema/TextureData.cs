using SomeEngine.Graphics;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Texture content selected at an extraction boundary. Metadata and tile selection stay fixed
/// across reloads. The source asset must remain open while acquiring this version's tiles.
/// </summary>
public sealed class TextureData
{
    private readonly Texture _asset;
    internal TextureData(Texture asset, Texture content)
    {
        _asset = asset;
        Content = content;
    }
    internal Texture Content { get; }
    internal long TextureId => _asset.ContentIdentity;
    internal IList<TextureMipTile>? MipTiles => Content.MipTiles;
    public ulong Revision => Content.Revision;
    public string? AssetGuid => Content.AssetGuid;
    public string? Name => Content.Name;
    public TextureDimension Dimension => Content.Dimension;
    public uint Width => Content.Width;
    public uint Height => Content.Height;
    public uint Depth => Content.Depth;
    public uint MipLevelCount => Content.MipLevelCount;
    public uint ArrayLayerCount => Content.ArrayLayerCount;
    public Format Format => Content.Format;
    public Format SampledFormat => Content.SampledFormat;
    public SomeEngine.Graphics.TextureViewDimension SampledDimension => Content.SampledDimension;
    public uint BootstrapMipLevel => Content.BootstrapMipLevel;
    public bool HasBootstrap { get { _asset.RequireContentAccess(); return Content.HasBootstrap; } }
    public bool IsStreamed { get { _asset.RequireContentAccess(); return Content.IsStreamed; } }

    public ValueTask<ResidentChunkLease> AcquireMipTileAsync(uint mipLevel, uint arrayLayer,
        uint face, uint depthSlice, uint tileX, uint tileY, ChunkRequestOptions options = default,
        CancellationToken cancellationToken = default)
    {
        _asset.RequireContentAccess();
        return Content.AcquireMipTileAsync(mipLevel, arrayLayer, face, depthSlice, tileX, tileY, options, cancellationToken);
    }

    public bool TryAcquireResidentMipTile(uint mipLevel, uint arrayLayer, uint face,
        uint depthSlice, uint tileX, uint tileY, out ResidentChunkLease data)
    {
        _asset.RequireContentAccess();
        return Content.TryAcquireResidentMipTile(mipLevel, arrayLayer, face, depthSlice, tileX, tileY, out data);
    }
}
