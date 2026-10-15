using Game.Content;
using SomeEngine.Resources;
using SomeEngine.Runtime;

internal sealed class ClusterOitScene : IDisposable
{
    private readonly List<Material> _materials = [];

    public void Initialize(SceneEdit edit, TestScene scene)
    {
        Dispose();
        var copies = new Dictionary<Material, Material>();
        foreach (var entity in edit.MeshEntities.Take(32))
        {
            Material source = edit.GetMaterial(entity);
            if (!copies.TryGetValue(source, out Material? material))
            {
                material = CreateGlass(source);
                material.Name = source.Name + " glass";
                material.State = source.State with { Surface = SurfaceMode.Translucent };
                copies.Add(source, material);
                _materials.Add(material);
            }
            edit.ReplaceMaterial(entity, material);
        }
    }

    private static Material CreateGlass(Material source) => source switch
    {
        StandardMaterial value => new StandardMaterial
        {
            Shader = value.Shader, AlbedoMap = value.AlbedoMap, NormalMap = value.NormalMap, ARMMap = value.ARMMap,
            MaterialSampler = value.MaterialSampler, BaseColorTint = value.BaseColorTint with { W = .35f },
            MetallicFactor = value.MetallicFactor, Roughness = value.Roughness, EmissiveFactor = value.EmissiveFactor,
        },
        ImportedScenes.SceneMaterial value => new ImportedScenes.SceneMaterial
        {
            Shader = value.Shader, AlbedoMap = value.AlbedoMap, NormalMap = value.NormalMap, ARMMap = value.ARMMap,
            OcclusionMap = value.OcclusionMap, EmissiveMap = value.EmissiveMap, MaterialSampler = value.MaterialSampler,
            BaseColorTint = value.BaseColorTint with { W = .35f }, MetallicFactor = value.MetallicFactor,
            Roughness = value.Roughness, EmissiveFactor = value.EmissiveFactor, NormalScale = value.NormalScale,
            OcclusionStrength = value.OcclusionStrength,
        },
        UnlitMaterial value => new UnlitMaterial
        {
            Shader = value.Shader, AlbedoMap = value.AlbedoMap, MaterialSampler = value.MaterialSampler,
            BaseColorTint = value.BaseColorTint with { W = .35f },
        },
        _ => throw new NotSupportedException($"DefaultGame glass needs a supported scene material: {source.GetType().Name}."),
    };

    public void Dispose()
    {
        foreach (Material material in _materials) material.Dispose();
        _materials.Clear();
    }
}
