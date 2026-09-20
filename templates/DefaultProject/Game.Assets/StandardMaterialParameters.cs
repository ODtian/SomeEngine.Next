using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace Game.Assets;

/// <summary>Typed access to the existing material parameter data. Missing local values keep material defaults or inherit.</summary>
public readonly struct StandardMaterialParameters
{
    private readonly MaterialBlock _content;
    public StandardMaterialParameters(MaterialBlock content) => _content = content ?? throw new ArgumentNullException(nameof(content));

    public AssetId<TextureAsset> AlbedoMap
    {
        get => _content.GetParameter(nameof(AlbedoMap))?.Texture ?? default;
        set => _content.SetParameter(new(nameof(AlbedoMap), MaterialParameterKind.Texture) { Texture = value });
    }
    public AssetId<TextureAsset> NormalMap
    {
        get => _content.GetParameter(nameof(NormalMap))?.Texture ?? default;
        set => _content.SetParameter(new(nameof(NormalMap), MaterialParameterKind.Texture) { Texture = value });
    }
    public AssetId<TextureAsset> ARMMap
    {
        get => _content.GetParameter(nameof(ARMMap))?.Texture ?? default;
        set => _content.SetParameter(new(nameof(ARMMap), MaterialParameterKind.Texture) { Texture = value });
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
    public float MetallicFactor
    {
        get => _content.GetParameter(nameof(MetallicFactor))?.Float ?? 0;
        set => _content.SetParameter(new(nameof(MetallicFactor), MaterialParameterKind.Float) { Float = value });
    }
    public float Roughness
    {
        get => _content.GetParameter(nameof(Roughness))?.Float ?? 0.5f;
        set => _content.SetParameter(new(nameof(Roughness), MaterialParameterKind.Float) { Float = value });
    }
    public Vector3 EmissiveFactor
    {
        get => _content.GetParameter(nameof(EmissiveFactor))?.Vector3 ?? default;
        set => _content.SetParameter(new(nameof(EmissiveFactor), MaterialParameterKind.Vector3) { Vector3 = value });
    }
}
