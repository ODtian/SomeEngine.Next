using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[Asset(".texture.asset")]
public sealed partial class Texture
{
    public partial string? Name { get; set; }
    public partial TextureDimension Dimension { get; set; }
    public partial uint Width { get; set; }
    public partial uint Height { get; set; }
    public partial uint Depth { get; set; }
    public partial uint MipLevelCount { get; set; }
    public partial uint ArrayLayerCount { get; set; }
    public partial Format Format { get; set; }
    public partial Format SampledFormat { get; set; }
    public partial SomeEngine.Graphics.TextureViewDimension SampledDimension { get; set; }
    public partial ImmutableList<TextureMipTile>? MipTiles { get; set; }
}
