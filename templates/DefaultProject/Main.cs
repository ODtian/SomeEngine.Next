using SomeEngine.Runtime;
#if SOMEENGINE_DEVELOPMENT
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
#endif

namespace Game;

public static class GameApplication
{
    public static Task<int> Main(string[] args)
    {
#if SOMEENGINE_DEVELOPMENT
        if (args.Length > 0 && args[0] is "import" or "build" or "list")
        {
            AssetProject project = AssetProjects.Create(
                ProjectPaths.Root,
                [ProjectPaths.EngineShaders],
                static services => GameAssetCapabilities.AddAssetCapabilities(services),
                GameAssetCapabilities.BuildVersion);
            return AssetCommands.RunAsync(project, args, Console.Out).AsTask();
        }
#endif
        return Application.RunAsync(args);
    }
}

#if SOMEENGINE_DEVELOPMENT
[AssetCapabilities]
public static partial class GameAssetCapabilities;
#endif
