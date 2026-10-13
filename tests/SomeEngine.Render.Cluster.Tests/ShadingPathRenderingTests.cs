using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Lighting;
using Xunit.Sdk;

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
            AssertForwardImage(expected, current);
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

    private static void AssertForwardImage(RasterImage expected, RasterImage actual)
    {
        ReadOnlySpan<Half> expectedColor = MemoryMarshal.Cast<byte, Half>(expected.Color);
        ReadOnlySpan<Half> actualColor = MemoryMarshal.Cast<byte, Half>(actual.Color);
        try
        {
            // Separately compiled shading entries can round to adjacent output values.
            for (int channel = 0; channel < expectedColor.Length; channel++)
                Assert.InRange(actualColor[channel], Half.BitDecrement(expectedColor[channel]),
                    Half.BitIncrement(expectedColor[channel]));
        }
        catch (XunitException)
        {
            ImageArtifacts.SaveComparison(new(64, 64, Format.R16G16B16A16Float, expected.Color),
                new(64, 64, Format.R16G16B16A16Float, actual.Color), "switch-to-forward-color");
            throw;
        }
        ImageArtifacts.AssertEqual(new(64, 64, Format.D32Float, expected.Depth),
            new(64, 64, Format.D32Float, actual.Depth), "switch-to-forward-depth");
    }

    private static void AssertReceiver(SurfaceReceiver expected, SurfaceReceiver actual)
    {
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Depth, actual.Depth);
        // Separately compiled entries can round interpolation differently before the final image.
        Assert.InRange(actual.DepthOffset - expected.DepthOffset, -0.000001f, 0.000001f);
        AssertVector(expected.ShadingNormal, actual.ShadingNormal);
        AssertVector(expected.GeometricNormal, actual.GeometricNormal);
        // Forward reconstructs position from the shared depth; deferred interpolates it.
        Assert.InRange(Vector3.Distance(expected.Position, actual.Position), 0f, 0.00002f);

        static void AssertVector(Vector3 expected, Vector3 actual)
        {
            float error = Vector3.Distance(expected, actual);
            Assert.True(error <= 0.000001f,
                $"Receiver vector differs by {error:R}: expected {expected.ToString("R")}, actual {actual.ToString("R")}.");
        }
    }
}
