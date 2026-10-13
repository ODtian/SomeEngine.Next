using System.Numerics;
using System.Text.Json;
using SomeEngine.Runtime;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

public sealed partial class DefaultGameRayShadowTests
{
    [Theory]
    [InlineData("spheres")]
    [InlineData("sponza")]
    [InlineData("bistro")]
    public Task DefaultSoftShadowsMatchIndependentSourceEmitters(string scene) =>
        CaptureDefaultSoftShadowsAsync(scene, ClusterShadingPath.Deferred);

    [Theory]
    [InlineData("spheres")]
    [InlineData("sponza")]
    [InlineData("bistro")]
    public Task DefaultForwardSoftShadowsMatchIndependentSourceEmitters(string scene) =>
        CaptureDefaultSoftShadowsAsync(scene, ClusterShadingPath.Forward);

    private async Task CaptureDefaultSoftShadowsAsync(string scene, ClusterShadingPath shadingPath)
    {
        string engine = FindEngine();
        using var evidence = new ShadowEvidence($"soft-ray-shadow-{scene}-{shadingPath.ToString().ToLowerInvariant()}");
        bool spheres = scene == "spheres";
        string sourceContent = spheres ? Path.Combine(engine, "templates", "DefaultProject")
            : Path.Combine(engine, "samples", "ImportedScenes", "Apps", scene == "sponza" ? "Sponza" : "Bistro");
        CopyContent(sourceContent, evidence.Content);
        string application = Path.Combine(engine, "templates", "DefaultProject", "Game.App", "bin", "Release", "net10.0", "Game.App.exe");
        var captures = new List<DefaultGameCapture.Result>();
        async Task<SceneLinearImage> Capture(string name)
        {
            string image = Path.Combine(evidence.Directory, name + ".bmp");
            string linear = Path.Combine(evidence.Directory, name + ".rgba16");
            string[] arguments = ["--test-scene", evidence.Content, "--frames", CaptureFrames.ToString(),
                "--hidden-window", "--fixed-step", "0.016666667", "--no-vsync", "--no-debug-ui", "--vsm",
                "--shading-path", shadingPath.ToString().ToLowerInvariant(),
                spheres ? "--dynamic-scene" : "--static-scene", "--frame-output", image, "--linear-frame-output", linear];
            captures.Add(await DefaultGameCapture.RunAsync(application, arguments, image, output));
            return SceneLinearImage.Load(linear);
        }
        SceneLinearImage beauty = await Capture("scene");
        string geometry = spheres ? Path.Combine(sourceContent, "assets", "Models", "ico_sphere.glb")
            : Path.Combine(sourceContent, "assets", "Imported", scene == "sponza" ? "Sponza" : "Bistro",
                scene == "sponza" ? "Sponza.gltf" : "Bistro.glb");
        using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(
            Path.Combine(sourceContent, "assets", "Runtime", "default.runtime.asset"));
        await ShadowVisibilityContent.BuildAsync(engine, sourceContent, evidence.Content, [settings.Id], shadingPath);
        SceneLinearImage visibility = await Capture("vsm-shadow");
        SceneFrame frame = visibility.Frame;
        Assert.Equal(CaptureFrames - 1, frame.Frame);
        Assert.Equal((uint)(CaptureFrames - 1), frame.SubmittedFrame);
        Assert.Equal(shadingPath, beauty.Frame.Options.ShadingPath);
        Assert.Equal(shadingPath, frame.Options.ShadingPath);
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
        Assert.Equal(4u, frame.ShadowSettings.BlockerSampleCount);
        Assert.Equal(8u, frame.ShadowSettings.FilterSampleCount);
        Assert.Equal(8f, frame.ShadowSettings.MaxFilterRadiusTexels);
        var rays = spheres ? new RayShadowScene(geometry, frame.Meshes.Select(mesh => mesh.Transform).ToArray())
            : new RayShadowScene(geometry);
        if (!spheres) MatchStaticSourceTransforms(rays, frame.Meshes);
        SceneLight light = frame.Lights.First(value => value.Kind == SceneLightKind.Directional);
        Assert.True(Matrix4x4.Invert(frame.View * frame.Projection, out Matrix4x4 clipToWorld));
        float sourceAngle = (light.ShadowSettings.SourceAngleDegrees ?? .5357f) * MathF.PI / 180;
        RayShadowImage.SoftPixel[] reference = await RayShadowImage.TraceSoftAsync(engine, rays, new()
        {
            ClipToWorld = clipToWorld, CameraPosition = frame.CameraPosition,
            Width = (uint)frame.Width, Height = (uint)frame.Height,
            LightKind = 0, LightDirection = Vector3.Normalize(light.Direction), SourceAngleRadians = sourceAngle,
            NearPlane = frame.NearPlane, FarPlane = frame.FarPlane, SampleCount = RayShadowImage.SourceSampleCount,
        }, vulkan: false);
        RayShadowSoftComparison comparison = RayShadowSoftComparison.Write(evidence.Directory, reference, frame.Width, frame.Height);
        await File.WriteAllTextAsync(Path.Combine(evidence.Directory, "comparison.json"), JsonSerializer.Serialize(new
        {
            Scene = scene, ShadingPath = shadingPath.ToString(),
            SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
            Frame = frame, Captures = captures, Comparison = comparison,
            Emitter = new { SourceAngleRadians = sourceAngle, light.ShadowSettings.SourceRadius },
            Reference = "Independent source glTF BLAS/TLAS, original UV alpha masks, actual source transforms and submitted camera, geometric-normal ULP origin; uniform solid-angle integration of the authored solar disk",
            ReferenceSamples = new { Samples = RayShadowImage.SourceSampleCount, Sampling = "Shifted low-discrepancy integration over source solid angle" },
            Observation = "Raw unfiltered visibility/receiver coverage. Every eligible source receiver contributes; all penumbra and boundary pixels remain in the comparison.",
            Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMismatchFraction = .02f,
                MaximumMeanVisibilityError = .02f, MinimumRenderedReceiverCoverage = .98f, MaximumSelfHits = 0 },
            MeetsSourceGeometryAccuracyThresholds = comparison.MeanVisibilityError <= .02f && comparison.MismatchFraction <= .02f,
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        output.WriteLine($"Independent default soft-shadow reference: {evidence.Directory}; {comparison}");
        Assert.True(comparison.ShadowedPixels >= 32 && comparison.LitPixels >= 512, $"Receiver coverage: {comparison}");
        Assert.True(comparison.RenderedReceiverCoverage >= .98f, $"Receiver coverage: {comparison}");
        Assert.Equal(0, comparison.SelfHitPixels);
        Assert.True(comparison.MeanVisibilityError <= .02f, $"Mean soft visibility error: {comparison}");
        Assert.True(comparison.MismatchFraction <= .02f, $"Soft visibility mismatch: {comparison}");
        evidence.Complete();
    }
}
