using SomeEngine.Resources;
using SomeEngine.Runtime;
#if SOMEENGINE_DEVELOPMENT
using DefaultGame.Content;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
#endif

namespace DefaultGame;

public static class SampleApplication
{
    public static async Task<int> Main(string[] args)
    {
#if SOMEENGINE_DEVELOPMENT
        if (args is ["sample"])
        {
            await SampleContent.BuildAsync(ProjectPaths.Root,
                Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../..")));
            return 0;
        }
        if (args is ["import-scene", var application, var source, var output])
        {
            await ImportedSceneBuild.BuildAsync(application, source, output,
                Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../..")));
            return 0;
        }
        if (AssetCommands.Matches(args))
        {
            AssetProject project = AssetProjects.Create(ProjectPaths.Root,
                [ProjectPaths.EngineShaders],
                static services => SampleAssetCapabilities.AddAssetCapabilities(services),
                SampleAssetCapabilities.BuildVersion);
            return await AssetCommands.RunAsync(project, args, Console.Out);
        }
#endif
        ImportedScenes.SceneMaterialAsset.Register();
        var scenes = SceneTestPlatform.ReadScenes(Path.Combine(AppContext.BaseDirectory, "test-scenes.json"));
        var platform = new SceneTestPlatform(scenes.Select(scene => scene with
        {
            ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory),
        }).ToArray(), RuntimeStartupOptions.Parse(args))
        {
            StartupTraceOutput = Environment.GetEnvironmentVariable("SOMEENGINE_STARTUP_TRACE"),
        };
        return await Application.RunAsync(args, static (material, seconds) =>
        {
            float roughness = 0.5f + MathF.Sin(seconds) * 0.2f;
            switch (material)
            {
                case StandardMaterial standard: standard.Roughness = roughness; return true;
                case ImportedScenes.SceneMaterial imported: imported.Roughness = roughness; return true;
                default: return false;
            }
        }, platform.Application);
    }
}
