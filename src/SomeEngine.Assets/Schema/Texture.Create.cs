using SomeEngine.Graphics;

namespace SomeEngine.Assets.Schema;

public sealed partial class Texture
{
    /// <summary>Reads a file's resource shape without creating a texture or reading pixel chunks.</summary>
    public static TextureDesc ReadDescription(Asset<Texture> asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        TextureData fields = asset.GetFieldsGenerated<TextureData>();
        bool cube = fields.SampledDimension is SomeEngine.Graphics.TextureViewDimension.Cube
            or SomeEngine.Graphics.TextureViewDimension.CubeArray;
        uint layers = cube ? checked(fields.ArrayLayerCount * 6) : fields.ArrayLayerCount;
        return new TextureDesc(fields.Dimension, fields.Width, fields.Height, fields.Depth,
            fields.MipLevelCount, layers, 1, fields.Format,
            TextureUsages.Sampled | TextureUsages.CopyDestination, label: fields.Name);
    }

    /// <summary>Returns detached file tile metadata. No runtime object or GPU allocation is created.</summary>
    public static IReadOnlyList<TextureMipTile> ReadMipTiles(Asset<Texture> asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Array.AsReadOnly((asset.GetFieldsGenerated<TextureData>().MipTiles ?? []).ToArray());
    }

    /// <summary>Updates procedural content through the same device-safe boundary, without any disk asset.</summary>
    public ValueTask UpdateAsync(in TextureDesc description, IReadOnlyList<TextureMipTile> tiles,
        SomeEngine.Graphics.TextureViewDimension sampledDimension, Format? sampledFormat = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return UpdateContentAsync(Create(description, tiles, sampledDimension, sampledFormat), cancellationToken);
    }

    /// <summary>Creates CPU texture content without a disk asset or device allocation.</summary>
    public static Texture Create(in TextureDesc description, IReadOnlyList<TextureMipTile> tiles,
        SomeEngine.Graphics.TextureViewDimension sampledDimension, Format? sampledFormat = null)
    {
        if (description.SampleCount != 1)
            throw new ArgumentException("Uploaded texture content requires one sample per texel.", nameof(description));
        ArgumentNullException.ThrowIfNull(tiles);
        bool cube = sampledDimension is SomeEngine.Graphics.TextureViewDimension.Cube or SomeEngine.Graphics.TextureViewDimension.CubeArray;
        if (cube && description.ArrayLayerCount % 6 != 0)
            throw new ArgumentException("Cube texture arrays require six resource layers per cube.", nameof(description));
        var content = new TextureData
        {
            Name = description.Label, Dimension = description.Dimension,
            Width = description.Width, Height = description.Height, Depth = description.Depth,
            MipLevelCount = description.MipLevelCount,
            ArrayLayerCount = cube ? description.ArrayLayerCount / 6 : description.ArrayLayerCount,
            Format = description.Format, SampledFormat = sampledFormat ?? description.Format,
            SampledDimension = sampledDimension,
            MipTiles = tiles.Select(static tile => tile.CopyFields()).ToList(),
        };
        TextureData.InitializeTiles(content);
        var result = new Texture { _content = content };
        _ = ValidateAndGetBootstrapTiles(result);
        return result;
    }
}
