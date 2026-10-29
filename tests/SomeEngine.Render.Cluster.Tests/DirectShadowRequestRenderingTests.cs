using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Components;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DirectShadowRequestRenderingTests
{
    [Fact]
    public async Task UnlitMaterialKeepsItsOwnRequestsAndRestoringStandardRestoresSharedDepthRequests()
    {
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, Options(),
            nativeD3D12GpuValidation: false);
        scene.UseMaterial("Standard");
        RasterImage expected = await scene.ReadRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);

        scene.UseMaterial("Unlit");
        RasterImage unlit = await scene.ReadNextRasterImageAsync();
        Assert.False(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        Assert.Contains(MemoryMarshal.Cast<byte, Half>(unlit.Color).ToArray(), value => (float)value > .01f);

        scene.UseMaterial("Standard");
        RasterImage actual = await scene.ReadNextRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        RasterImage.AssertEqual(expected, actual, "shared-primary-depth-requests-after-unlit");
    }

    [Fact]
    public async Task SharedPrimaryDepthRequestsMatchMaterialRequestsAcrossFrameBoundaries()
    {
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, Options(),
            nativeD3D12GpuValidation: false);
        scene.UseMaterial("Standard");
        RasterImage expected = await scene.ReadRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        ClusterPipelineOutputs outputs = scene.Outputs;

        scene.Shadows.SetPrimaryDepthReceiverRequests(false);
        RasterImage materialRequests = await scene.ReadNextRasterImageAsync();
        Assert.False(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        RasterImage.AssertEqual(expected, materialRequests, "shared-primary-depth-and-material-requests");

        scene.Shadows.SetPrimaryDepthReceiverRequests(true);
        RasterImage actual = await scene.ReadNextRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        RasterImage.AssertEqual(expected, actual, "shared-primary-depth-requests-after-reenabling");
        AssertOutputs(outputs, scene.Outputs);
    }

    [Fact]
    public async Task FirstPersonMembershipRoundTripRestoresForwardShadowImage()
    {
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, Options(),
            nativeD3D12GpuValidation: false);
        scene.UseMaterial("Standard");
        RasterImage expected = await scene.ReadRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        Assert.Contains(MemoryMarshal.Cast<byte, float>(expected.Depth).ToArray(), value => value < 1f);
        Assert.Contains(MemoryMarshal.Cast<byte, Half>(expected.Color).ToArray(), value => (float)value > .01f);
        ClusterPipelineOutputs outputs = scene.Outputs;

        scene.SetView(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64,
            PlayerId: 1, FirstPerson: new FirstPersonCamera(1.2f, .8f)));
        scene.SetMeshFirstPerson(1, new(FirstPersonMeshKind.FirstPerson, 1));
        RasterImage firstPerson = await scene.ReadNextRasterImageAsync();
        Assert.False(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
        Assert.False(expected.Depth.AsSpan().SequenceEqual(firstPerson.Depth));
        Assert.Contains(MemoryMarshal.Cast<byte, Half>(firstPerson.Color).ToArray(), value => (float)value > .01f);

        scene.SetMeshFirstPerson(1, default);
        scene.SetView(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64));
        RasterImage actual = await scene.ReadNextRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
        RasterImage.AssertEqual(expected, actual, "direct-shadow-requests-after-first-person");
        AssertOutputs(outputs, scene.Outputs);
    }

    [Fact]
    public async Task FailedDebugSelectionPreservesForwardShadowImage()
    {
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, Options(),
            nativeD3D12GpuValidation: false);
        scene.UseMaterial("Standard");
        RasterImage expected = await scene.ReadRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        ClusterPipelineOutputs outputs = scene.Outputs;

        scene.SetDebugMode(uint.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(() => scene.ReadNextRasterImageAsync());
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);

        scene.SetDebugMode(0);
        RasterImage actual = await scene.ReadNextRasterImageAsync();
        Assert.True(scene.Shadows.UsesPrimaryDepthReceiverRequests);
        Assert.Equal(ClusterShadingPath.Forward, scene.InstalledShadingPath);
        AssertOutputs(outputs, scene.Outputs);
        RasterImage.AssertEqual(expected, actual, "direct-shadow-requests-after-failed-debug");
    }

    private static ClusterPipelineOptions Options() => new()
    {
        MaxCandidates = 4096,
        ShadingPath = ClusterShadingPath.Forward,
        EnableFrameMetricsReadback = true,
    };

    private static void AssertOutputs(ClusterPipelineOutputs expected, ClusterPipelineOutputs actual)
    {
        Assert.Equal(expected.Visibility, actual.Visibility);
        Assert.Equal(expected.Depth, actual.Depth);
        Assert.Equal(expected.MaterialColor, actual.MaterialColor);
        Assert.Equal(expected.MotionVectors, actual.MotionVectors);
        Assert.Same(expected.Geometry, actual.Geometry);
    }
}
