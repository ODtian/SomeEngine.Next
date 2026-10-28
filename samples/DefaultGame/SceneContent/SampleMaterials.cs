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

    public static async Task BuildPhysicsSkinAsync(AssetProject project, CancellationToken cancellationToken)
    {
        string path = Path.Combine(project.RootPath, "assets/Shaders/physics-crowd.shader");
        using ShaderAsset skin = await ShaderAsset.OpenSourceAsync(path, cancellationToken);
        ShaderSourceBlock inputs = await skin.GetBlockAsync<ShaderSourceBlock>(cancellationToken);
        inputs.Specializations = SampleContent.CreateMaterialSpecializations(0);
        await skin.SaveSourceAsync(path, cancellationToken: cancellationToken);
        await project.BuildAsync(path, cancellationToken: cancellationToken);
    }

    private static async Task SaveShaderAsync(AssetProject project, ShaderAsset shader, string fileName,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(project.RootPath, $"assets/Shaders/{fileName}.shader.asset");
        ShaderSourceBlock inputs = shader.GetBlock<ShaderSourceBlock>();
        foreach (ShaderSpecialization specialization in SampleContent.CreateMaterialSpecializations(0))
            inputs.Specializations.Add(specialization);
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(path, SlangShaderImporter.CreateDefaultTargets(slang));
        await shader.CompileAsync(slang, settings, cancellationToken);
        project.RegisterAsset(await shader.SaveAsync(path, cancellationToken));
        Console.WriteLine($"Material shader: {fileName}");
    }
}
