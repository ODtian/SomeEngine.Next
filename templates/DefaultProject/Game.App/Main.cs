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
if (args.Any(static argument => argument is not ("--hud" or "--hud-static")))
{
    Console.Error.WriteLine("DefaultGame accepts --hud or --hud-static. Scene settings are available in the test panel (F1).");
    return 1;
}
var scenes = System.Text.Json.JsonSerializer.Deserialize<SomeEngine.Runtime.TestScene[]>(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-scenes.json")))!;
var platform = new SomeEngine.Runtime.SceneTestPlatform(scenes.Select(scene => scene with
{
    ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory),
}).ToArray());
var startup = SomeEngine.Runtime.RuntimeStartupOptions.Parse(args);
platform.SetHudOverlay(startup.HudOverlay, startup.HudNumbersChange);
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
