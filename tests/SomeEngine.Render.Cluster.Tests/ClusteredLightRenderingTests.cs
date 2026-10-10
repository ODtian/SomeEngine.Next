using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Runtime;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
[Collection("Local light reference")]
public sealed class ClusteredLightRenderingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EightyOverlappingLightsPreserveTheCompleteRenderedContribution(bool shadows, bool froxels)
    {
        string engine = FrameStageScene.FindRoot();
        using var evidence = new ShadowEvidence($"eighty-lights-{shadows}-{froxels}");
        int lightCount = 1;
        var inputs = new ShadowReferenceInputs(256, 256,
            new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(new(0, 0, -8), new(4, 4, .1f), true), new(new(0, 0, -6), new(.6f), false)],
            world =>
            {
                for (int light = 0; light < lightCount; light++)
                    world.CreateEntity(new PointLight(Vector3.Zero, 10, Vector3.One, 30f / lightCount));
            });
        await ShadowReferenceScene.BuildAsync(engine, evidence.Content, inputs);
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 4096, MaxShadowLights = 80,
            LocalResolutionLodBias = 4,
            DynamicResolutionEnabled = false, MaxFilterRadiusTexels = 0,
            MarkPagesUsingFroxels = froxels,
        };
        List<string> arguments = ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window",
            "--no-vsync", "--frames", "32", "--no-debug-ui", "--fixed-step", "0.016666667",
            "--no-taa", "--static-scene", "--no-mesh-shaders", "--hardware-raster"];
        if (!shadows) arguments.Add("--no-vsm");
        var images = new List<SceneLinearImage>();
        foreach (int count in new[] { 1, 80 })
        {
            lightCount = count;
            await ShadowReferenceScene.WriteWorldAsync(evidence.Content, inputs);
            var script = new SceneScript(evidence.Content, 32,
                [new(31, edit => edit.CaptureLinearImage(images.Add))])
                { WaitForFrames = false, ShadowSettings = settings };
            ApplicationResult result = await Application.RunAsync(arguments.ToArray(), script);
            Assert.True(result.Succeeded, $"{result.Failure}; {result.Error}");
            SceneLinearImage image = images[^1];
            Assert.Equal(count, image.Frame.Lights.Length);
            image.Save(Path.Combine(evidence.Directory, $"lights-{count}.rgba16"));
            if (shadows)
            {
                var quality = Assert.IsType<SomeEngine.Render.Features.Shadows.VirtualShadowFrameQuality>(image.Frame.Quality?.Shadows);
                Assert.Equal(0u, quality.AllocationFailures);
                Assert.False(quality.LightAssignmentOverflow);
            }
        }
        Assert.Equal(2, images.Count);
        double difference = 0, expectedEnergy = 0;
        for (int channel = 0; channel < images[0].Pixels.Length; channel++)
        {
            if (channel % 4 == 3) continue;
            float expected = (float)images[0].Pixels.Span[channel];
            float actual = (float)images[1].Pixels.Span[channel];
            Assert.True(float.IsFinite(expected) && float.IsFinite(actual));
            difference += Math.Abs(actual - expected);
            expectedEnergy += Math.Abs(expected);
        }
        double relativeError = difference / Math.Max(expectedEnergy, 1e-12);
        output.WriteLine($"One 30-intensity light versus eighty 0.375-intensity lights: relative RGB error {relativeError:P6}; energy {expectedEnergy}.");
        try
        {
            Assert.True(expectedEnergy > 100, "The comparison must contain visible lighting.");
            // The two sums differ only by floating-point addition and RGBA16F rounding.
            Assert.InRange(relativeError, 0, .001);
        }
        catch (XunitException)
        {
            ImageArtifacts.SaveComparison(
                new(256, 256, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(images[0].Pixels.Span).ToArray()),
                new(256, 256, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(images[1].Pixels.Span).ToArray()),
                nameof(EightyOverlappingLightsPreserveTheCompleteRenderedContribution));
            throw;
        }
        evidence.Complete();
    }

    [Fact]
    public async Task SharedBudgetExhaustionIsReportedInTheRenderedFrame()
    {
        string engine = FrameStageScene.FindRoot();
        using var evidence = new ShadowEvidence("local-light-budget-exhaustion");
        var inputs = new ShadowReferenceInputs(256, 256,
            new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(new(0, 0, -8), new(4, 4, .1f), true), new(new(0, 0, -6), new(.6f), false)],
            world =>
            {
                // All depth slices now overlap: eighty entries per cell exceed the shared average of sixty-four.
                for (int light = 0; light < 80; light++)
                    world.CreateEntity(new PointLight(Vector3.Zero, 10_000, Vector3.One, .375f));
            });
        await ShadowReferenceScene.BuildAsync(engine, evidence.Content, inputs);
        var images = new List<SceneLinearImage>();
        var script = new SceneScript(evidence.Content, 32,
            [new(31, edit => edit.CaptureLinearImage(images.Add))])
        {
            WaitForFrames = false,
            ShadowSettings = new VirtualShadowMapSettings
            {
                VirtualResolution = 4096, AtlasSize = 4096, MaxShadowLights = 80,
                LocalResolutionLodBias = 4, DynamicResolutionEnabled = false, MaxFilterRadiusTexels = 0,
            },
        };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
             "--frames", "32", "--no-debug-ui", "--fixed-step", "0.016666667", "--no-taa",
             "--static-scene", "--no-mesh-shaders", "--hardware-raster"], script);
        Assert.True(result.Succeeded, $"{result.Failure}; {result.Error}");
        SceneLinearImage image = Assert.Single(images);
        image.Save(Path.Combine(evidence.Directory, "bounded-overflow.rgba16"));
        Assert.Equal(80, image.Frame.Lights.Length);
        var quality = Assert.IsType<SomeEngine.Render.Features.Shadows.VirtualShadowFrameQuality>(image.Frame.Quality?.Shadows);
        Assert.True(quality.LightAssignmentOverflow);
        Assert.All(image.Pixels.ToArray(), value => Assert.True(Half.IsFinite(value)));
        output.WriteLine("The completed frame reports shared light-list budget exhaustion without out-of-range accesses or a fatal exception.");
        evidence.Complete();
    }

}
