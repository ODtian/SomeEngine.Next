using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Schema;

/// <summary>Built-in unlit material. It is one ordinary concrete Material type.</summary>
public sealed partial class UnlitMaterial : Material
{
    public UnlitMaterial()
    {
        MaterialSampler = new SamplerDesc(
            FilterType.Linear,
            FilterType.Linear,
            FilterType.Linear,
            AddressType.Repeat,
            AddressType.Repeat,
            AddressType.Repeat);
        BaseColorTint = Vector4.One;
    }

    public partial Texture? AlbedoMap { get; set; }

    public partial SamplerDesc MaterialSampler { get; set; }

    public partial Vector4 BaseColorTint { get; set; }
}
