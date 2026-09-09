namespace SomeEngine.Assets.Schema;

internal sealed class StandardMaterialData : MaterialData
{
    internal StandardMaterialData()
    {
        MaterialType = SomeEngine.Serialization.BinaryTypeId.FromLogicalName("SomeEngine.Assets.Schema.StandardMaterial");
        MaterialSampler = new(SomeEngine.Graphics.FilterType.Linear, SomeEngine.Graphics.FilterType.Linear, SomeEngine.Graphics.FilterType.Linear, SomeEngine.Graphics.AddressType.Repeat, SomeEngine.Graphics.AddressType.Repeat, SomeEngine.Graphics.AddressType.Repeat);
        BaseColorTint = System.Numerics.Vector4.One;
        Roughness = 0.5f;
    }
    public AssetId<Texture> AlbedoMap
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "AlbedoMap").Texture;
        set => Set(new("AlbedoMap", MaterialParameterKind.Texture) { Texture = value });
    }
    public AssetId<Texture> NormalMap
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "NormalMap").Texture;
        set => Set(new("NormalMap", MaterialParameterKind.Texture) { Texture = value });
    }
    public AssetId<Texture> ARMMap
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "ARMMap").Texture;
        set => Set(new("ARMMap", MaterialParameterKind.Texture) { Texture = value });
    }
    public SomeEngine.Graphics.SamplerDesc MaterialSampler
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "MaterialSampler").Sampler;
        set => Set(new("MaterialSampler", MaterialParameterKind.Sampler) { Sampler = value });
    }
    public System.Numerics.Vector4 BaseColorTint
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "BaseColorTint").Vector4;
        set => Set(new("BaseColorTint", MaterialParameterKind.Vector4) { Vector4 = value });
    }
    public float MetallicFactor
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "MetallicFactor").Float;
        set => Set(new("MetallicFactor", MaterialParameterKind.Float) { Float = value });
    }
    public float Roughness
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "Roughness").Float;
        set => Set(new("Roughness", MaterialParameterKind.Float) { Float = value });
    }
    public System.Numerics.Vector3 EmissiveFactor
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "EmissiveFactor").Vector3;
        set => Set(new("EmissiveFactor", MaterialParameterKind.Vector3) { Vector3 = value });
    }
}
