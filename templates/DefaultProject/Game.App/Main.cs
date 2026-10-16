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
        WindowVSync = false, DynamicScene = scenes[0].Animate,
    };
BlendEffect? surfaceEffect = Enum.TryParse(args.FirstOrDefault(static value => value.StartsWith("--blend-effect="))?["--blend-effect=".Length..],
    true, out BlendEffect effect) ? effect : null;
BlendMethod blendMethod = Enum.TryParse(args.FirstOrDefault(static value => value.StartsWith("--blend-method="))?["--blend-method=".Length..],
    true, out BlendMethod method) ? method : BlendMethod.Forward;
BlendScene blendScene = Enum.TryParse(args.FirstOrDefault(static value => value.StartsWith("--blend-scene="))?["--blend-scene=".Length..],
    true, out BlendScene inputScene) ? inputScene : BlendScene.DepthBands;
BlendWorkload? workload = blendScene != BlendScene.DepthBands ? BlendWorkloads.Scenes.Single(value => value.Scene == blendScene)
    : surfaceEffect is { } inputEffect ? new(inputEffect.ToString(), inputEffect, inputEffect == BlendEffect.Xray ? 32 : 128, 4) : null;
string? blendShader = args.FirstOrDefault(static value => value.StartsWith("--blend-shader="))?["--blend-shader=".Length..];
string? oitScene = args.FirstOrDefault(static value => value.StartsWith("--oit-scene="))?["--oit-scene=".Length..];
SomeEngine.Transparency.Research.TransparencyComparison? tileRaster = args.Contains("--transparency-tiles-hardware")
    ? TransparencyComparison.TileHardware : args.Contains("--transparency-tiles-software") ? TransparencyComparison.TileSoftware : null;
if (oitScene is not null) tileRaster ??= TransparencyComparison.TileSoftware;
string? clusterEffect = args.FirstOrDefault(static value => value.StartsWith("--cluster-effect="))?["--cluster-effect=".Length..]
    ?? (args.Contains("--cluster-oit") ? "glass" : null);
using var clusterAssets = clusterEffect is null ? null : await ClusterBlendAssets.OpenAsync(AppContext.BaseDirectory,
    startupOptions?.GraphicsBackend ?? SomeEngine.Runtime.RuntimeGraphicsBackend.Direct3D12);
using var clusterScene = clusterAssets is null ? null : new ClusterBlendScene(clusterAssets, clusterEffect!);
var platform = new SomeEngine.Runtime.SceneTestPlatform(scenes.Select(scene => scene with
{
    ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory),
}).ToArray(), startupOptions)
{
    StartupTraceOutput = Environment.GetEnvironmentVariable("SOMEENGINE_STARTUP_TRACE"),
    InitializeScene = clusterScene is null ? null : clusterScene.Initialize,
    UpdateScene = clusterScene is null || args.Contains("--cluster-static") ? null : clusterScene.Update,
    CreateColorFeature = workload is { } selectedWorkload
        ? (rendering, scene) => new BlendEffectColorFeature(rendering,
            selectedWorkload.CreateData(rendering.Width, rendering.Height, scene.Name == "1024 spheres"
                ? selectedWorkload.Scene == BlendScene.Assembly ? 56 : 32 : 0), blendMethod, selectedWorkload.Distance, new(blendShader))
        : tileRaster is { } selectedRaster ? (rendering, _) => new TileVisibilityColorFeature(rendering,
            oitScene switch
            {
                "glass" => ResearchGeometry.CreateGlass(32, rendering.Width, rendering.Height),
                "smoke" => ResearchGeometry.CreateSmoke(rendering.Width, rendering.Height),
                "foliage" => ResearchGeometry.CreateFoliage(rendering.Width, rendering.Height),
                _ => ResearchGeometry.CreateShadedSpheres(8, .5f, rendering.Width, rendering.Height),
            }, selectedRaster, 1f / 255)
        : null,
    ColorFeatureSettings = new Dictionary<string, string>
    {
        ["Effect"] = clusterScene?.Effect ?? workload?.Effect.ToString() ?? "OIT",
        ["Workload"] = clusterScene?.Workload ?? workload?.Name ?? oitScene ?? "Shaded spheres",
        ["Method"] = clusterScene is not null ? "Cluster" : workload is not null ? blendMethod.ToString() : tileRaster?.ToString() ?? "Opaque",
        ["Candidates"] = (startupOptions?.BlendIndex ?? SomeEngine.Render.Cluster.Pipeline.ClusterBlendIndex.Xyz).ToString(),
        ["Shader"] = blendShader is null ? "Current" : "Reference",
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
