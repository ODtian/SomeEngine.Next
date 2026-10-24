using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

/// <summary>Typed access to the existing material parameter data. Missing local values keep material defaults or inherit.</summary>
public readonly struct UnlitMaterialParameters
{
    private readonly MaterialBlock _content;
    public UnlitMaterialParameters(MaterialBlock content) => _content = content ?? throw new ArgumentNullException(nameof(content));

    public AssetId<TextureAsset> AlbedoMap
    {
        get => _content.GetParameter(nameof(AlbedoMap))?.Texture ?? default;
        set => _content.SetParameter(new(nameof(AlbedoMap), MaterialParameterKind.Texture) { Texture = value });
    }
    public SamplerDesc MaterialSampler
    {
        get => _content.GetParameter(nameof(MaterialSampler))?.Sampler ?? new(FilterType.Linear, FilterType.Linear, FilterType.Linear,
            AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);
        set => _content.SetParameter(new(nameof(MaterialSampler), MaterialParameterKind.Sampler) { Sampler = value });
    }
    public Vector4 BaseColorTint
    {
        get => _content.GetParameter(nameof(BaseColorTint))?.Vector4 ?? Vector4.One;
        set => _content.SetParameter(new(nameof(BaseColorTint), MaterialParameterKind.Vector4) { Vector4 = value });
    }
}
