using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SomeEngine.Core.Math;
using SomeEngine.Render.Shadows;
using SomeEngine.Runtime;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[CollectionDefinition("Local light reference", DisableParallelization = true)]
public sealed class LocalLightReferenceCollection;

[Trait("Category", "Hardware")]
[Collection("Local light reference")]
public sealed class LocalLightRayShadowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("point")]
    [InlineData("spot")]
    [InlineData("point-range")]
    [InlineData("spot-cone")]
    public async Task LocalLightShadowsMatchIndependentSourceTriangles(string light)
    {
        using var scene = await LocalScene.CreateAsync(light, output);
        var settings = new VirtualShadowMapSettings { DynamicResolutionEnabled = true };
        SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings });
        SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(scene.Content, light, illuminated: false);
        SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        RayShadowComparison comparison = await scene.CompareAsync(shadowed, unshadowed, ambient);
        AssertAccuracy(comparison);
        if (light == "point-range") Assert.True(comparison.RangeRejectedPixels > 0);
        if (light == "spot-cone") Assert.True(comparison.ConeRejectedPixels > 0);
        if (light.StartsWith("point", StringComparison.Ordinal))
            Assert.True(comparison.CubeFaceReceiverPixels.Count(count => count > 0) >= 2,
                "The point fixture must cross a cube-face boundary without removing either side.");
    }

    [Fact]
    public async Task RepeatedLocalLightStartupAndShutdownPreservesIndependentShadows()
    {
        for (int repetition = 0; repetition < 3; repetition++)
        {
            using var scene = await LocalScene.CreateAsync("spot", output);
            var settings = new VirtualShadowMapSettings { DynamicResolutionEnabled = true };
            SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 64, [])
                { WaitForFrames = false, ShadowSettings = settings });
            SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 64, [])
                { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
            await ShadowReferenceScene.WriteWorldAsync(scene.Content, "spot", illuminated: false);
            SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 64, [])
                { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
            AssertAccuracy(await scene.CompareAsync(shadowed, unshadowed, ambient));
        }
    }

    [Theory]
    [InlineData("point")]
    [InlineData("spot")]
    public async Task LocalLightPagePressureRecoversAfterMovingCasterAndCanceledRefresh(string light)
    {
        using var scene = await LocalScene.CreateAsync(light, output);
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 1024, PageSize = 128,
            MaxShadowLights = 1, DynamicResolutionEnabled = false,
        };
        SceneAction[] actions =
        [
            new(31, edit => edit.CaptureLinearImage(image => image.Save(Path.Combine(scene.Directory, "old-footprint.rgba16")))),
            new(32, edit =>
            {
                edit.SetEntityTransform(edit.MeshEntities[1], new TransformQvvs(new(-1, 0, -6), Quaternion.Identity, .6f));
                edit.RefreshShadowMaps();
                edit.CancelFrame();
            }),
        ];
        SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 64, actions)
            { WaitForFrames = false, ShadowSettings = settings });
        SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 64,
        [
            new(31, edit => edit.CaptureLinearImage(image => image.Save(Path.Combine(scene.Directory, "unshadowed-old.rgba16")))),
            actions[1],
        ]) { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(scene.Content, light, illuminated: false);
        SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 64,
        [
            new(31, edit => edit.CaptureLinearImage(image => image.Save(Path.Combine(scene.Directory, "ambient-old.rgba16")))),
            actions[1],
        ]) { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        Assert.Equal(63, shadowed.Frame.Frame);
        Assert.Equal(62u, shadowed.Frame.SubmittedFrame);
        RayShadowComparison comparison = await scene.CompareAsync(shadowed, unshadowed, ambient);
        RayShadowComparison oldFootprint = await scene.CompareAsync(
            SceneLinearImage.Load(Path.Combine(scene.Directory, "old-footprint.rgba16")),
            SceneLinearImage.Load(Path.Combine(scene.Directory, "unshadowed-old.rgba16")),
            SceneLinearImage.Load(Path.Combine(scene.Directory, "ambient-old.rgba16")), "old-footprint");
        AssertAccuracy(comparison);
        AssertAccuracy(oldFootprint);
    }

    private static void AssertAccuracy(RayShadowComparison comparison)
    {
        Assert.True(comparison.AllReceivers.ShadowedPixels >= 32 && comparison.AllReceivers.LitPixels >= 512);
        Assert.True(comparison.RenderedReceiverCoverage >= .98f);
        Assert.Equal(0, comparison.SelfHitPixels);
        Assert.True(comparison.AllReceivers.MismatchFraction <= .02f &&
            comparison.AllReceivers.MeanVisibilityError <= .02f &&
            comparison.FalseShadowFraction <= .02f && comparison.MissedShadowFraction <= .02f,
            $"Independent local-light visibility: {comparison}");
    }

    private sealed class LocalScene(string engine, string directory, ITestOutputHelper output) : IDisposable
    {
        internal string Directory => directory;
        internal string Content => Path.Combine(directory, "content");
        private readonly List<object> _captures = [];

        internal static async Task<LocalScene> CreateAsync(string light, ITestOutputHelper output)
        {
            string engine = FindEngine();
            string? evidence = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_EVIDENCE");
            string directory = Path.Combine(evidence ?? Path.GetTempPath(), $"local-ray-{light}-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var scene = new LocalScene(engine, directory, output);
            try { await ShadowReferenceScene.BuildAsync(engine, scene.Content, light); return scene; }
            catch { scene.Dispose(); throw; }
        }

        internal async Task<SceneLinearImage> CaptureAsync(string name, SceneScript script, params string[] options)
        {
            string raw = Path.Combine(directory, name + ".rgba16");
            string[] arguments = ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
                "--frames", script.FrameCount.ToString(),
                "--no-debug-ui", "--fixed-step", "0.016666667", "--linear-frame-output", raw,
                "--frame-output", Path.Combine(directory, name + ".bmp"), .. options];
            var contentFiles = System.IO.Directory.EnumerateFiles(Path.Combine(Content, "assets"), "*.asset", SearchOption.AllDirectories)
                .Append(Path.Combine(Content, "Library/AssetManifest/catalog.json"))
                .OrderBy(path => path, StringComparer.Ordinal).Select(path => new
                {
                    File = Path.GetRelativePath(Content, path),
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                }).ToArray();
            ApplicationResult result = await Application.RunAsync(arguments, script);
            var capture = new { Arguments = arguments, result.Succeeded, result.CompletedFrames, result.Failure,
                SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                ContentFiles = contentFiles, script.FrameCount,
                Actions = script.Actions.Select(action => action.Frame).ToArray(), script.WaitForFrames, script.ShadowSettings };
            _captures.Add(capture);
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".capture.json"),
                JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(result.Succeeded, $"{result.Failure}; {result.Error}");
            return SceneLinearImage.Load(raw);
        }

        internal async Task<RayShadowComparison> CompareAsync(SceneLinearImage shadowed,
            SceneLinearImage unshadowed, SceneLinearImage ambient, string? evidenceName = null)
        {
            SceneFrame frame = shadowed.Frame;
            Assert.Equal(frame.Projection, unshadowed.Frame.Projection);
            Assert.Equal(frame.Projection, ambient.Frame.Projection);
            Assert.Equal(frame.Meshes, unshadowed.Frame.Meshes);
            Assert.Equal(frame.Meshes, ambient.Frame.Meshes);
            var rays = new RayShadowScene(Path.Combine(engine, "templates/DefaultProject/assets/Models/ico_sphere.glb"),
                frame.Meshes.Select(mesh => mesh.Transform).ToArray());
            SceneLight lamp = frame.Lights.Single();
            Assert.True(Matrix4x4.Invert(frame.View * frame.Projection, out Matrix4x4 clipToWorld));
            uint[] reference = await RayShadowImage.TraceAsync(engine, rays, new()
            {
                ClipToWorld = clipToWorld, CameraPosition = frame.CameraPosition,
                Width = (uint)frame.Width, Height = (uint)frame.Height,
                LightKind = lamp.Kind == SceneLightKind.Point ? 1u : 2u,
                LightPosition = lamp.Position, LightDirection = lamp.Direction,
                LightRange = lamp.Range, OuterConeCos = lamp.OuterConeCos,
                NearPlane = frame.NearPlane, FarPlane = frame.FarPlane,
            }, vulkan: false);
            var actual = new Vector3[frame.Width * frame.Height];
            for (int pixel = 0; pixel < actual.Length; pixel++)
            {
                int channel = pixel * 4;
                float direct = (float)unshadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
                float shaded = (float)shadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
                float visibility = direct > 0 ? shaded / direct : float.NaN;
                // No radiance/shading threshold removes receivers. An indeterminate ratio
                // is a full error, not an excluded edge or a silently lit pixel.
                actual[pixel] = new(visibility, (float)shadowed.Pixels.Span[channel + 3], direct > 0 ? 1 : 0);
            }
            string comparisonDirectory = evidenceName is null ? directory : Path.Combine(directory, evidenceName);
            System.IO.Directory.CreateDirectory(comparisonDirectory);
            RayShadowComparison comparison = RayShadowComparison.Write(comparisonDirectory, reference, frame.Width, frame.Height, actual);
            await File.WriteAllTextAsync(Path.Combine(comparisonDirectory, "comparison.json"), JsonSerializer.Serialize(new
            {
                SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                Frame = frame, Captures = _captures, Comparison = comparison,
                Reference = "Independent source triangles and geometric-normal ULP origin; authored point range/cube-face or spot range/cone; actual submitted projection and transforms",
                Observation = "Unfiltered Standard user-material color: (shadowed - ambient)/(unshadowed - ambient). All eligible source receivers retained; nonpositive direct radiance is an error, not a denominator filter. No source replacement.",
                Pressure = "Configured 64-page local-light fixture is page pressure, not proof of a particular resident/missing mip. No private page probes.",
                Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMismatchFraction = .02f,
                    MaximumMeanVisibilityError = .02f, MaximumFalseShadowFraction = .02f,
                    MaximumMissedShadowFraction = .02f, MinimumShadowedPixels = 32,
                    MinimumLitPixels = 512, MinimumRenderedReceiverCoverage = .98f, MaximumSelfHits = 0 },
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            output.WriteLine($"Local light independent reference: {directory}; {comparison}");
            return comparison;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Content)) System.IO.Directory.Delete(Content, recursive: true);
            if (Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_EVIDENCE") is null)
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select a SomeEngine checkout for the independent local-light reference.");
    }
}
