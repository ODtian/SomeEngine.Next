namespace SomeEngine.Assets.Schema;

internal sealed class UnlitMaterialData : MaterialData
{
    internal UnlitMaterialData()
    {
        MaterialType = SomeEngine.Serialization.BinaryTypeId.FromLogicalName("SomeEngine.Assets.Schema.UnlitMaterial");
        MaterialSampler = new(SomeEngine.Graphics.FilterType.Linear, SomeEngine.Graphics.FilterType.Linear, SomeEngine.Graphics.FilterType.Linear, SomeEngine.Graphics.AddressType.Repeat, SomeEngine.Graphics.AddressType.Repeat, SomeEngine.Graphics.AddressType.Repeat);
        BaseColorTint = System.Numerics.Vector4.One;
    }
    public AssetId<Texture> AlbedoMap
    {
        get => Parameters.FirstOrDefault(static p => p.Name == "AlbedoMap").Texture;
        set => Set(new("AlbedoMap", MaterialParameterKind.Texture) { Texture = value });
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
}
