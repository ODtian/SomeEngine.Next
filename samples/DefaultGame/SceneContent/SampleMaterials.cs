using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace DefaultGame.Content;

/// <summary>The sample selects its Cluster entries before saving common release code.</summary>
public static class SampleMaterials
{
    public static async Task BuildAsync(AssetProject project, CancellationToken cancellationToken)
    {
        using ShaderAsset standard = await ShaderAsset.OpenSourceAsync(Path.Combine(project.RootPath, "assets/Shaders/standard.shader"), cancellationToken);
        using ShaderSourceBlock standardInputs = StandardMaterialInputs.Create();
        using ClusterMaterialEntriesBlock standardEntries = ClusterMaterialEntriesBlock.Create();
        standard.SetBlock(standardInputs);
        standard.SetBlock(standardEntries);
        standard.SelectInputs<ShaderSourceBlock>();
        standard.SelectInputs<ClusterMaterialEntriesBlock>();
        await SaveShaderAsync(project, standard, "standard", cancellationToken);

        using ShaderAsset unlit = await ShaderAsset.OpenSourceAsync(Path.Combine(project.RootPath, "assets/Shaders/unlit.shader"), cancellationToken);
        using ShaderSourceBlock unlitInputs = UnlitMaterialInputs.Create();
        using ClusterMaterialEntriesBlock unlitEntries = ClusterMaterialEntriesBlock.CreateUnlit();
        unlit.SetBlock(unlitInputs);
        unlit.SetBlock(unlitEntries);
        unlit.SelectInputs<ShaderSourceBlock>();
        unlit.SelectInputs<ClusterMaterialEntriesBlock>();
        await SaveShaderAsync(project, unlit, "unlit", cancellationToken);
        await MaterialTemplates.SaveAsync(project, standard.Reference, unlit.Reference, cancellationToken);
    }

    private static async Task SaveShaderAsync(AssetProject project, ShaderAsset shader, string fileName,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(project.RootPath, $"assets/Shaders/{fileName}.shader.asset");
        ShaderSourceBlock inputs = shader.GetBlock<ShaderSourceBlock>();
        foreach (string mode in new[] { "0", "8" })
        {
            inputs.Specializations.Add(new() { EntryPoint = "ClusterShadeMode", Arguments = [mode] });
            inputs.Specializations.Add(new() { EntryPoint = "ClusterShadowReceiver", Arguments = ["0", mode] });
            foreach (string export in new[] { "false", "true" })
                inputs.Specializations.Add(new() { EntryPoint = "ClusterForwardShade", Arguments = ["0", mode, export] });
        }
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(path, SlangShaderImporter.CreateDefaultTargets(slang));
        await shader.CompileAsync(slang, settings, cancellationToken);
        project.RegisterAsset(await shader.SaveAsync(path, cancellationToken));
        Console.WriteLine($"Material shader: {fileName}");
    }
}
