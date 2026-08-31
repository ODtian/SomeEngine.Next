using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using Texture = SomeEngine.Assets.Schema.Texture;

namespace SomeEngine.Render.Materials;

/// <summary>A built-in material with no special treatment in render or Cluster infrastructure.</summary>
public sealed partial class StandardMaterial : Material
{
    public StandardMaterial(
        Texture baseColor,
        Texture normal,
        in SamplerDesc sampler,
        float roughness)
    {
        BaseColor = baseColor ?? throw new ArgumentNullException(nameof(baseColor));
        Normal = normal ?? throw new ArgumentNullException(nameof(normal));
        Sampler = sampler;
        Roughness = roughness;
    }

    public partial Texture BaseColor { get; set; }

    public partial Texture Normal { get; set; }

    public partial SamplerDesc Sampler { get; set; }

    public partial float Roughness { get; set; }
}
