using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Schema;

/// <summary>Built-in physically based material. It is one ordinary concrete Material type.</summary>
public sealed partial class StandardMaterial : Material
{
    public StandardMaterial()
    {
        MaterialSampler = new SamplerDesc(
            FilterType.Linear,
            FilterType.Linear,
            FilterType.Linear,
            AddressType.Repeat,
            AddressType.Repeat,
            AddressType.Repeat);
        BaseColorTint = Vector4.One;
        MetallicFactor = 0.0f;
        Roughness = 0.5f;
        EmissiveFactor = Vector3.Zero;
    }

    public partial Texture? AlbedoMap { get; set; }

    public partial Texture? NormalMap { get; set; }

    public partial Texture? ARMMap { get; set; }

    public partial SamplerDesc MaterialSampler { get; set; }

    public partial Vector4 BaseColorTint { get; set; }

    public partial float MetallicFactor { get; set; }

    public partial float Roughness { get; set; }

    public partial Vector3 EmissiveFactor { get; set; }
}
