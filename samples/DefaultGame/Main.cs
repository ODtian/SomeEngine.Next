using SomeEngine.Resources;
using SomeEngine.Runtime;
using System.Text.Json.Nodes;
#if SOMEENGINE_DEVELOPMENT
using DefaultGame.Content;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using Microsoft.Extensions.DependencyInjection;
#endif

namespace DefaultGame;

public static class SampleApplication
{
    public static async Task<int> Main(string[] args)
    {
#if SOMEENGINE_DEVELOPMENT
        if (args is ["sample-crowd-materials"])
        {
            string engine = Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../.."));
            await CrowdContent.BuildMaterialsAsync(ProjectPaths.Root, engine);
            return 0;
        }
        if (args is ["sample-crowd"])
        {
            string engine = Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../.."));
            await SampleContent.BuildAsync(ProjectPaths.Root, engine);
            await CrowdContent.BuildAsync(ProjectPaths.Root, engine);
            return 0;
        }
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
        if (args is ["reuse-scene", var applicationRoot, var importedRoot, var outputRoot])
        {
            await ImportedSceneBuild.ReuseAsync(applicationRoot, importedRoot, outputRoot,
                Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../..")));
            return 0;
        }
        if (args is ["prepare-materials"] || AssetCommands.Matches(args))
        {
            AssetProject project = AssetProjects.Create(ProjectPaths.Root,
                [ProjectPaths.EngineShaders],
                SampleAssetCapabilities.AddAssetCapabilities(new ServiceCollection()),
                SampleAssetCapabilities.BuildVersion);
            if (args is ["prepare-materials"])
            {
                await SampleMaterials.BuildAsync(project, CancellationToken.None);
                return 0;
            }
            return await AssetCommands.RunAsync(project, args, Console.Out);
        }
        if (args.Contains("--gpu-cluster", StringComparer.Ordinal))
        {
            using GpuClusterExample example = await GpuClusterExample.CreateAsync(ProjectPaths.Root);
            var material = new UnlitMaterial { BaseColorTint = new(1, 0, 0, 1) };
            example.SetMaterial(material);
            // Grid density, dimensions and the update frame are example data.
            example.SetGrid(32, new(2, 2));
            var scene = new ApplicationScene(ProjectPaths.Root)
            {
                Options = RuntimeStartupOptions.Parse(args),
                UpdateFrame = frame =>
                {
                    if (frame.Index == 0)
                    {
                        foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                        frame.Scene.SetCameraTransform(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity);
                    }
                    if (frame.Index == 120) example.SetGrid(32, new(4, 2));
                },
            };
            scene.InstallGeometry(example);
            return await Application.RunAsync(args, application: scene);
        }
#endif
        bool crowdOnly = args.Contains("--crowd", StringComparer.Ordinal);
        bool regressionOnly = args.Contains("--regression", StringComparer.Ordinal);
        bool measure = args.Contains("--measure", StringComparer.Ordinal);
        args = args.Where(argument => argument is not ("--crowd" or "--regression" or "--measure")).ToArray();
        RuntimeStartupOptions startupOptions = RuntimeStartupOptions.Parse(args);
        startupOptions = startupOptions with
        {
            SurfacePanel = startupOptions.SurfacePanel || !crowdOnly,
            WorldNameplates = startupOptions.WorldNameplates || !crowdOnly,
        };
        string? clusterEffect = args.FirstOrDefault(static value => value.StartsWith("--cluster-effect=", StringComparison.Ordinal))
            ?["--cluster-effect=".Length..] ?? (args.Contains("--cluster-oit", StringComparer.Ordinal) ? "glass" : null);
        ClusterBlendAssets? clusterAssets = clusterEffect is null ? null
            : await ClusterBlendAssets.OpenAsync(AppContext.BaseDirectory, startupOptions.GraphicsBackend);
        ClusterBlendScene? clusterScene = clusterAssets is null ? null : new(clusterAssets, clusterEffect!);
        var scenes = SceneTestPlatform.ReadScenes(Path.Combine(AppContext.BaseDirectory, "test-scenes.json")).ToList();
        using CrowdScene? crowd = regressionOnly ? null : new(new CrowdExample());
        if (crowd is not null)
        {
            TestScene[] characters =
            [
                crowd.CreateScene("1000 characters / inline", Path.Combine(AppContext.BaseDirectory, "CrowdInline")),
                crowd.CreateScene("1000 characters / cache", Path.Combine(AppContext.BaseDirectory, "CrowdCache")),
            ];
            if (crowdOnly) scenes = characters.ToList();
            else scenes.AddRange(characters);
        }
        var platform = new SceneTestPlatform(scenes.Select(scene =>
        {
            string directory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory);
            string import = Path.Combine(directory, "scene-import.json");
            JsonNode? source = scene.DescribeWorkload is null && File.Exists(import)
                ? JsonNode.Parse(File.ReadAllText(import)) : null;
            if (source is not null) source["ContentDirectory"] = directory;
            return scene with
            {
                ContentDirectory = directory,
                DescribeWorkload = scene.DescribeWorkload ?? (source is null ? null : () => source),
            };
        }).ToArray(), startupOptions)
        {
            ExitAfterMeasurements = measure,
            MaximumMeasurementSeconds = crowdOnly ? null : 45,
            StartupTraceOutput = Environment.GetEnvironmentVariable("SOMEENGINE_STARTUP_TRACE"),
            InitializeScene = clusterScene is null ? null : clusterScene.Initialize,
            UpdateScene = clusterScene is null || args.Contains("--cluster-static", StringComparer.Ordinal) ? null : clusterScene.Update,
            ColorFeatureSettings = clusterScene is null ? null : new Dictionary<string, string>
            {
                ["Effect"] = clusterScene.Effect,
                ["Workload"] = clusterScene.Workload,
                ["Method"] = "Cluster",
                ["Candidates"] = startupOptions.BlendIndex.ToString(),
            },
        };
        if (measure) platform.StartMeasurements();
        return await Application.RunAsync<SampleSceneMaterials>(args, static (material, seconds) =>
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
