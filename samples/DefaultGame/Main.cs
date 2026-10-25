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
#if !DEBUG && !SOMEENGINE_MANAGED_DIAGNOSTICS
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            Console.Error.WriteLine("Build the NativeAOT Release application before launching DefaultGame.");
            return 1;
        }
#endif
#if SOMEENGINE_DEVELOPMENT
        if (args.Length != 0 && args[0] is "--pack" or "--update-pack" or "--refresh-shader" or
            "--inspect-shader" or "--refresh-scene-shaders" or "refresh-scene-shaders" or "--import-scene" or
            "--project" or "--engine")
            return await RunDevelopmentCommandAsync(args);
        if (args is ["sample"])
        {
            using var measurements = ShaderBuildMeasurements.Start(Console.Out);
            await SampleContent.BuildAsync(ProjectPaths.Root,
                Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../..")));
            return 0;
        }
        if (args is ["import-scene", var application, var source, var output])
        {
            using var measurements = ShaderBuildMeasurements.Start(Console.Out);
            await ImportedSceneBuild.BuildAsync(application, source, output,
                Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../..")));
            return 0;
        }
        if (args is ["prepare-materials"] || AssetCommands.Matches(args))
        {
            AssetProject project = AssetProjects.Create(ProjectPaths.Root,
                [ProjectPaths.EngineShaders],
                SampleAssetCapabilities.AddAssetCapabilities(new ServiceCollection()));
            if (args is ["prepare-materials"])
            {
                using var measurements = ShaderBuildMeasurements.Start(Console.Out);
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
            SceneTestPlatform.ConfigureViews(scene, args);
            return await Application.RunAsync(args, application: scene);
        }
#endif
        string? measuredScene = null;
        string? testContentDirectory = null;
        if (args is ["--measure-scene", var sceneName, .. var measurementArguments])
        {
            measuredScene = sceneName;
            args = measurementArguments;
        }
        else if (args is ["--test-scene", var contentDirectory, .. var renderingArguments])
        {
            testContentDirectory = Path.GetFullPath(contentDirectory);
            args = renderingArguments;
        }
        RuntimeStartupOptions startupOptions = RuntimeStartupOptions.Parse(args);
        string? clusterEffect = args.FirstOrDefault(static value => value.StartsWith("--cluster-effect=", StringComparison.Ordinal))
            ?["--cluster-effect=".Length..] ?? (args.Contains("--cluster-oit", StringComparer.Ordinal) ? "glass" : null);
        ClusterBlendAssets? clusterAssets = clusterEffect is null ? null
            : await ClusterBlendAssets.OpenAsync(AppContext.BaseDirectory, startupOptions.GraphicsBackend);
        ClusterBlendScene? clusterScene = clusterAssets is null ? null : new(clusterAssets, clusterEffect!);
        TestScene[] scenes = testContentDirectory is null
            ? SceneTestPlatform.ReadScenes(Path.Combine(AppContext.BaseDirectory, "test-scenes.json"), measuredScene)
            : [new("Shadow reference", testContentDirectory)];
        var platform = new SceneTestPlatform(scenes.Select(scene => scene with
        {
            ContentDirectory = Path.GetFullPath(scene.ContentDirectory, AppContext.BaseDirectory),
        }).ToArray(), startupOptions)
        {
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
        platform.ConfigureViews(args);
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

#if SOMEENGINE_DEVELOPMENT
    // Legacy command spellings remain an application concern; the implementation lives in the libraries.
    private static async Task<int> RunDevelopmentCommandAsync(string[] args)
    {
        if (args[0] is "--pack" or "--update-pack")
        {
            if (args.Length < 3) throw new ArgumentException("Usage: --pack|--update-pack <content root> <pack> [options]");
            await AssetCommands.PackAsync(Path.GetFullPath(args[1]),
                [args[0][2..], Path.GetFullPath(args[2]), .. args[3..]], Console.Out);
            return 0;
        }
        if (args[0] == "--refresh-shader") return await RefreshSelectedShaderAsync(args);
        if (args[0] == "--inspect-shader") return await InspectSelectedShaderAsync(args);
        if (args is ["--import-scene", var application, var source, var output, var importEngine])
        {
            using var measurements = ShaderBuildMeasurements.Start(Console.Out);
            await ImportedSceneBuild.BuildAsync(application, source, output, importEngine);
            return 0;
        }
        if (args is ["--refresh-scene-shaders", var applicationRoot, var sceneRoot, var engineRoot])
        {
            using var measurements = ShaderBuildMeasurements.Start(Console.Out);
            await ImportedSceneBuild.RefreshShadersAsync(applicationRoot, sceneRoot, engineRoot);
            return 0;
        }
        if (args is ["refresh-scene-shaders", var applicationRoot2, var sceneRoot2])
        {
            using var measurements = ShaderBuildMeasurements.Start(Console.Out);
            await ImportedSceneBuild.RefreshShadersAsync(applicationRoot2, sceneRoot2,
                Path.GetFullPath(Path.Combine(ProjectPaths.EngineShaders, "../..")));
            return 0;
        }
        if (args[0] is "--project" or "--engine")
        {
            string? project = null, engine = null;
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (option is not ("--project" or "--engine") || ++index == args.Length)
                    throw new ArgumentException("Usage: --project <project directory> --engine <engine checkout>");
                if (option == "--project") project = Path.GetFullPath(args[index]);
                else engine = Path.GetFullPath(args[index]);
            }
            if (project is null || engine is null) throw new ArgumentException("Both --project and --engine are required.");
            using var measurements = ShaderBuildMeasurements.Start(Console.Out);
            await SampleContent.BuildAsync(project, engine);
            return 0;
        }
        throw new ArgumentException("The selected development command has invalid arguments.");
    }

    private static async Task<int> RefreshSelectedShaderAsync(string[] args)
    {
        const string usage = "Usage: --refresh-shader <content root> <shader GUID> <engine root> [--source-root <root>] [--regenerate-material] [--identity-ties] [--dry-run]";
        if (args.Length < 4 || !Guid.TryParse(args[2], out Guid id) || id == Guid.Empty)
            throw new ArgumentException(usage);
        string root = Path.GetFullPath(args[1]);
        string engine = Path.GetFullPath(args[3]);
        string sourceRoot = root;
        bool dryRun = false, regenerate = false, identityTies = false;
        for (int index = 4; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run": dryRun = true; break;
                case "--regenerate-material": regenerate = true; break;
                case "--identity-ties": identityTies = true; break;
                case "--source-root" when index + 1 < args.Length: sourceRoot = Path.GetFullPath(args[++index]); break;
                default: throw new ArgumentException(usage);
            }
        }
        string engineShaders = Path.Combine(engine, "assets", "Shaders");
        string sourceShaders = Path.Combine(sourceRoot, "assets", "Shaders");
        foreach (string path in new[] { engineShaders, sourceShaders, Path.Combine(root, "assets", "Shaders") })
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        bool shared = !string.Equals(root, sourceRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if ((regenerate || identityTies) && !shared)
            throw new ArgumentException("Material regeneration and identity ties require an explicitly selected shared material definition.");
        if (shared)
        {
            AssetManifest source = AssetManifest.Load(Path.Combine(sourceRoot, "Library", "AssetManifest"));
            if (!source.TryGetAsset(id, out AssetEntry entry)
                || !Path.GetFileName(entry.DefinitionPath).StartsWith("scene_", StringComparison.Ordinal))
                throw new ArgumentException("A shared definition must be one imported scene material shader.");
        }
        var project = new AssetProject(root, [], shaderIncludeDirectories: [engineShaders, sourceShaders]);
        using ShaderBuildMeasurements? measurements = dryRun ? null : ShaderBuildMeasurements.Start(Console.Out);
        await AssetCommands.RefreshShaderAsync(project, id, Console.Out, dryRun, sourceRoot,
            shared ? (definition, destination, token) => new ValueTask<Guid>(ImportedSceneImporter.BuildMaterialShaderAsync(
                project, definition, engine, regenerate, identityTies, destination, token)) : null);
        return 0;
    }

    private static async Task<int> InspectSelectedShaderAsync(string[] args)
    {
        const string usage = "Usage: --inspect-shader <content root> <shader GUID> <entry> <engine root> [--arg <expression>] [--pack <pack>] [--target Dxil|Spirv] [--output <file.dxil>]";
        if (args.Length < 5 || !Guid.TryParse(args[2], out Guid id) || id == Guid.Empty)
            throw new ArgumentException(usage);
        string root = Path.GetFullPath(args[1]);
        string engine = Path.GetFullPath(args[4]);
        var values = new List<string>();
        string? pack = null, output = null;
        SlangShaderSharp.SlangCompileTarget target = SlangShaderSharp.SlangCompileTarget.Dxil;
        for (int index = 5; index < args.Length; index++)
        {
            string option = args[index];
            if (++index == args.Length) throw new ArgumentException(usage);
            switch (option)
            {
                case "--arg": values.Add(args[index]); break;
                case "--pack": pack = Path.GetFullPath(args[index]); break;
                case "--output": output = Path.GetFullPath(args[index]); break;
                case "--target": target = args[index] switch
                {
                    "Dxil" => SlangShaderSharp.SlangCompileTarget.Dxil,
                    "Spirv" => SlangShaderSharp.SlangCompileTarget.Spirv,
                    _ => throw new ArgumentException(usage),
                }; break;
                default: throw new ArgumentException(usage);
            }
        }
        await AssetCommands.InspectShaderAsync(root, id, args[3],
            [Path.Combine(engine, "assets", "Shaders"), Path.Combine(root, "assets", "Shaders")], Console.Out,
            values, pack, target, output, Path.Combine(ProjectPaths.Root, "obj"));
        return 0;
    }
#endif
}
