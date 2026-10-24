using Game.Assets;
using Game.Content;
using Game.App;
using SomeEngine.Runtime;

string manifestPath = Path.Combine(AppContext.BaseDirectory, "Library", "AssetManifest", SomeEngine.Assets.AssetManifest.CatalogFileName);
if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine("Build project content with Game.Build, then build Game.App to copy the assets.");
    return 1;
}
MaterialTypes.Register();
ImportedScenes.SceneMaterialAsset.Register();
bool crowdOnly = args.Contains("--crowd");
bool regressionOnly = args.Contains("--regression");
bool measure = args.Contains("--measure");
string[] engineArgs = args.Where(argument => argument is not ("--crowd" or "--regression" or "--measure")).ToArray();
RuntimeStartupOptions startup = RuntimeStartupOptions.Parse(["--no-vsync", .. engineArgs]);
startup = startup with { SurfacePanel = startup.SurfacePanel || !crowdOnly, WorldNameplates = startup.WorldNameplates || !crowdOnly };
var scenes = System.Text.Json.JsonSerializer.Deserialize<TestScene[]>(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-scenes.json")))!
    .Select(scene => scene with { ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory) }).ToList();
var example = new CrowdExample();
using var crowd = new CrowdScene(example);
TestScene[] crowdScenes =
[
    new("1000 characters / inline", Path.Combine(AppContext.BaseDirectory, "CrowdInline"))
        { CreateApplication = path => crowd.CreateApplication(path) },
    new("1000 characters / cache", Path.Combine(AppContext.BaseDirectory, "CrowdCache"))
        { CreateApplication = path => crowd.CreateApplication(path) },
];
if (crowdOnly) scenes = crowdScenes.ToList();
else if (!regressionOnly) scenes.AddRange(crowdScenes);
var platform = new SceneTestPlatform(scenes, startup) { ExitAfterMeasurements = measure };
if (measure) platform.StartMeasurements();
return await Application.RunAsync(engineArgs, static (material, seconds) =>
{
    float roughness = 0.5f + MathF.Sin(seconds) * 0.2f;
    switch (material)
    {
        case StandardMaterial standard: standard.Roughness = roughness; return true;
        case ImportedScenes.SceneMaterial imported: imported.Roughness = roughness; return true;
        default: return false;
    }
}, platform);
