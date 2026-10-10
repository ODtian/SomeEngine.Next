using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Runtime;
using Xunit.Sdk;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
[Collection("Local light reference")]
public sealed class HiddenWindowRenderingTests
{
    [Fact]
    public async Task HiddenWindowKeepsRenderingSceneEditsAfterACanceledFrame()
    {
        string engine = FrameStageScene.FindRoot();
        using var evidence = new ShadowEvidence("hidden-window-scene-edits");
        var position = new Vector3(0, 0, -8);
        var inputs = new ShadowReferenceInputs(128, 128,
            new SceneCamera { Position = Vector3.Zero, Target = position, Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(position, new(2), false)],
            world => world.CreateEntity(new PointLight(new(0, 2, -3), 20, Vector3.One, 30)));
        await ShadowReferenceScene.BuildAsync(engine, evidence.Content, inputs);
        var images = new List<SceneLinearImage>();
        var script = new SceneScript(evidence.Content, 36,
        [
            new(31, edit => edit.CaptureLinearImage(images.Add)),
            new(32, edit =>
            {
                edit.SetEntityTransform(edit.MeshEntities[0],
                    new TransformQvvs(new(2, 0, -8), Quaternion.Identity, 2));
                edit.CancelFrame();
            }),
            new(33, edit => edit.CaptureLinearImage(images.Add)),
            new(34, edit => edit.SetEntityTransform(edit.MeshEntities[0],
                new TransformQvvs(position, Quaternion.Identity, 2))),
            new(35, edit => edit.CaptureLinearImage(images.Add)),
        ]) { WaitForFrames = false };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
             "--no-debug-ui", "--fixed-step", "0.016666667", "--no-taa", "--no-vsm", "--static-scene"], script);

        Assert.True(result.Succeeded, $"{result.Failure}; {result.Error}");
        Assert.Equal(35, result.CompletedFrames);
        Assert.Equal([31, 33, 35], images.Select(image => image.Frame.Frame));
        Assert.Equal([31u, 32u, 34u], images.Select(image => image.Frame.SubmittedFrame));
        Assert.All(images, image => Assert.All(image.Pixels.ToArray(), value => Assert.True(Half.IsFinite(value))));
        try
        {
            Assert.False(images[0].Pixels.Span.SequenceEqual(images[1].Pixels.Span),
                "A hidden window must render the scene edit on the first frame after cancellation.");
        }
        catch (XunitException)
        {
            ImageArtifacts.SaveComparison(
                new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(images[0].Pixels.Span).ToArray()),
                new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(images[1].Pixels.Span).ToArray()),
                nameof(HiddenWindowKeepsRenderingSceneEditsAfterACanceledFrame));
            throw;
        }
        ImageArtifacts.AssertEqual(
            new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(images[0].Pixels.Span).ToArray()),
            new(128, 128, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(images[2].Pixels.Span).ToArray()));
        evidence.Complete();
    }
}
