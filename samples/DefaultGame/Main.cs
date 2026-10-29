using SomeEngine.Resources;
using SomeEngine.Runtime;
using SomeEngine.Job;
using SomeEngine.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
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
        string? physicsSelection = args.FirstOrDefault(argument => argument == "--physics"
            || argument.StartsWith("--physics=", StringComparison.Ordinal));
        string? physicsId = physicsSelection is null or "--physics" ? null : physicsSelection["--physics=".Length..];
        string? physicsCheck = args.FirstOrDefault(argument => argument == "--physics-check"
            || argument.StartsWith("--physics-check=", StringComparison.Ordinal));
        string? physicsCheckId = physicsCheck is null or "--physics-check" ? null : physicsCheck["--physics-check=".Length..];
        PhysicsCrowdScale? crowdScale = PhysicsCrowdScale.Read(physicsId);
        bool crowdCheck = PhysicsCrowdScale.Read(physicsCheckId).HasValue;
        if (crowdScale.HasValue || crowdCheck)
            JobSystem.Initialize(new() { MaxResourceStates = PhysicsCrowdScale.MaxResourceStates });
        bool devicePhysics = crowdCheck || args.Contains("--physics-device=gpu", StringComparer.Ordinal);
        if (physicsCheck is not null)
        {
            string? physicsTrace = args.FirstOrDefault(argument => argument.StartsWith("--physics-trace=", StringComparison.Ordinal));
            using TraceCapture? capture = physicsTrace is null ? null : EngineTrace.Start(physicsTrace["--physics-trace=".Length..]);
            return devicePhysics ? MeasureDevicePhysicsScenes(args, physicsCheckId) : MeasurePhysicsScenes.Run(Console.Out, physicsCheckId);
        }
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
        if (args is ["sample-physics-skin"])
        {
            await SampleContent.BuildPhysicsSkinAsync(ProjectPaths.Root,
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
        bool inlineCrowdOnly = args.Contains("--crowd-inline", StringComparer.Ordinal);
        bool cachedCrowdOnly = args.Contains("--crowd-cache", StringComparer.Ordinal);
        bool crowdOnly = inlineCrowdOnly || cachedCrowdOnly || args.Contains("--crowd", StringComparer.Ordinal);
        bool characterClothOnly = crowdScale.HasValue;
        if (characterClothOnly) devicePhysics = true;
        bool physicsOnly = physicsSelection is not null;
        string? physicsContent = args.FirstOrDefault(argument => argument.StartsWith("--physics-content=", StringComparison.Ordinal));
        string physicsContentDirectory = physicsContent is null ? AppContext.BaseDirectory : physicsContent["--physics-content=".Length..];
        bool regressionOnly = args.Contains("--regression", StringComparer.Ordinal);
        bool measure = args.Contains("--measure", StringComparer.Ordinal);
        args = args.Where(argument => argument != physicsSelection && argument != physicsContent
            && !argument.StartsWith("--physics-device=", StringComparison.Ordinal)
            && argument is not ("--crowd" or "--crowd-inline" or "--crowd-cache" or "--regression" or "--measure")).ToArray();
        RuntimeStartupOptions startupOptions = RuntimeStartupOptions.Parse(args);
        startupOptions = startupOptions with
        {
            SurfacePanel = startupOptions.SurfacePanel || !crowdOnly,
            WorldNameplates = startupOptions.WorldNameplates || !crowdOnly,
        };
        if (physicsOnly) startupOptions = startupOptions with { FixedFrameSeconds = 1f / 60, SynchronousFrames = true };
        string? clusterEffect = args.FirstOrDefault(static value => value.StartsWith("--cluster-effect=", StringComparison.Ordinal))
            ?["--cluster-effect=".Length..] ?? (args.Contains("--cluster-oit", StringComparer.Ordinal) ? "glass" : null);
        ClusterBlendAssets? clusterAssets = clusterEffect is null && (regressionOnly || crowdOnly) ? null
            : await ClusterBlendAssets.OpenAsync(physicsOnly ? physicsContentDirectory : AppContext.BaseDirectory, startupOptions.GraphicsBackend,
                characterClothOnly ? "Physics crowd skin" : null);
        PhysicsCrowdDisplay? characterCloth = crowdScale is { } displayScale
            ? new(clusterAssets!.Standard, clusterAssets.PhysicsSkin!, displayScale) : null;
        ClusterBlendScene? clusterScene = clusterEffect is null ? null : new(clusterAssets!, clusterEffect);
        var scenes = SceneTestPlatform.ReadScenes(Path.Combine(AppContext.BaseDirectory, "test-scenes.json")).ToList();
        using CrowdScene? crowd = regressionOnly || physicsOnly ? null : new(new CrowdExample());
        if (crowd is not null)
        {
            TestScene[] characters =
            [
                crowd.CreateScene("1000 characters / inline", Path.Combine(AppContext.BaseDirectory, "CrowdInline")),
                crowd.CreateScene("1000 characters / cache", Path.Combine(AppContext.BaseDirectory, "CrowdCache")),
            ];
            if (cachedCrowdOnly) scenes = [characters[1]];
            else if (inlineCrowdOnly) scenes = [characters[0]];
            else if (crowdOnly) scenes = characters.ToList();
            else scenes.AddRange(characters);
        }
        PhysicsSceneExample[] physicsExamples = [];
        if (!regressionOnly && !crowdOnly)
        {
            IEnumerable<PhysicsSceneDescription> descriptions = physicsSelection is null or "--physics"
                ? PhysicsScenes.All : PhysicsScenes.All.Concat(PhysicsScaleScenes.All);
            physicsExamples = descriptions
                .Where(description => physicsSelection is null or "--physics" || description.Id == physicsSelection["--physics=".Length..])
                .Select(description => devicePhysics ? PhysicsSceneExample.CreateDevice(description) : PhysicsSceneExample.CreateCpu(description)).ToArray();
            TestScene[] physics = physicsExamples.Select(example => example.CreateScene(physicsContentDirectory, clusterAssets!.Standard)).ToArray();
            if (characterCloth is not null) physics = [characterCloth.CreateScene(physicsContentDirectory)];
            if (physicsOnly) scenes = physics.ToList();
            else scenes.AddRange(physics);
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
            MaximumMeasurementSeconds = regressionOnly ? 45 : null,
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
        if (devicePhysics)
            foreach (PhysicsSceneExample example in physicsExamples) platform.Application.InstallGeometry(example);
        if (characterCloth is not null) platform.Application.InstallGeometry(characterCloth);
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

    private static int MeasureDevicePhysicsScenes(string[] args, string? id)
    {
        RuntimeGraphicsBackend selected = RuntimeStartupOptions.Parse(args).GraphicsBackend;
        using IGraphicsBackend backend = selected == RuntimeGraphicsBackend.Direct3D12
            ? D3D12GraphicsBackend.Create()
            : VulkanGraphicsBackend.Create(new VulkanBackendOptions(EnableValidation: false, EnableDebugMessages: false));
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: PhysicsCrowdScale.Read(id).HasValue ? DeviceFeatures.IndirectCommands : default,
            label: "Scene physics measurement"));
        return MeasurePhysicsScenes.RunDevice(Console.Out, backend, device, id);
    }
}
