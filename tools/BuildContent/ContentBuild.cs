using DefaultGame.Content;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;

internal static class ContentBuild
{
    private static async Task<int> Main(string[] arguments)
    {
        switch (arguments)
        {
            case ["engine", var engineRoot]:
                await EngineShaderContent.BuildAsync(engineRoot, [SlangCompileTarget.Dxil]);
                return 0;
            case ["sample", var projectRoot, var engineRoot]:
                await SampleContent.BuildAsync(projectRoot, engineRoot);
                return 0;
            case ["shadow-settings", var projectRoot, var engineRoot]:
                SampleContent.BuildShadowShaders(projectRoot, engineRoot);
                return 0;
            case ["cluster-settings", var projectRoot, var engineRoot]:
                SampleContent.BuildClusterShaders(projectRoot, engineRoot);
                return 0;
            case ["runtime-settings", var projectRoot, var engineRoot]:
                await SampleContent.RefreshApplicationSettingsAsync(new AssetProject(projectRoot, []), engineRoot);
                return 0;
            case ["shader", var engineRoot, var definition]:
                await SampleContent.RefreshShaderAsync(engineRoot, definition, engineRoot);
                return 0;
            case ["shader", var projectRoot, var definition, var engineRoot]:
                await SampleContent.RefreshShaderAsync(projectRoot, definition, engineRoot);
                return 0;
            default:
                Console.Error.WriteLine("Usage: engine <engine-root> | sample <project-root> <engine-root> | shadow-settings <project-root> <engine-root> | cluster-settings <project-root> <engine-root> | runtime-settings <project-root> <engine-root> | shader <engine-root> <definition> | shader <project-root> <definition> <engine-root>");
                return 2;
        }
    }
}
