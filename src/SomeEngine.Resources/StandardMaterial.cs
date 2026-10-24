using Texture = SomeEngine.Resources.Texture;
using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

/// <summary>Standard material with metalness and roughness parameters.</summary>
[MaterialType("SomeEngine.Assets.Schema.StandardMaterial")]
public sealed partial class StandardMaterial : Material
{
    public StandardMaterial()
    {
        Texture white = MaterialTextures.Create("White", [255, 255, 255, 255]);
        Texture normal = MaterialTextures.Create("Flat normal", [128, 128, 255, 255]);
        InitializeContent(null, null,
            [(new(nameof(AlbedoMap), MaterialParameterKind.Texture), white),
             (new(nameof(NormalMap), MaterialParameterKind.Texture), normal),
             (new(nameof(ARMMap), MaterialParameterKind.Texture), white)],
            [white, normal]);
    }

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
