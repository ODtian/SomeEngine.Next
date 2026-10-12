using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Lighting;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadingPathRenderingTests
{
    [Fact]
    public async Task SwitchingShadingPathsPreservesImagesAndGeometryOutputs()
    {
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions
            {
                MaxCandidates = 4096,
                EnableFrameMetricsReadback = true,
                ExportSurfaceReceivers = true,
            });
        scene.UseMaterial("Standard");
        RasterImage expected = await scene.ReadRasterImageAsync();
        var receiver = scene.ObserveSurfaceReceiver(32, 32);
        expected = await scene.ReadNextRasterImageAsync();
        SurfaceReceiver expectedReceiver = receiver.Value;
        Assert.True((expectedReceiver.Flags & SurfaceReceiverFlags.Present) != 0);
        Assert.True(expectedReceiver.ShadingNormal.LengthSquared() > 0f);
        ClusterPipelineOutputs outputs = scene.Outputs;
        Assert.Equal(ClusterShadingPath.Deferred, scene.InstalledShadingPath);
        Assert.Contains(MemoryMarshal.Cast<byte, float>(expected.Depth).ToArray(), depth => depth < 1f);

        scene.SetShadingPath(ClusterShadingPath.Forward);
        for (int frame = 0; frame < 2; frame++)
        {
            RasterImage current = await scene.ReadNextRasterImageAsync();
            Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
            Assert.Equal(outputs.Visibility, scene.Outputs.Visibility);
            Assert.Equal(outputs.Depth, scene.Outputs.Depth);
            Assert.Equal(outputs.MaterialColor, scene.Outputs.MaterialColor);
            Assert.Equal(outputs.MotionVectors, scene.Outputs.MotionVectors);
            Assert.Same(outputs.Geometry, scene.Outputs.Geometry);
            Assert.Equal(outputs.SurfaceReceiverConsumers, scene.Outputs.SurfaceReceiverConsumers);
            RasterImage.AssertEqual(expected, current, "switch-to-forward");
            AssertReceiver(expectedReceiver, receiver.Value);
        }

        scene.SetShadingPath(ClusterShadingPath.Deferred);
        for (int frame = 0; frame < 2; frame++)
        {
            RasterImage current = await scene.ReadNextRasterImageAsync();
            Assert.Equal(ClusterShadingPath.Deferred, scene.InstalledShadingPath);
            Assert.Equal(outputs.Visibility, scene.Outputs.Visibility);
            Assert.Equal(outputs.Depth, scene.Outputs.Depth);
            Assert.Equal(outputs.MaterialColor, scene.Outputs.MaterialColor);
            Assert.Equal(outputs.MotionVectors, scene.Outputs.MotionVectors);
            Assert.Same(outputs.Geometry, scene.Outputs.Geometry);
            Assert.Equal(outputs.SurfaceReceiverConsumers, scene.Outputs.SurfaceReceiverConsumers);
            RasterImage.AssertEqual(expected, current, "switch-back-to-deferred");
            AssertReceiver(expectedReceiver, receiver.Value);
        }
    }
    private static void AssertReceiver(SurfaceReceiver expected, SurfaceReceiver actual)
    {
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Depth, actual.Depth);
        Assert.Equal(expected.DepthOffset, actual.DepthOffset);
        // Separately compiled entries can round interpolation differently before the final image.
        AssertVector(expected.ShadingNormal, actual.ShadingNormal);
        AssertVector(expected.GeometricNormal, actual.GeometricNormal);
        AssertVector(expected.Position, actual.Position);

        static void AssertVector(Vector3 expected, Vector3 actual)
        {
            float error = Vector3.Distance(expected, actual);
            Assert.True(error <= 0.000001f,
                $"Receiver vector differs by {error:R}: expected {expected.ToString("R")}, actual {actual.ToString("R")}.");
        }
    }
}
