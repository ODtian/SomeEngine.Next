using Texture = SomeEngine.Resources.Texture;
using System.Numerics;
using SomeEngine.Graphics;

using SomeEngine.Resources;
namespace Game.Content;

/// <summary>Project physically based material. It is one ordinary concrete Material type.</summary>
[MaterialType("SomeEngine.Assets.Schema.StandardMaterial")]
public sealed partial class StandardMaterial : Material
{
    public partial Texture? AlbedoMap { get; set; }

    public partial Texture? NormalMap { get; set; }

    public partial Texture? ARMMap { get; set; }

    public partial SamplerDesc MaterialSampler { get; set; } = new(
        FilterType.Linear, FilterType.Linear, FilterType.Linear,
        AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);

    public partial Vector4 BaseColorTint { get; set; } = Vector4.One;

    public partial float MetallicFactor { get; set; }

    public partial float Roughness { get; set; } = 0.5f;

    public partial Vector3 EmissiveFactor { get; set; }
}
