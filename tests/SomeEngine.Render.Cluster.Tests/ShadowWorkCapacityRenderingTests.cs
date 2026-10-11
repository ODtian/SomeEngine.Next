using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;
using SomeEngine.Runtime;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowWorkCapacityRenderingTests
{
    [Theory]
    [InlineData("vulkan")]
    [InlineData("d3d12")]
    public async Task GrowingInitialLightStoragePreservesCompletedWorkFeedback(string backend)
    {
        string root = FrameStageScene.FindRoot();
        using var files = new ShadowEvidence("shadow-work-capacity-" + backend);
        var inputs = new ShadowReferenceInputs(64, 64,
            new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(new(0, 0, -8), new(4, 4, .1f), true), new(new(0, 0, -6), new(.6f), false)],
            world =>
            {
                world.CreateEntity(new PointLight(new(-4, 2, -3), 20, Vector3.One, 15));
                world.CreateEntity(new PointLight(new(4, 2, -3), 20, Vector3.One, 15));
            });
        await ShadowReferenceScene.BuildAsync(root, files.Content, inputs);
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 2048, AtlasSize = 1024, PageSize = 128,
            DirectionalClipmapLevels = 1, FirstPersonShadowsEnabled = false,
            DynamicResolutionEnabled = false, MaxFilterRadiusTexels = 0,
            WorkLoadBudget = 1_000_000,
        };

        SceneLinearImage expected = await CaptureAsync(files.Content, backend, settings with { MaxShadowLights = 2 });
        SceneLinearImage actual = await CaptureAsync(files.Content, backend, settings with { MaxShadowLights = 1 });
        Assert.Equal(2, actual.Frame.Lights.Length);
        var quality = Assert.IsType<VirtualShadowFrameQuality>(actual.Frame.Quality?.Shadows);
        Assert.Equal(12, quality.Views.Length);
        Assert.Equal(ClusterWorkOverflow.None, quality.WorkOverflow);
        ImageArtifacts.AssertEqual(
            new(64, 64, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(expected.Pixels.Span).ToArray()),
            new(64, 64, Format.R16G16B16A16Float, MemoryMarshal.AsBytes(actual.Pixels.Span).ToArray()));
        files.Complete();
    }

    private static async Task<SceneLinearImage> CaptureAsync(
        string content, string backend, VirtualShadowMapSettings settings)
    {
        SceneLinearImage? image = null;
        var script = new SceneScript(content, 16,
            [new(15, edit => edit.CaptureLinearImage(value => image = value))]) { ShadowSettings = settings };
        using var messages = new VulkanErrors();
        Trace.Listeners.Add(messages);
        try
        {
            ApplicationResult result = await Application.RunAsync(
                ["--hardware-adapter", "--graphics-backend", backend, "--hardware-raster",
                 backend == "vulkan" ? "--rhi-validation" : "--no-rhi-validation",
                 "--hidden-window", "--no-vsync", "--no-debug-ui", "--no-taa", "--fixed-step", "0.016666667"], script);
            Assert.True(result.Succeeded, result.Error?.ToString());
            Assert.True(messages.Errors.IsEmpty, string.Join(Environment.NewLine, messages.Errors));
            return Assert.IsType<SceneLinearImage>(image);
        }
        finally { Trace.Listeners.Remove(messages); }
    }

    private sealed class VulkanErrors : TraceListener
    {
        internal ConcurrentQueue<string> Errors { get; } = new();
        public override void Write(string? message)
        {
            if (message?.StartsWith("Vulkan [ErrorBitExt/", StringComparison.Ordinal) == true)
                Errors.Enqueue(message);
        }
        public override void WriteLine(string? message) => Write(message);
    }
}
