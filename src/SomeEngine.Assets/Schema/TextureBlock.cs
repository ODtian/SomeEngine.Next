using SomeEngine.Graphics;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using EngineTexture = SomeEngine.Resources.Texture;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.TextureBlock")]
[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Dimension), nameof(Width), nameof(Height),
    nameof(Depth), nameof(MipLevelCount), nameof(ArrayLayerCount), nameof(Format), nameof(SampledFormat),
    nameof(SampledDimension), nameof(MipTiles))]
public sealed partial class TextureBlock : AssetBlock
{
    public partial TextureDimension Dimension { get; set; }
    public partial uint Width { get; set; }
    public partial uint Height { get; set; }
    public partial uint Depth { get; set; }
    public partial uint MipLevelCount { get; set; }
    public partial uint ArrayLayerCount { get; set; }
    public partial Format Format { get; set; }
    public partial Format SampledFormat { get; set; }
    public partial SomeEngine.Graphics.TextureViewDimension SampledDimension { get; set; }
    public partial IList<TextureMipTile> MipTiles { get; set; }

    public TextureDesc GetDescription(string? name = null) => new(Dimension, Width, Height, Depth, MipLevelCount,
        TextureTiles.IsCube(SampledDimension) ? checked(ArrayLayerCount * 6) : ArrayLayerCount,
        1, Format, TextureUsages.Sampled | TextureUsages.CopyDestination, label: name);

    internal void ValidateDescription()
        => TextureTiles.Validate(GetDescription(), SampledDimension, SampledFormat, MipTiles.ToArray());

    protected override void ValidateContent() => _ = ReadTiles();

    private (TextureMipTile Tile, ulong ContentKey)[] ReadTiles()
    {
        var result = new (TextureMipTile Tile, ulong ContentKey)[MipTiles.Count];
        var keys = new HashSet<ulong>();
        for (int index = 0; index < result.Length; index++)
        {
            TextureMipTile tile = MipTiles[index];
            ulong key = TextureTiles.ChunkKey(tile.MipLevel, tile.ArrayLayer, tile.Face, tile.DepthSlice, tile.TileX, tile.TileY);
            if (!keys.Add(key)) throw new InvalidDataException("Texture tile keys collided or repeat the same coordinates.");
            ulong contentKey = Chunks.ContainsKey(tile.ChunkKey) ? tile.ChunkKey : Chunks.ContainsKey(key) ? key : tile.ChunkKey;
            ulong length = Chunks.ContainsKey(contentKey) ? checked((ulong)Chunks[contentKey].Length) : tile.DecodedLength;
            result[index] = (tile.ChunkKey == key && tile.DecodedLength == length
                ? tile : tile with { ChunkKey = key, DecodedLength = length }, contentKey);
        }
        Array.Sort(result, static (left, right) => TextureTiles.Compare(left.Tile, right.Tile));
        TextureTiles.Validate(GetDescription(), SampledDimension, SampledFormat, result.Select(static row => row.Tile).ToArray());
        return result;
    }

    /// <summary>Loads the coarsest mip by default, plus any pixels already supplied by this edit branch.</summary>
    public async ValueTask<EngineTexture> CreateAsync(string? name = null, CancellationToken cancellationToken = default, uint? firstMip = null)
    {
        using TextureBlock copy = Duplicate();
        cancellationToken.ThrowIfCancellationRequested();
        var content = copy.ReadTiles();
        TextureMipTile[] tiles = content.Select(static row => row.Tile).ToArray();
        _ = TextureTiles.Bootstrap(copy.GetDescription(), copy.SampledDimension, tiles);
        uint first = firstMip ?? copy.MipLevelCount - 1;
        if (first >= copy.MipLevelCount) throw new ArgumentOutOfRangeException(nameof(firstMip));
        var selected = content.Where(row => row.Tile.MipLevel >= first || copy.Chunks.ContainsKey(row.ContentKey)).ToArray();
        var pixels = new List<SomeEngine.Resources.TexturePixels>();
        foreach (var row in selected)
        {
            ReadOnlyMemory<byte> bytes = await copy.ReadBytesAsync(row.ContentKey, cancellationToken).ConfigureAwait(false);
            if ((ulong)bytes.Length != row.Tile.DecodedLength)
                throw new InvalidDataException("Texture chunk length differs from its tile declaration.");
            pixels.Add(new(row.Tile, bytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        EngineTexture texture = EngineTexture.Create(copy.GetDescription(name), tiles, pixels, copy.SampledDimension, copy.SampledFormat);
        texture.DataVersion = copy.DataVersion;
        return texture;
    }

    /// <summary>Explicitly acquires an additional mip; the Texture itself never opens a source.</summary>
    public async ValueTask LoadMipAsync(EngineTexture texture, uint mipLevel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(texture.IsDisposed, texture);
        using TextureBlock copy = Duplicate();
        var content = copy.ReadTiles();
        if (mipLevel >= copy.MipLevelCount) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        ulong revision = texture.Revision;
        if (texture.DataVersion != copy.DataVersion || texture.Width != copy.Width || texture.Height != copy.Height || texture.Depth != copy.Depth
            || texture.Dimension != copy.Dimension || texture.MipLevelCount != copy.MipLevelCount
            || texture.ArrayLayerCount != copy.ArrayLayerCount || texture.Format != copy.Format
            || texture.SampledFormat != copy.SampledFormat || texture.SampledDimension != copy.SampledDimension
            || !texture.MipTiles.SequenceEqual(content.Select(static row => row.Tile)))
            throw new InvalidOperationException("The texture description differs; use a full content update before loading more mips.");
        var selected = content.Where(row => row.Tile.MipLevel == mipLevel).ToArray();
        var pixels = new List<SomeEngine.Resources.TexturePixels>();
        foreach (var row in selected)
        {
            if (texture.TryGetTile(row.Tile.ChunkKey, out _)) continue;
            ReadOnlyMemory<byte> bytes = await copy.ReadBytesAsync(row.ContentKey, cancellationToken).ConfigureAwait(false);
            pixels.Add(new(row.Tile, bytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        texture.AddPixels(pixels, revision);
    }

    public async ValueTask UpdateAsync(EngineTexture texture, string? name = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(texture.IsDisposed, texture);
        await using EngineTexture replacement = await CreateAsync(name, cancellationToken).ConfigureAwait(false);
        await texture.UpdateAsync(replacement, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        var content = ReadTiles();
        // This block belongs to the isolated save branch. The local rows retain the old
        // content keys while the encoded metadata uses deterministic tile coordinates.
        MipTiles = content.Select(static row => row.Tile).ToArray();
        writer.WriteMetadata(BlockTypeId, this);
        using AssetBlockSource? source = RetainSource();
        for (int index = 0; index < content.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = content[index];
            if (Chunks.ContainsKey(row.ContentKey))
                writer.WriteChunk(row.Tile.ChunkKey, Chunks[row.ContentKey], ChunkCompression.None,
                    alignment: 4096, ordinal: checked((uint)index));
            else
                await writer.CopyChunkAsync(source ?? throw new InvalidDataException("Texture tile has no pixel content."),
                    row.ContentKey, cancellationToken, destinationKey: row.Tile.ChunkKey, ordinal: checked((uint)index)).ConfigureAwait(false);
        }
    }

}
