using System.Numerics;
using System.Text.Json;
using SomeEngine.Runtime;
using SomeEngine.Assets.Schema;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed partial class DefaultGameRayShadowTests(ITestOutputHelper output)
{
    private const int CaptureFrames = 64;

    [Theory]
    [InlineData("spheres")]
    [InlineData("sponza")]
    [InlineData("bistro")]
    public Task ThreeSceneShadowDiagnosticsCoverSourceGeometryReceivers(string scene) => CaptureSceneAsync(scene,
        "diagnostic", ["--static-scene", "--synchronous-frames", "--no-taa", "--no-async-compute",
            "--hardware-raster", "--no-mesh-shaders"]);

    [Theory]
    [InlineData("spheres")]
    [InlineData("sponza")]
    [InlineData("bistro")]
    public Task DefaultSceneShadowDiagnosticsCoverSourceGeometryReceivers(string scene) => CaptureSceneAsync(scene,
        "default", [scene == "spheres" ? "--dynamic-scene" : "--static-scene"]);

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

    private async Task CaptureSceneAsync(string scene, string mode, string[] renderingOptions)
    {
        string engine = FindEngine();
        using var evidence = new ShadowEvidence($"ray-shadow-{scene}-{mode}");
        string directory = evidence.Directory;
        string content = evidence.Content;
        {
            bool spheres = scene == "spheres";
            string sourceContent = spheres ? Path.Combine(engine, "templates", "DefaultProject")
                : Path.Combine(engine, "samples", "ImportedScenes", "Apps", scene == "sponza" ? "Sponza" : "Bistro");
            Assert.True(Directory.Exists(Path.Combine(sourceContent, "Library", "AssetManifest")),
                $"Build the DefaultGame {scene} content before running its RT shadow regression.");
            CopyContent(sourceContent, content);
            string application = Path.Combine(engine, "templates", "DefaultProject", "Game.App", "bin", "Release", "net10.0", "Game.App.exe");
            List<DefaultGameCapture.Result> captures = [];
            async Task<SceneLinearImage> Capture(string name)
            {
                string image = Path.Combine(directory, name + ".bmp");
                string linear = Path.Combine(directory, name + ".rgba16");
                string[] arguments = ["--test-scene", content, "--frames", CaptureFrames.ToString(),
                    "--hidden-window", "--fixed-step", "0.016666667", "--no-vsync", "--no-debug-ui",
                    "--vsm", "--vsm-ray-count", "0", .. renderingOptions, "--frame-output", image, "--linear-frame-output", linear];
                captures.Add(await DefaultGameCapture.RunAsync(application, arguments, image, output));
                SceneLinearImage result = SceneLinearImage.Load(linear);
                Assert.Equal(CaptureFrames - 1, result.Frame.Frame);
                Assert.Equal((uint)(CaptureFrames - 1), result.Frame.SubmittedFrame);
                return result;
            }
            SceneLinearImage beauty = await Capture("scene");
            string geometry = spheres ? Path.Combine(sourceContent, "assets", "Models", "ico_sphere.glb")
                : Path.Combine(sourceContent, "assets", "Imported", scene == "sponza" ? "Sponza" : "Bistro",
                    scene == "sponza" ? "Sponza.gltf" : "Bistro.glb");
            using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(
                Path.Combine(sourceContent, "assets", "Runtime", "default.runtime.asset"));
            await ShadowVisibilityContent.BuildAsync(engine, sourceContent, content, [settings.Id]);
            SceneLinearImage visibility = await Capture("vsm-shadow");
            SceneFrame frame = visibility.Frame;
            Assert.Equal(beauty.Frame.View, frame.View);
            Assert.Equal(beauty.Frame.Projection, frame.Projection);
            Assert.Equal(beauty.Frame.JitterPixels, frame.JitterPixels);
            Assert.Equal(beauty.Frame.SceneSeconds, frame.SceneSeconds);
            Assert.Equal(beauty.Frame.Meshes, frame.Meshes);
            Assert.Equal(beauty.Frame.Lights, frame.Lights);
            Assert.Equal(beauty.Frame.ShadowSettings, frame.ShadowSettings);
            Assert.Equal(beauty.Frame.TemporalSettings, frame.TemporalSettings);
            Assert.Equal(beauty.Frame.RasterEdgePixels, frame.RasterEdgePixels);
            Assert.Equal(beauty.Frame.ClusterWorkCapacity, frame.ClusterWorkCapacity);
            Assert.Equal(beauty.Frame.Adapter, frame.Adapter);
            Assert.Equal(beauty.Frame.DriverVersion, frame.DriverVersion);
            var rays = spheres ? new RayShadowScene(geometry, frame.Meshes.Select(mesh => mesh.Transform).ToArray())
                : new RayShadowScene(geometry);
            if (!spheres) MatchStaticSourceTransforms(rays, frame.Meshes);
            Assert.True(Matrix4x4.Invert(frame.View * frame.Projection, out Matrix4x4 clipToWorld));
            SceneLight light = frame.Lights.First(value => value.Kind == SceneLightKind.Directional);
            uint[] reference = await RayShadowImage.TraceAsync(engine, rays, new()
            {
                ClipToWorld = clipToWorld, CameraPosition = frame.CameraPosition,
                Width = (uint)frame.Width, Height = (uint)frame.Height,
                LightDirection = Vector3.Normalize(light.Direction), LightKind = 0,
                NearPlane = frame.NearPlane, FarPlane = frame.FarPlane,
            }, vulkan: false);
            RayShadowComparison comparison = RayShadowComparison.Write(directory, reference, frame.Width, frame.Height);
            BistroShadowBaseline? baseline = scene == "bistro" && mode == "default"
                ? BistroShadowBaseline.Read(Path.Combine(engine, "tests", "SomeEngine.Render.Cluster.Tests",
                    "Baselines", "bistro-hard-default.json")) : null;
            bool? withinBaseline = baseline?.Accepts(ShadowAccuracyObservation.From(comparison));
            await File.WriteAllTextAsync(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(new
            {
                Scene = scene, Mode = mode, SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                Reference = "Source glTF BLAS/TLAS; original triangles and UV alpha masks; barycentric geometric position; geometric-normal ULP origin; inverse actual jittered submitted projection; mapped-normal shading reported separately",
                ReferenceScope = "Highest-detail source geometry accuracy diagnostics. Native rendering uses distance-selected primary and shadow LODs, texel-center depth samples and mapped-normal receiver bias.",
                Frame = frame, Captures = captures,
                SourceGeometry = new { Meshes = rays.Meshes.Length, Instances = rays.Instances.Length,
                    Triangles = rays.Indices.Length / 3, AlphaMaskedGeometries = rays.Geometries.Count(value => value.AlphaCutoff >= 0) },
                PixelChannels = "Raw linear RGBA16F before TAA/tone mapping: red = visibility, green = receiver coverage, blue = mapped-normal shading eligibility. Beauty is retained separately; it is not numerical truth.",
                BoundaryDefinition = "Reference visibility or geometric eligibility changes within a 5x5 neighborhood. All valid receivers remain in aggregate, images and CSV.",
                Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMismatchFraction = .02f,
                    MaximumMeanVisibilityError = .02f, MinimumShadowedPixels = 32,
                    MinimumLitPixels = 512, MinimumRenderedReceiverCoverage = .98f },
                MeetsSourceGeometryAccuracyThresholds = comparison.AllReceivers.MeanVisibilityError <= .02f &&
                    comparison.AllReceivers.MismatchFraction <= .02f,
                QualityVerdict = "Source-geometry quality diagnostic; not an exact selected-geometry/texel-center oracle.",
                CalibratedBaseline = baseline is null ? "Original 2% source-geometry accuracy limits" : "bistro-hard-default.json",
                MeetsCalibratedBaseline = withinBaseline,
                Comparison = comparison,
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            output.WriteLine($"Highest-detail source geometry shadow diagnostics: {directory}; {comparison}");
            ShadowReferenceComparison all = comparison.AllReceivers;
            Assert.True(all.ShadowedPixels >= 32 && all.LitPixels >= 512,
                $"The scene must cover both geometric shadow and light receivers. {comparison}");
            Assert.True(comparison.RenderedReceiverCoverage >= .98f, $"Receiver coverage: {comparison}");
            Assert.Equal(0, comparison.SelfHitPixels);
            if (baseline is not null)
            {
                Assert.True(baseline.MatchesSource(geometry), "Source geometry changed; review the quality baseline.");
                Assert.True(baseline.MatchesFrame(frame), "Capture configuration changed; the quality baseline is not applicable.");
                Assert.True(withinBaseline == true, $"Source-geometry quality regressed from the calibrated baseline: {comparison}");
            }
            else
            {
                Assert.True(BistroShadowBaseline.AcceptsUnprofiled(ShadowAccuracyObservation.From(comparison)),
                    $"Unprofiled source-geometry accuracy still requires <= 2% mismatch and mean error: {comparison}");
            }
            evidence.Complete();
        }
    }

    private static void MatchStaticSourceTransforms(RayShadowScene rays, SceneMeshTransform[] captured)
    {
        Assert.Equal(rays.Instances.Length, captured.Length);
        var remaining = captured.Select(mesh => mesh.Transform).ToList();
        for (int index = 0; index < rays.Instances.Length; index++)
        {
            Matrix4x4 source = rays.Instances[index].Transform;
            int match = remaining.FindIndex(transform => MatchesTransform(source, transform));
            Assert.True(match >= 0, "Each static source glTF instance must have a matching submitted scene transform.");
            rays.Instances[index] = rays.Instances[index] with { Transform = remaining[match] };
            remaining.RemoveAt(match);
        }
        Assert.Empty(remaining);

        static bool MatchesTransform(Matrix4x4 a, Matrix4x4 b) =>
            MathF.Abs(a.M11 - b.M11) <= .0001f && MathF.Abs(a.M12 - b.M12) <= .0001f &&
            MathF.Abs(a.M13 - b.M13) <= .0001f && MathF.Abs(a.M14 - b.M14) <= .0001f &&
            MathF.Abs(a.M21 - b.M21) <= .0001f && MathF.Abs(a.M22 - b.M22) <= .0001f &&
            MathF.Abs(a.M23 - b.M23) <= .0001f && MathF.Abs(a.M24 - b.M24) <= .0001f &&
            MathF.Abs(a.M31 - b.M31) <= .0001f && MathF.Abs(a.M32 - b.M32) <= .0001f &&
            MathF.Abs(a.M33 - b.M33) <= .0001f && MathF.Abs(a.M34 - b.M34) <= .0001f &&
            MathF.Abs(a.M41 - b.M41) <= .0001f && MathF.Abs(a.M42 - b.M42) <= .0001f &&
            MathF.Abs(a.M43 - b.M43) <= .0001f && MathF.Abs(a.M44 - b.M44) <= .0001f;
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
