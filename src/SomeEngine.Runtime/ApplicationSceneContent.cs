using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Render;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

internal sealed class ApplicationSceneContent(ApplicationContent content, World world, SceneWorldData scene) : IDisposable
{
    internal ApplicationContent Content { get; } = content;
    internal World World { get; } = world;
    internal SceneWorldData Scene { get; } = scene;
    internal IDisposable? GameSystems { get; set; }
    internal SceneScript? Script { get; init; }

    internal static async Task<ApplicationSceneContent> LoadAsync<TMaterials>(string directory, ShaderLibrary shaders)
        where TMaterials : ISceneMaterials
    {
        string root = Path.GetFullPath(directory);
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
        AssetLoader files = EngineShaderContent.OpenFiles(root, manifest);
        var content = new ApplicationContent(root, files, manifest);
        AssetEntry settingsEntry = manifest.List(ApplicationSettingsAsset.FileType).Single();
        using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(files,
            new AssetId<ApplicationSettingsAsset>(settingsEntry.AssetGuid)).ConfigureAwait(false);
        ApplicationSettingsBlock boot = await settings.GetBlockAsync<ApplicationSettingsBlock>().ConfigureAwait(false);
        using WorldAsset asset = await WorldAsset.OpenAsync(files, boot.Scene).ConfigureAwait(false);
        World world = await asset.LoadAsync(SceneSerialization.CreateRegistry(),
            new(Execution: new WorldJobs(JobSystem.Default))).ConfigureAwait(false);
        SceneWorldData scene = await SceneWorldData.CreateAsync<TMaterials>(world, content, shaders).ConfigureAwait(false);
        return new(content, world, scene);
    }

    public void Dispose()
    {
        GameSystems?.Dispose();
        Scene.Dispose();
        World.Dispose();
        Content.Files.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
