#if SOMEENGINE_DEVELOPMENT
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeGame;

internal static class MaterialAssets
{
    internal static async Task PrepareAsync(AssetProject project)
    {
        using ShaderAsset standard = await ShaderAsset.OpenSourceAsync(Path.Combine(project.RootPath, "assets/Shaders/standard.shader"));
        using ShaderSourceBlock standardInputs = StandardMaterialInputs.Create();
        using ClusterMaterialEntriesBlock standardEntries = ClusterMaterialEntriesBlock.Create();
        standard.SetBlock(standardInputs);
        standard.SetBlock(standardEntries);
        standard.SelectInputs<ShaderSourceBlock>();
        standard.SelectInputs<ClusterMaterialEntriesBlock>();
        await SaveAsync(project, standard, "standard");

        using ShaderAsset unlit = await ShaderAsset.OpenSourceAsync(Path.Combine(project.RootPath, "assets/Shaders/unlit.shader"));
        using ShaderSourceBlock unlitInputs = UnlitMaterialInputs.Create();
        using ClusterMaterialEntriesBlock unlitEntries = ClusterMaterialEntriesBlock.CreateUnlit();
        unlit.SetBlock(unlitInputs);
        unlit.SetBlock(unlitEntries);
        unlit.SelectInputs<ShaderSourceBlock>();
        unlit.SelectInputs<ClusterMaterialEntriesBlock>();
        await SaveAsync(project, unlit, "unlit");
        await MaterialTemplates.SaveAsync(project, standard.Reference, unlit.Reference);
    }

    private static async Task SaveAsync(AssetProject project, ShaderAsset shader, string name)
    {
        string path = Path.Combine(project.RootPath, $"assets/Shaders/{name}.shader.asset");
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(path, SlangShaderImporter.CreateDefaultTargets(slang));
        await shader.CompileAsync(slang, settings);
        project.RegisterAsset(await shader.SaveAsync(path));
    }
}
#endif