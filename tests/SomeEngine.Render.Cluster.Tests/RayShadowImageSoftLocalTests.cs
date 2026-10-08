using System.Numerics;
using System.Text.Json;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Runtime;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
[Collection("Local light reference")]
public sealed class RayShadowImageSoftLocalTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(SceneLightKind.Point)]
    [InlineData(SceneLightKind.Spot)]
    public async Task DefaultSoftLocalShadowsMatchTheAuthoredSourceRadius(SceneLightKind kind)
    {
        string engine = FindEngine();
        using var evidence = new ShadowEvidence("soft-local-ray-" + kind);
        var inputs = new ShadowReferenceInputs(256, 256,
            new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(new(0, 0, -8), new(4, 4, .1f), true), new(new(0, 0, -6), new(.6f), false)],
            world =>
            {
                var emitter = new LightShadowSettings(SourceRadius: .5f);
                if (kind == SceneLightKind.Point)
                    world.CreateEntity(new PointLight(new(-4, 2, -3), 20, Vector3.One, 30, ShadowSettings: emitter));
                else world.CreateEntity(new SpotLight(new(-4, 2, -3), 20, Vector3.Normalize(new(4, -2, -5)),
                    MathF.Cos(MathF.PI / 12), MathF.Cos(MathF.PI * 2 / 9), Vector3.One, 30, ShadowSettings: emitter));
            });
        await ShadowReferenceScene.BuildAsync(engine, evidence.Content, inputs);
        var captures = new List<object>();
        async Task<SceneLinearImage> Capture(string name, params string[] options)
        {
            string linear = Path.Combine(evidence.Directory, name + ".rgba16");
            string[] arguments = ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
                "--frames", "64", "--no-debug-ui", "--fixed-step", "0.016666667", "--linear-frame-output", linear,
                "--frame-output", Path.Combine(evidence.Directory, name + ".bmp"), .. options];
            ApplicationResult result = await Application.RunAsync(arguments,
                new SceneScript(evidence.Content, 64, []) { WaitForFrames = false });
            captures.Add(new { Arguments = arguments, result.Succeeded, result.CompletedFrames, result.Failure });
            Assert.True(result.Succeeded, $"{result.Failure}; {result.Error}");
            return SceneLinearImage.Load(linear);
        }
        SceneLinearImage shadowed = await Capture("shadowed");
        SceneLinearImage unshadowed = await Capture("unshadowed", "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(evidence.Content, inputs with { AddLights = null });
        SceneLinearImage ambient = await Capture("ambient", "--no-vsm");
        SceneFrame frame = shadowed.Frame;
        Assert.Equal(frame.View, unshadowed.Frame.View);
        Assert.Equal(frame.View, ambient.Frame.View);
        Assert.Equal(frame.Projection, unshadowed.Frame.Projection);
        Assert.Equal(frame.Projection, ambient.Frame.Projection);
        Assert.Equal(frame.Meshes, unshadowed.Frame.Meshes);
        Assert.Equal(frame.Meshes, ambient.Frame.Meshes);
        Assert.Equal(4u, frame.ShadowSettings.BlockerSampleCount);
        Assert.Equal(8u, frame.ShadowSettings.FilterSampleCount);
        Assert.Equal(8f, frame.ShadowSettings.MaxFilterRadiusTexels);
        SceneLight emitter = frame.Lights.Single();
        Assert.Equal(.5f, emitter.ShadowSettings.SourceRadius);
        Assert.True(Matrix4x4.Invert(frame.View * frame.Projection, out Matrix4x4 clipToWorld));
        var rays = new RayShadowScene(Path.Combine(engine, "templates", "DefaultProject", "assets", "Models", "ico_sphere.glb"),
            frame.Meshes.Select(mesh => mesh.Transform).ToArray());
        RayShadowImage.SoftPixel[] reference = await RayShadowImage.TraceSoftAsync(engine, rays, new()
        {
            ClipToWorld = clipToWorld, CameraPosition = frame.CameraPosition,
            Width = (uint)frame.Width, Height = (uint)frame.Height,
            LightKind = kind == SceneLightKind.Point ? 1u : 2u,
            LightPosition = emitter.Position, LightDirection = emitter.Direction,
            LightRange = emitter.Range, OuterConeCos = emitter.OuterConeCos,
            SourceRadius = emitter.ShadowSettings.SourceRadius,
            NearPlane = frame.NearPlane, FarPlane = frame.FarPlane, SampleCount = RayShadowImage.SourceSampleCount,
        }, vulkan: false);
        var actual = new Vector3[frame.Width * frame.Height];
        for (int pixel = 0; pixel < actual.Length; pixel++)
        {
            int channel = pixel * 4;
            float direct = (float)unshadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
            float shaded = (float)shadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
            actual[pixel] = new(direct > 0 ? shaded / direct : float.NaN,
                (float)shadowed.Pixels.Span[channel + 3], direct > 0 ? 1 : 0);
        }
        RayShadowSoftComparison comparison = RayShadowSoftComparison.Write(evidence.Directory, reference,
            frame.Width, frame.Height, actual);
        await File.WriteAllTextAsync(Path.Combine(evidence.Directory, "comparison.json"), JsonSerializer.Serialize(new
        {
            SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
            Frame = frame, Captures = captures, Comparison = comparison,
            Reference = "Independent original glTF triangles and UV alpha, submitted transforms/camera, geometric-normal ULP origin, authored point/spot range and cone; uniform solid-angle spherical-emitter integration ending at its surface",
            ReferenceSamples = new { Samples = RayShadowImage.SourceSampleCount, Sampling = "Shifted low-discrepancy integration over source solid angle" },
            Observation = "Unfiltered user color (shadowed-ambient)/(unshadowed-ambient). Every eligible source receiver remains; nonpositive direct radiance contributes full error.",
            Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMismatchFraction = .02f,
                MaximumMeanVisibilityError = .02f, MaximumFalseShadowFraction = .02f,
                MaximumMissedShadowFraction = .02f, MinimumRenderedReceiverCoverage = .98f, MaximumSelfHits = 0 },
        }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
        output.WriteLine($"Independent default soft local-light reference: {evidence.Directory}; {comparison}");
        Assert.True(comparison.ShadowedPixels >= 32 && comparison.LitPixels >= 512, $"Reference coverage: {comparison}");
        Assert.True(comparison.PenumbraPixels >= 32, $"Area emitter penumbra: {comparison}");
        Assert.True(comparison.RenderedReceiverCoverage >= .98f, $"Receiver coverage: {comparison}");
        Assert.Equal(0, comparison.SelfHitPixels);
        Assert.True(comparison.MeanVisibilityError <= .02f, $"Mean soft visibility error: {comparison}");
        Assert.True(comparison.MismatchFraction <= .02f, $"Soft visibility mismatch: {comparison}");
        Assert.True(comparison.FalseShadowFraction <= .02f, $"False soft shadow: {comparison}");
        Assert.True(comparison.MissedShadowFraction <= .02f, $"Missed soft shadow: {comparison}");
        evidence.Complete();
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select the engine checkout for the source-radius shadow regression.");
    }
}
