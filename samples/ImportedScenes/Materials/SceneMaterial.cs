using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using Texture = SomeEngine.Resources.Texture;

namespace ImportedScenes;

[MaterialType("ImportedScenes.SceneMaterial")]
public sealed partial class SceneMaterial : Material
{
    public partial Texture? AlbedoMap { get; set; }
    public partial Texture? NormalMap { get; set; }
    public partial Texture? ARMMap { get; set; }
    public partial Texture? OcclusionMap { get; set; }
    public partial Texture? EmissiveMap { get; set; }
    public partial SamplerDesc MaterialSampler { get; set; } = new(
        FilterType.Linear, FilterType.Linear, FilterType.Linear,
        AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);
    public partial Vector4 BaseColorTint { get; set; } = Vector4.One;
    public partial float MetallicFactor { get; set; } = 1;
    public partial float Roughness { get; set; } = 1;
    public partial Vector3 EmissiveFactor { get; set; }
    public partial float NormalScale { get; set; } = 1;
    public partial float OcclusionStrength { get; set; } = 1;
}

public sealed class SceneMaterialAsset : MaterialAsset
{
    public SceneMaterialAsset(Guid id = default) : base(id, SceneMaterial.TypeId) { }
    private SceneMaterialAsset(SceneMaterialAsset source) : base(source) { }
    public override SceneMaterialAsset Duplicate()
    {
        lock (SyncRoot) { RequireIdle(); return new(this); }
    }
    public static void Register() => MaterialAsset.RegisterType<SceneMaterial, SceneMaterialAsset>(static id => new(id));
}
