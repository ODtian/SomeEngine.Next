using Texture = SomeEngine.Resources.Texture;
using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

/// <summary>Standard material with metalness and roughness parameters.</summary>
public sealed partial class StandardMaterial : Material
{
    public partial Texture? AlbedoMap { get; set; }

    public partial Texture? NormalMap { get; set; }

    public partial Texture? ARMMap { get; set; }

    public partial Texture? OcclusionMap { get; set; }

    public partial Texture? EmissiveMap { get; set; }

    public partial float NormalScale { get; set; } = 1;

    public partial float OcclusionStrength { get; set; } = 1;

    public partial bool UseOcclusionMap { get; set; }

    public partial bool UseEmissiveMap { get; set; }

    public partial bool DecodeAlbedoSrgb { get; set; }

    public partial bool DecodeEmissiveSrgb { get; set; }

    public partial SamplerDesc MaterialSampler { get; set; } = new(
        FilterType.Linear, FilterType.Linear, FilterType.Linear,
        AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);

    public partial Vector4 BaseColorTint { get; set; } = Vector4.One;

    public partial float MetallicFactor { get; set; }

    public partial float Roughness { get; set; } = 0.5f;

    public partial Vector3 EmissiveFactor { get; set; }
}
