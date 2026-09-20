using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Schema;

/// <summary>Built-in unlit material. It is one ordinary concrete Material type.</summary>
public sealed partial class UnlitMaterial : Material
{
    public partial Texture? AlbedoMap { get; set; }

    public partial SamplerDesc MaterialSampler { get; set; } = new(
        FilterType.Linear, FilterType.Linear, FilterType.Linear,
        AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);

    public partial Vector4 BaseColorTint { get; set; } = Vector4.One;
}
