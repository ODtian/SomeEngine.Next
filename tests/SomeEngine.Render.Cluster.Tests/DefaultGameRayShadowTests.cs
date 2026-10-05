using System.Numerics;
using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DefaultGameRayShadowTests(ITestOutputHelper output)
{
    private const int CaptureFrames = 64;
    private const float FixedStep = .016666667f;

    [Theory]
    [InlineData("spheres")]
    [InlineData("sponza")]
    [InlineData("bistro")]
    public Task ThreeSceneShadowsMatchHardwareRayTracing(string scene) => CaptureSceneAsync(scene,
        "diagnostic", ["--static-scene", "--synchronous-frames", "--no-taa", "--no-async-compute",
            "--hardware-raster", "--no-mesh-shaders"], 0);

    [Theory]
    [InlineData("spheres")]
    [InlineData("sponza")]
    [InlineData("bistro")]
    public Task DefaultSceneShadowsMatchHardwareRayTracing(string scene) => CaptureSceneAsync(scene,
        "default", [scene == "spheres" ? "--dynamic-scene" : "--static-scene"],
        scene == "spheres" ? (CaptureFrames - 1) * FixedStep : 0);

    [Fact]
    public async Task GeometricOcclusionIsIndependentOfNormalTextures()
    {
        string engine = FindEngine();
        var scene = new RayShadowScene(Path.Combine(engine, "templates", "DefaultProject", "assets", "Models", "ico_sphere.glb"),
            [Matrix4x4.CreateScale(ShadowReferenceScene.ReceiverScale) * Matrix4x4.CreateTranslation(ShadowReferenceScene.ReceiverPosition),
             Matrix4x4.CreateScale(ShadowReferenceScene.CasterScale) * Matrix4x4.CreateTranslation(ShadowReferenceScene.CasterPosition)]);
        Matrix4x4.Invert(Matrix4x4.CreateLookAt(Vector3.Zero, ShadowReferenceScene.ReceiverPosition, Vector3.UnitY) *
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), out Matrix4x4 clipToWorld);
        var inputs = new RayShadowImage.Inputs
        {
            ClipToWorld = clipToWorld, CameraPosition = Vector3.Zero, Width = 96, Height = 96,
            LightDirection = ShadowReferenceScene.LightDirection, NearPlane = .1f, FarPlane = 100,
        };
        uint[] first = await RayShadowImage.TraceAsync(engine, scene, inputs, vulkan: false);
        scene.Texels[0] = 0xff0080ff;
        uint[] second = await RayShadowImage.TraceAsync(engine, scene, inputs, vulkan: false);
        Assert.Equal(first.Select(value => value & 3u), second.Select(value => value & 3u));
        Assert.True(first.Count(value => (value & 3u) <= 1u) >= 512);
        Assert.True(first.Count(value => (value & 3u) == 0u) >= 32);
        Assert.True(first.Where((value, index) => (value & 4u) != (second[index] & 4u)).Any(),
            "The normal texture variation must change the separately reported shading condition.");
        Assert.DoesNotContain(first.Concat(second), value => (value & 3u) <= 1u && (value & 8u) != 0);
    }

    private async Task CaptureSceneAsync(string scene, string mode, string[] renderingOptions, float captureSeconds)
    {
        string engine = FindEngine();
        string? evidence = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_EVIDENCE");
        string directory = Path.Combine(evidence ?? Path.GetTempPath(), $"ray-shadow-{scene}-{mode}-{Guid.NewGuid():N}");
        string content = Path.Combine(directory, "content");
        Directory.CreateDirectory(directory);
        try
        {
            bool spheres = scene == "spheres";
            string sourceContent = spheres ? Path.Combine(engine, "templates", "DefaultProject")
                : Path.Combine(engine, "samples", "ImportedScenes", "Apps", scene == "sponza" ? "Sponza" : "Bistro");
            Assert.True(Directory.Exists(Path.Combine(sourceContent, "Library", "AssetManifest")),
                $"Build the DefaultGame {scene} content before running its RT shadow regression.");
            CopyContent(sourceContent, content);
            SceneInputs inputs = await ReadSceneInputsAsync(content, captureSeconds);
            string application = Path.Combine(engine, "templates", "DefaultProject", "Game.App", "bin", "Release", "net10.0", "Game.App.exe");
            List<DefaultGameCapture.Result> captures = [];
            async Task Capture(string file)
            {
                string image = Path.Combine(directory, file);
                string[] arguments = ["--test-scene", content, "--frames", CaptureFrames.ToString(),
                    "--hidden-window", "--fixed-step", "0.016666667", "--no-vsync", "--no-debug-ui",
                    "--vsm", .. renderingOptions, "--frame-output", image];
                captures.Add(await DefaultGameCapture.RunAsync(application, arguments, image, output));
            }
            await Capture("scene.bmp");
            string geometry = spheres ? Path.Combine(sourceContent, "assets", "Models", "ico_sphere.glb")
                : Path.Combine(sourceContent, "assets", "Imported", scene == "sponza" ? "Sponza" : "Bistro",
                    scene == "sponza" ? "Sponza.gltf" : "Bistro.glb");
            await ShadowVisibilityContent.BuildAsync(engine, sourceContent, content, geometry, spheres);
            await Capture("vsm-shadow.bmp");
            var rays = spheres ? new RayShadowScene(geometry, inputs.MeshTransforms) : new RayShadowScene(geometry, false);
            Matrix4x4.Invert(Matrix4x4.CreateLookAt(inputs.Camera.Position, inputs.Camera.Target, inputs.Camera.Up) *
                Matrix4x4.CreatePerspectiveFieldOfView(inputs.Camera.VerticalFieldOfView,
                    (float)inputs.Width / inputs.Height, inputs.Camera.NearPlane, inputs.Camera.FarPlane), out Matrix4x4 clipToWorld);
            uint[] reference = await RayShadowImage.TraceAsync(engine, rays, new()
            {
                ClipToWorld = clipToWorld, CameraPosition = inputs.Camera.Position,
                Width = (uint)inputs.Width, Height = (uint)inputs.Height,
                LightDirection = Vector3.Normalize(inputs.Light.Direction), LightKind = 0,
                NearPlane = inputs.Camera.NearPlane, FarPlane = inputs.Camera.FarPlane,
            }, vulkan: false);
            RayShadowComparison comparison = RayShadowComparison.Write(directory, reference, inputs.Width, inputs.Height);
            await File.WriteAllTextAsync(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(new
            {
                Scene = scene, Mode = mode, SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                Reference = "Source glTF BLAS/TLAS; original triangle geometry and alpha masks; barycentric surface position; geometric-normal ULP ray origin; shading conditions reported independently",
                Camera = inputs.Camera, Light = inputs.Light, inputs.Width, inputs.Height,
                CaptureFrames, FixedStep, CaptureSeconds = captureSeconds, RenderingOptions = renderingOptions,
                Captures = captures, MeshTransforms = inputs.MeshTransforms,
                SourceGeometry = new { Meshes = rays.Meshes.Length, Instances = rays.Instances.Length,
                    Triangles = rays.Indices.Length / 3, AlphaMaskedGeometries = rays.Geometries.Count(value => value.AlphaCutoff >= 0) },
                PixelChannels = "Inverse-tone-mapped red = shadow visibility * temporal receiver coverage; green = receiver coverage; blue = shading condition. Visibility is red/green.",
                BoundaryDefinition = "Reference visibility or geometric eligibility changes within a 5x5 neighborhood. All receiver pixels remain in the aggregate, images and CSV.",
                Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMismatchFraction = .02f,
                    MaximumMeanVisibilityError = .02f, MaximumFalseShadowFraction = .02f,
                    MaximumMissedShadowFraction = .02f, MinimumShadowedPixels = 32,
                    MinimumLitPixels = 512, MinimumRenderedReceiverCoverage = .98f },
                Comparison = comparison,
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            output.WriteLine($"Hardware RT shadow comparison: {directory}; {comparison}");
            ShadowReferenceComparison all = comparison.AllReceivers;
            Assert.True(all.ShadowedPixels >= 32 && all.LitPixels >= 512,
                $"The scene must cover both geometric shadow and light receivers. {comparison}");
            Assert.True(comparison.RenderedReceiverCoverage >= .98f, $"Receiver coverage: {comparison}");
            Assert.Equal(0, comparison.SelfHitPixels);
            Assert.True(all.MeanVisibilityError <= .02f && all.MismatchFraction <= .02f &&
                all.FalseShadowPixels <= all.LitPixels * .02f && all.MissedShadowPixels <= all.ShadowedPixels * .02f,
                $"DefaultGame {scene}/{mode} differs from hardware RT shadows. {comparison}. Evidence: {directory}");
        }
        finally
        {
            if (Directory.Exists(content)) Directory.Delete(content, recursive: true);
            if (evidence is null) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record SceneInputs(SceneCamera Camera, DirectionalLight Light, int Width, int Height, Matrix4x4[] MeshTransforms);

    private static async Task<SceneInputs> ReadSceneInputsAsync(string content, float seconds)
    {
        AssetManifest manifest = AssetManifest.Load(Path.Combine(content, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(content, manifest));
        using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(assets,
            new(manifest.List(ApplicationSettingsAsset.FileType).Single().AssetGuid));
        ApplicationSettingsBlock values = settings.GetBlock<ApplicationSettingsBlock>();
        using WorldAsset file = await WorldAsset.OpenAsync(assets, values.Scene);
        using World world = await file.LoadAsync(SceneSerialization.CreateRegistry());
        Entity camera = Entities(new QueryDefinitionBuilder().Read<SceneCamera>()).Single();
        Entity light = Entities(new QueryDefinitionBuilder().Read<DirectionalLight>()).First();
        Matrix4x4[] transforms = Entities(new QueryDefinitionBuilder().Read<WorldTransform>().Read<MeshSource>()).Select(entity =>
        {
            TransformQvvs transform = world.Read<WorldTransform>(entity).Qvvs;
            if (seconds != 0 && world.Has<MeshMotion>(entity))
            {
                MeshMotion motion = world.Read<MeshMotion>(entity);
                Vector3 frequency = new(.55f + Unit(Hash(motion.Seed ^ 0xA511E9B3u)) * .35f,
                    .70f + Unit(Hash(motion.Seed ^ 0x63D83595u)) * .45f,
                    .42f + Unit(Hash(motion.Seed ^ 0xB5297A4Du)) * .31f);
                transform.Position += new Vector3(MathF.Sin(seconds * frequency.X) * motion.Amplitude.X,
                    MathF.Sin(seconds * frequency.Y) * motion.Amplitude.Y, MathF.Sin(seconds * frequency.Z) * motion.Amplitude.Z);
            }
            return transform.ToMatrix();
        }).ToArray();
        return new(world.Read<SceneCamera>(camera), world.Read<DirectionalLight>(light), values.Width, values.Height, transforms);

        Entity[] Entities(QueryDefinitionBuilder definition)
        {
            QueryHandle query = world.Query(definition);
            var entities = new List<Entity>();
            try
            {
                world.ExecuteQuery(query, ref entities, static (QueryCursor rows, ref List<Entity> values) =>
                {
                    foreach (var row in rows.Rows) values.Add(row.Entity);
                });
                return entities.ToArray();
            }
            finally { world.ReleaseQuery(query); }
        }
        static uint Hash(uint value)
        {
            unchecked { value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15; value *= 0x846CA68Bu; return value ^ (value >> 16); }
        }
        static float Unit(uint value) => (value >> 8) * (1f / 16_777_215f);
    }

    private static void CopyContent(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "assets"), "*.asset", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(source, "Library", "AssetManifest"), "*", SearchOption.AllDirectories)))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select a SomeEngine checkout for the DefaultGame RT shadow regression.");
    }
}
