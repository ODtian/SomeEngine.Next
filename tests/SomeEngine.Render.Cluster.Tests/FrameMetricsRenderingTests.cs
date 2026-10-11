using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class FrameMetricsRenderingTests
{
    [Fact]
    public async Task AddingAndRemovingAShadeMaterialPreservesFrameMetricsReadback()
    {
        using var messages = new VulkanErrors();
        Trace.Listeners.Add(messages);
        try
        {
            // This check requires the native layer; missing validation is not a passing result.
            using IGraphicsBackend validation = VulkanGraphicsBackend.Create(
                new VulkanBackendOptions(EnableValidation: true, EnableDebugMessages: true));
            await using var scene = await FrameStageScene.CreateAsync("Vulkan", true,
                new ClusterPipelineOptions
                {
                    MaxCandidates = 65536,
                    ForceHardwareRaster = true,
                    EnableFrameMetricsReadback = true,
                }, nativeVulkanValidation: true);
            RasterImage before = await scene.ReadRasterImageAsync();
            var added = scene.AddMaterialMesh(scene.CreateShadeMaterial(new Vector4(.1f, .2f, .9f, 1)));
            RasterImage expanded = await scene.ReadRasterImageAsync();
            Assert.NotEqual(before.Color, expanded.Color);
            Assert.True(expanded.Metrics.Work.Complete);
            Assert.True(expanded.Metrics.ShadingWorkItems > 0);
            scene.RemoveMaterialMesh(added);
            RasterImage restored = await scene.ReadRasterImageAsync();
            RasterImage.AssertEqual(before, restored);
            Assert.True(restored.Metrics.Work.Complete);
            Assert.True(restored.Metrics.ShadingWorkItems > 0);
        }
        finally
        {
            Trace.Listeners.Remove(messages);
        }
        Assert.Empty(messages.Errors);
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
