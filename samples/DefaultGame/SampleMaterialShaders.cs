#if SOMEENGINE_DEVELOPMENT
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace DefaultGame;

internal static class SampleMaterialShaders
{
    internal static readonly AssetId<ShaderAsset> GpuCluster = new(new Guid("e4d41dff-6daa-4f1c-862b-b4495993c655"));
    internal static readonly AssetId<ShaderAsset> GpuTerrain = new(new Guid("17bb2709-3789-4dda-a177-0929c127a5fb"));
    internal static readonly AssetId<ShaderAsset> TerrainDisplacement = new(new Guid("f8046b46-99d6-4623-89a4-4ab7d16a0faf"));

    internal static async Task<Shader> LoadAsync(string contentDirectory, AssetId<ShaderAsset> id)
    {
        AssetManifest manifest = AssetManifest.Load(Path.Combine(contentDirectory, "Library", "AssetManifest"));
        await using var files = EngineShaderContent.OpenFiles(contentDirectory, manifest);
        using ShaderAsset asset = await ShaderAsset.OpenAsync(files, id);
        var settings = SlangShaderImporter.CreateSessionDescription(Path.Combine(contentDirectory, asset.SourcePath),
            SlangShaderImporter.CreateTargets(SlangShaderImporter.GlobalSession,
                [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv]),
            [ProjectPaths.EngineShaders]);
        using var library = new ShaderLibrary(SlangShaderImporter.GlobalSession, settings);
        return await library.CreateAsync(asset, files);
    }
}
#endif
