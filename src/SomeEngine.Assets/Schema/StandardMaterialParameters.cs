using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

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
    public AssetId<TextureAsset> OcclusionMap
    {
        get => _content.GetParameter(nameof(OcclusionMap))?.Texture ?? default;
        set => _content.SetParameter(new(nameof(OcclusionMap), MaterialParameterKind.Texture) { Texture = value });
    }
    public AssetId<TextureAsset> EmissiveMap
    {
        get => _content.GetParameter(nameof(EmissiveMap))?.Texture ?? default;
        set => _content.SetParameter(new(nameof(EmissiveMap), MaterialParameterKind.Texture) { Texture = value });
    }
    public float NormalScale
    {
        get => _content.GetParameter(nameof(NormalScale))?.Float ?? 1;
        set => _content.SetParameter(new(nameof(NormalScale), MaterialParameterKind.Float) { Float = value });
    }
    public float OcclusionStrength
    {
        get => _content.GetParameter(nameof(OcclusionStrength))?.Float ?? 1;
        set => _content.SetParameter(new(nameof(OcclusionStrength), MaterialParameterKind.Float) { Float = value });
    }
    public bool UseOcclusionMap
    {
        get => _content.GetParameter(nameof(UseOcclusionMap))?.Bool ?? false;
        set => _content.SetParameter(new(nameof(UseOcclusionMap), MaterialParameterKind.Bool) { Bool = value });
    }
    public bool UseEmissiveMap
    {
        get => _content.GetParameter(nameof(UseEmissiveMap))?.Bool ?? false;
        set => _content.SetParameter(new(nameof(UseEmissiveMap), MaterialParameterKind.Bool) { Bool = value });
    }
    public bool DecodeAlbedoSrgb
    {
        get => _content.GetParameter(nameof(DecodeAlbedoSrgb))?.Bool ?? false;
        set => _content.SetParameter(new(nameof(DecodeAlbedoSrgb), MaterialParameterKind.Bool) { Bool = value });
    }
    public bool DecodeEmissiveSrgb
    {
        get => _content.GetParameter(nameof(DecodeEmissiveSrgb))?.Bool ?? false;
        set => _content.SetParameter(new(nameof(DecodeEmissiveSrgb), MaterialParameterKind.Bool) { Bool = value });
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
