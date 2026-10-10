using System.Numerics;
using System.Text.Json;
using SomeEngine.Core.Math;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Runtime;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
[Collection("Local light reference")]
public sealed class FroxelShadowRenderingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(SceneLightKind.Directional)]
    [InlineData(SceneLightKind.Point)]
    public async Task FroxelReceiverRequestsPreserveSourceShadowsAfterACanceledCasterMove(SceneLightKind lightKind)
    {
        string engine = FrameStageScene.FindRoot();
        using var evidence = new ShadowEvidence("froxel-receiver-" + lightKind);
        var inputs = new ShadowReferenceInputs(256, 256,
            new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(new(0, 0, -8), new(4, 4, .1f), true), new(new(0, 0, -6), new(.6f), false)],
            world =>
            {
                if (lightKind == SceneLightKind.Directional)
                    world.CreateEntity(new DirectionalLight(ShadowReferenceScene.LightDirection, Vector3.One, 3,
                        ShadowSettings: new(SourceAngleDegrees: 0)));
                else world.CreateEntity(new PointLight(new(-4, 2, -3), 20, Vector3.One, 30));
            });
        await ShadowReferenceScene.BuildAsync(engine, evidence.Content, inputs);
        await ShadowVisibilityContent.BuildStandardAsync(engine,
            Path.Combine(engine, "templates", "DefaultProject"), evidence.Content);
        var images = new List<SceneLinearImage>();
        var settings = new VirtualShadowMapSettings
        {
            MarkPagesUsingFroxels = true,
            DynamicResolutionEnabled = false,
            MaxFilterRadiusTexels = 0,
        };
        var script = new SceneScript(evidence.Content, 34,
        [
            new(31, edit => edit.CaptureLinearImage(images.Add)),
            new(32, edit =>
            {
                edit.SetEntityTransform(edit.MeshEntities[1],
                    new TransformQvvs(new(-1, 0, -6), Quaternion.Identity, .6f));
                edit.CancelFrame();
            }),
            new(33, edit => edit.CaptureLinearImage(images.Add)),
        ]) { WaitForFrames = false, ShadowSettings = settings };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
             "--frames", "34", "--no-debug-ui", "--fixed-step", "0.016666667",
             "--no-taa", "--static-scene", "--no-mesh-shaders", "--hardware-raster"], script);
        Assert.True(result.Succeeded, $"{result.Failure}; {result.Error}");
        Assert.Equal(2, images.Count);
        Assert.Equal(31, images[0].Frame.Frame);
        Assert.Equal(33, images[1].Frame.Frame);
        Assert.Equal(32u, images[1].Frame.SubmittedFrame);
        Assert.NotEqual(images[0].Frame.Meshes, images[1].Frame.Meshes);
        Assert.False(images[0].Pixels.Span.SequenceEqual(images[1].Pixels.Span),
            "Moving the caster must change the observed shadow image.");

        foreach (SceneLinearImage image in images)
        {
            SceneFrame frame = image.Frame;
            string directory = Path.Combine(evidence.Directory, $"frame-{frame.Frame}");
            Directory.CreateDirectory(directory);
            string raw = Path.Combine(directory, "vsm-shadow.rgba16");
            image.Save(raw);
            var rays = new RayShadowScene(
                Path.Combine(engine, "templates", "DefaultProject", "assets", "Models", "ico_sphere.glb"),
                frame.Meshes.Select(mesh => mesh.Transform).ToArray());
            Assert.True(Matrix4x4.Invert(frame.View * frame.Projection, out Matrix4x4 clipToWorld));
            SceneLight light = Assert.Single(frame.Lights);
            uint[] reference = await RayShadowImage.TraceAsync(engine, rays, new()
            {
                ClipToWorld = clipToWorld, CameraPosition = frame.CameraPosition,
                Width = (uint)frame.Width, Height = (uint)frame.Height,
                LightKind = (uint)light.Kind, LightPosition = light.Position,
                LightDirection = light.Direction, LightRange = light.Range, OuterConeCos = light.OuterConeCos,
                NearPlane = frame.NearPlane, FarPlane = frame.FarPlane,
            }, vulkan: false);
            RayShadowComparison comparison = RayShadowComparison.Write(directory, reference,
                frame.Width, frame.Height, ShadowImageFiles.ReadRawLinearPixels(raw, frame.Width, frame.Height));
            await File.WriteAllTextAsync(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(new
            {
                SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                Frame = frame, Comparison = comparison,
                Reference = "Original source triangles and UV alpha, submitted camera and transforms, geometric-normal ULP origin",
                Observation = "Actual VSM visibility with the unchanged Standard vertex, visibility and surface evaluation",
                Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMeanVisibilityError = .02f,
                    MaximumMismatchFraction = .02f, MinimumRenderedReceiverCoverage = .98f, MaximumSelfHits = 0 },
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            output.WriteLine($"Froxel source comparison: {directory}; {comparison}");
            Assert.True(comparison.AllReceivers.ShadowedPixels >= 32 && comparison.AllReceivers.LitPixels >= 512,
                $"Reference receiver coverage: {comparison}");
            Assert.True(comparison.RenderedReceiverCoverage >= .98f, $"Rendered receiver coverage: {comparison}");
            Assert.Equal(0, comparison.SelfHitPixels);
            Assert.True(comparison.AllReceivers.MeanVisibilityError <= .02f,
                $"Mean source visibility error: {comparison}");
            Assert.True(comparison.AllReceivers.MismatchFraction <= .02f,
                $"Source visibility mismatch: {comparison}");
        }
        evidence.Complete();
    }
}
