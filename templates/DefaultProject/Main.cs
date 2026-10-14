using SomeEngine.Runtime;
#if SOMEENGINE_DEVELOPMENT
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using Microsoft.Extensions.DependencyInjection;
#endif

namespace Game;

public static class GameApplication
{
    public static Task<int> Main(string[] args)
    {
#if SOMEENGINE_DEVELOPMENT
        if (args is ["prepare-materials"] || AssetCommands.Matches(args))
        {
            AssetProject project = AssetProjects.Create(
                ProjectPaths.Root,
                [ProjectPaths.EngineShaders],
                GameAssetCapabilities.AddAssetCapabilities(new ServiceCollection()),
                GameAssetCapabilities.BuildVersion);
            return args is ["prepare-materials"] ? PrepareMaterialsAsync(project)
                : AssetCommands.RunAsync(project, args, Console.Out).AsTask();
        }
#endif
        return Application.RunAsync<EngineSceneMaterials>(args);
    }

#if SOMEENGINE_DEVELOPMENT
    private static async Task<int> PrepareMaterialsAsync(AssetProject project)
    {
        await MaterialAssets.PrepareAsync(project);
        return 0;
    }
#endif
}

#if SOMEENGINE_DEVELOPMENT
[AssetCapabilities]
public static partial class GameAssetCapabilities;
#endif
