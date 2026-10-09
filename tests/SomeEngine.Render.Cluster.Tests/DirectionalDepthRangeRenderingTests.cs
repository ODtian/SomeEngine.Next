using System.Numerics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DirectionalDepthRangeRenderingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADirectionalCasterBeyondTheNearDepthRangeShadowsTheVisibleReceiver(bool hardware)
    {
        var options = new ClusterPipelineOptions { ForceHardwareRaster = hardware,
            SoftwareRasterEdgeThreshold = 64, MaxCandidates = 65536, EnableFrameMetricsReadback = true };
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            DirectionalFirstClipmapLevel = 10, DirectionalClipmapLevels = 1,
            DirectionalResolutionLodBias = 0, DirectionalMovingResolutionLodBias = 0,
            DirectionalMinCameraViewportWidth = 2048, DirectionalDepthRangeScale = .2f,
            DirectionalReceiverMask = false, MaxFilterRadiusTexels = 0,
            DirectionalCoarsePageMode = VirtualShadowCoarsePageMode.Disabled,
        };
        await using var scene = await FrameStageScene.CreateAsync("D3D12",true,options,shadowSettings:settings);
        scene.SetMeshTransform(0,new(new Vector3(0,0,-2),Quaternion.Identity) { Stretch = new(2,2,.1f) });
        scene.SetMeshTransform(1,new(new Vector3(0,0,6),Quaternion.Identity) { Stretch = new(.6f) });
        scene.UseDirectionalLight(-Vector3.UnitZ);
        RasterImage shadowed = await scene.ReadRasterImageAsync();
        scene.SetMeshTransform(1,new(new Vector3(20,0,6),Quaternion.Identity) { Stretch = new(.6f) });
        RasterImage unshadowed = await scene.ReadRasterImageAsync();
        Assert.NotEqual(unshadowed.Color,shadowed.Color);
        Assert.Equal(unshadowed.Depth,shadowed.Depth);
    }
}
