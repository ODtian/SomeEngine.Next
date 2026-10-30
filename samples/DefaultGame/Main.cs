using SomeEngine.Resources;
using SomeEngine.Runtime;
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
        if (args is ["prepare-materials"] or ["prepare-materials", _]
            or ["prepare-mesh-fields"] or ["prepare-mesh-fields", _] || AssetCommands.Matches(args))
        {
            string projectRoot = args is ["prepare-materials" or "prepare-mesh-fields", var sceneRoot]
                ? Path.GetFullPath(sceneRoot) : ProjectPaths.Root;
            string engineRoot = Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../.."));
            AssetProject project = AssetProjects.Create(projectRoot,
                [ProjectPaths.EngineShaders],
                SampleAssetCapabilities.AddAssetCapabilities(new ServiceCollection()),
                SampleAssetCapabilities.BuildVersion);
            if (args[0] == "prepare-mesh-fields")
            {
                await MeshDistanceFields.BuildAsync(project, CancellationToken.None);
                return 0;
            }
            if (args is ["prepare-materials"])
            {
                await SampleMaterials.BuildAsync(project,
                    Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../..")), CancellationToken.None);
                return 0;
            }
            if (args is ["prepare-materials", _])
            {
                string shaders = Path.Combine(projectRoot, "assets/Shaders");
                ImportedSceneImporter.WriteSurfaceSources(shaders, engineRoot);
                foreach (string definition in Directory.EnumerateFiles(shaders, "scene_*.shader"))
                    await project.BuildAsync(definition);
                const string giPath = "assets/Shaders/gi_screen_probes.shader.asset";
                AssetManifest shared = AssetManifest.Load(Path.Combine(ProjectPaths.Root, "Library/AssetManifest"));
                AssetEntry giShader = shared.Assets.Values.Single(entry => entry.Path == giPath);
                File.Copy(Path.Combine(ProjectPaths.Root, giPath), Path.Combine(projectRoot, giPath), true);
                project.RegisterAsset(giShader);
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
        RuntimeStartupOptions startupOptions = RuntimeStartupOptions.Parse(args);
        string? clusterEffect = args.FirstOrDefault(static value => value.StartsWith("--cluster-effect=", StringComparison.Ordinal))
            ?["--cluster-effect=".Length..] ?? (args.Contains("--cluster-oit", StringComparer.Ordinal) ? "glass" : null);
        ClusterBlendAssets? clusterAssets = clusterEffect is null ? null
            : await ClusterBlendAssets.OpenAsync(AppContext.BaseDirectory, startupOptions.GraphicsBackend);
        ClusterBlendScene? clusterScene = clusterAssets is null ? null : new(clusterAssets, clusterEffect!);
        var scenes = SceneTestPlatform.ReadScenes(Path.Combine(AppContext.BaseDirectory, "test-scenes.json"));
        int sceneArgument = Array.IndexOf(args, "--measure-scene");
        if (sceneArgument >= 0)
            scenes = [scenes.Single(scene => scene.Name.Equals(args[sceneArgument + 1], StringComparison.OrdinalIgnoreCase))];
        var platform = new SceneTestPlatform(scenes.Select(scene => scene with
        {
            ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory),
        }).ToArray(), startupOptions)
        {
            StartupTraceOutput = Environment.GetEnvironmentVariable("SOMEENGINE_STARTUP_TRACE"),
            ExitAfterMeasurements = args.Contains("--measure-scenes", StringComparer.Ordinal),
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
        int gpuTimesArgument = Array.IndexOf(args, "--gpu-times");
        var gi = new ExperimentalGi(() => platform.Application.Options.ExperimentalGlobalIllumination);
        platform.Application.InstallGeometry(gi);
        platform.Application.CreateColorFeature = gi.Connect;
        SceneGpuTimes? gpuTimes = gpuTimesArgument >= 0 ? new(platform) : null;
        if (gpuTimes is not null) platform.Application.FrameMeasured = gpuTimes.Add;
        if (platform.ExitAfterMeasurements) platform.StartMeasurements(args.Contains("--trace-scenes", StringComparer.Ordinal));
        int result = await Application.RunAsync<SampleSceneMaterials>(args, static (material, seconds) =>
        {
            float roughness = 0.5f + MathF.Sin(seconds) * 0.2f;
            switch (material)
            {
                case StandardMaterial standard: standard.Roughness = roughness; return true;
                case ImportedScenes.SceneMaterial imported: imported.Roughness = roughness; return true;
                default: return false;
            }
        }, platform.Application);
        gpuTimes?.Write(args[gpuTimesArgument + 1]);
        return result;
    }
}
