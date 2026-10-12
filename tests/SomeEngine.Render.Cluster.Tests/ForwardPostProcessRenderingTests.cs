using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ForwardPostProcessRenderingTests
{
    [Fact]
    public async Task ForwardShadingPreservesMotionDepthAndTemporalColor()
    {
        using var messages = new VulkanErrors();
        Trace.Listeners.Add(messages);
        try
        {
            PostProcessedImage expected;
            await using (var deferred = await FrameStageScene.CreateAsync("Vulkan", false,
                new ClusterPipelineOptions { MaxCandidates = 4096, ShadingPath = ClusterShadingPath.Deferred, ExportSurfaceReceivers = false },
                nativeVulkanValidation: true))
            {
                deferred.UseMaterial("Unlit");
                deferred.UseMaterial(1, deferred.CreateShadeMaterial(new Vector4(1, 0, 0, 1)));
                deferred.UseTemporalHistory();
                await deferred.ReadPostProcessedColorAsync();
                deferred.SetMeshTransform(1, new TransformQvvs(new Vector3(.6f, 0, -6), Quaternion.Identity, .6f));
                expected = await deferred.ReadNextPostProcessedImageAsync();
                Assert.Equal(ClusterShadingPath.Deferred, deferred.InstalledShadingPath);
            }

            await using var forward = await FrameStageScene.CreateAsync("Vulkan", false,
                new ClusterPipelineOptions { MaxCandidates = 4096, ShadingPath = ClusterShadingPath.Forward, ExportSurfaceReceivers = false },
                nativeVulkanValidation: true);
            forward.UseMaterial("Unlit");
            forward.UseMaterial(1, forward.CreateShadeMaterial(new Vector4(1, 0, 0, 1)));
            forward.UseTemporalHistory();
            byte[] before = await forward.ReadPostProcessedColorAsync();
            forward.SetMeshTransform(1, new TransformQvvs(new Vector3(.6f, 0, -6), Quaternion.Identity, .6f));
            PostProcessedImage actual = await forward.ReadNextPostProcessedImageAsync();
            Assert.Equal(ClusterShadingPath.Forward, forward.InstalledShadingPath);
            Assert.Null(forward.Outputs.SurfaceReceiverConsumers);

            Assert.NotEqual(before, actual.Color);
            Assert.Contains(MemoryMarshal.Cast<byte, float>(actual.Depth).ToArray(), depth => depth < 1f);
            Assert.Contains(MemoryMarshal.Cast<byte, Half>(actual.Motion).ToArray(),
                motion => float.IsFinite((float)motion) && MathF.Abs((float)motion) > .001f);
            ImageArtifacts.AssertEqual(new(64, 64, Format.D32Float, expected.Depth),
                new(64, 64, Format.D32Float, actual.Depth), "forward-post-depth");
            ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16Float, expected.Motion),
                new(64, 64, Format.R16G16Float, actual.Motion), "forward-post-motion");
            ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, expected.Color),
                new(64, 64, Format.R16G16B16A16Float, actual.Color), "forward-post-temporal-color");
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
