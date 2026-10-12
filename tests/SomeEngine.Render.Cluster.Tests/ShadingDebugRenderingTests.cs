using System.Runtime.InteropServices;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadingDebugRenderingTests
{
    [Fact]
    public async Task DebugColorSurvivesPathChangesAndCanReturnToNormalShading()
    {
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 4096, EnableFrameMetricsReadback = true });
        scene.UseMaterial("Standard");
        RasterImage normal = await scene.ReadRasterImageAsync();

        scene.SetDebugMode(8);
        RasterImage debug = await scene.ReadNextRasterImageAsync();
        float[] depth = MemoryMarshal.Cast<byte, float>(debug.Depth).ToArray();
        Half[] color = MemoryMarshal.Cast<byte, Half>(debug.Color).ToArray();
        int covered = 0;
        for (int pixel = 0; pixel < depth.Length; pixel++)
        {
            if (depth[pixel] >= 1f) continue;
            covered++;
            float red = (float)color[pixel * 4];
            float green = (float)color[pixel * 4 + 1];
            float blue = (float)color[pixel * 4 + 2];
            Assert.InRange(red, -.002f, 1.002f);
            Assert.InRange(green, -.002f, 1.002f);
            Assert.InRange(blue, -.002f, 1.002f);
            Assert.InRange(red + green + blue, .998f, 1.002f);
        }
        Assert.True(covered > 0);
        Assert.False(normal.Color.AsSpan().SequenceEqual(debug.Color));

        scene.SetShadingPath(ClusterShadingPath.Forward);
        RasterImage forwardDebug = await scene.ReadNextRasterImageAsync();
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
        RasterImage.AssertEqual(debug, forwardDebug, "forward-debug-barycentrics");

        scene.SetDebugMode(0);
        RasterImage forwardNormal = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(normal, forwardNormal, "forward-debug-disabled");

        scene.SetDebugMode(8);
        RasterImage returnedDebug = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(debug, returnedDebug, "forward-debug-restored");

        scene.SetShadingPath(ClusterShadingPath.Deferred);
        RasterImage deferredDebug = await scene.ReadNextRasterImageAsync();
        Assert.Equal(ClusterShadingPath.Deferred, scene.InstalledShadingPath);
        RasterImage.AssertEqual(debug, deferredDebug, "deferred-debug-restored");

        scene.SetDebugMode(0);
        RasterImage deferredNormal = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(normal, deferredNormal, "deferred-debug-disabled");
    }
}
