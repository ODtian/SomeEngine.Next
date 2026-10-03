using Game.Assets;
using Game.Content;

string manifestPath = Path.Combine(AppContext.BaseDirectory, "Library", "AssetManifest", SomeEngine.Assets.AssetManifest.CatalogFileName);
if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine("Project content has not been built. Run Game.Build for this project, then rebuild Game.App.");
    return 1;
}
MaterialTypes.Register();
ImportedScenes.SceneMaterialAsset.Register();
if (args is ["--test-scene", var contentDirectory, .. var renderingArguments])
{
    var options = SomeEngine.Runtime.RuntimeStartupOptions.Parse(renderingArguments);
    var testPlatform = new SomeEngine.Runtime.SceneTestPlatform(
        [new("Shadow reference", Path.GetFullPath(contentDirectory))], options);
    return await SomeEngine.Runtime.Application.RunAsync(renderingArguments, platform: testPlatform);
}
if (args.Length != 0)
{
    Console.Error.WriteLine("Use F1 for interactive settings, or --test-scene <content directory> followed by rendering options for a reproducible capture.");
    return 1;
}
var scenes = System.Text.Json.JsonSerializer.Deserialize<SomeEngine.Runtime.TestScene[]>(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-scenes.json")))!;
var platform = new SomeEngine.Runtime.SceneTestPlatform(scenes.Select(scene => scene with
{
    ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory),
}).ToArray());
return await SomeEngine.Runtime.Application.RunAsync([], static (material, seconds) =>
{
    float roughness = 0.5f + MathF.Sin(seconds) * 0.2f;
    switch (material)
    {
        case StandardMaterial standard: standard.Roughness = roughness; return true;
        case ImportedScenes.SceneMaterial imported: imported.Roughness = roughness; return true;
        default: return false;
    }
}, platform);
