using Game.Assets;
using Game.Content;
using SomeEngine.Transparency.Research;

#if !DEBUG && !GAME_MANAGED_DIAGNOSTICS
if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
{
    Console.Error.WriteLine("Build the NativeAOT Release application before launching DefaultGame.");
    return 1;
}
#endif

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
string? measuredScene = null;
if (args is ["--measure-scene", var sceneName, .. var measurementArguments])
{
    measuredScene = sceneName;
    args = measurementArguments;
}
var scenes = SomeEngine.Runtime.SceneTestPlatform.ReadScenes(
    Path.Combine(AppContext.BaseDirectory, "test-scenes.json"), measuredScene);
SomeEngine.Runtime.RuntimeStartupOptions? startupOptions = args.Length == 0 ? null :
    SomeEngine.Runtime.RuntimeStartupOptions.Parse(args) with
    {
        WindowVSync = false, DynamicScene = scenes[0].Animate, AsyncCompute = true,
    };
BlendEffect? surfaceEffect = Enum.TryParse(args.FirstOrDefault(static value => value.StartsWith("--blend-effect="))?["--blend-effect=".Length..],
    true, out BlendEffect effect) ? effect : null;
BlendMethod blendMethod = Enum.TryParse(args.FirstOrDefault(static value => value.StartsWith("--blend-method="))?["--blend-method=".Length..],
    true, out BlendMethod method) ? method : BlendMethod.Forward;
SomeEngine.Transparency.Research.TransparencyComparison? tileRaster = args.Contains("--transparency-tiles-hardware")
    ? TransparencyComparison.TileHardware : args.Contains("--transparency-tiles-software") ? TransparencyComparison.TileSoftware : null;
var platform = new SomeEngine.Runtime.SceneTestPlatform(scenes.Select(scene => scene with
{
    ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory),
}).ToArray(), startupOptions)
{
    StartupTraceOutput = Environment.GetEnvironmentVariable("SOMEENGINE_STARTUP_TRACE"),
    CreateColorFeature = surfaceEffect is { } selectedEffect
        ? (rendering, scene) => new BlendEffectColorFeature(rendering,
            new(selectedEffect, selectedEffect == BlendEffect.Xray ? 32 : 128, rendering.Width, rendering.Height,
                depthOffset: scene.Name == "1024 spheres" ? 32 : 0), blendMethod, 4)
        : tileRaster is { } selectedRaster ? (rendering, _) => new TileVisibilityColorFeature(rendering,
            ResearchGeometry.CreateShadedSpheres(8, .5f, rendering.Width, rendering.Height), selectedRaster, 1f / 255)
        : null,
    ColorFeatureSettings = new Dictionary<string, string>
    {
        ["Effect"] = surfaceEffect?.ToString() ?? "OIT",
        ["Method"] = surfaceEffect is not null ? blendMethod.ToString() : tileRaster?.ToString() ?? "Opaque",
    },
};
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
