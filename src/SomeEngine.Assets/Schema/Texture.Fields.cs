using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public sealed partial class Texture
{
    private TextureData _content = new();

    public string? Name { get => _content.Name; set => _content.Name = value; }
    public TextureDimension Dimension { get => _content.Dimension; set => _content.Dimension = value; }
    public uint Width { get => _content.Width; set => _content.Width = value; }
    public uint Height { get => _content.Height; set => _content.Height = value; }
    public uint Depth { get => _content.Depth; set => _content.Depth = value; }
    public uint MipLevelCount { get => _content.MipLevelCount; set => _content.MipLevelCount = value; }
    public uint ArrayLayerCount { get => _content.ArrayLayerCount; set => _content.ArrayLayerCount = value; }
    public Format Format { get => _content.Format; set => _content.Format = value; }
    public Format SampledFormat { get => _content.SampledFormat; set => _content.SampledFormat = value; }
    public SomeEngine.Graphics.TextureViewDimension SampledDimension { get => _content.SampledDimension; set => _content.SampledDimension = value; }
    public IList<TextureMipTile>? MipTiles { get => _content.MipTiles; set => _content.MipTiles = value; }
}
