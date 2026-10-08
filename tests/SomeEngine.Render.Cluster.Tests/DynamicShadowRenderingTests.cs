using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DynamicShadowRenderingTests
{
    [Fact]
    public async Task AnAutomaticShadowCasterMovesAgainAfterReturningToStaticCache()
    {
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions
        { ForceHardwareRaster = true, MaxCandidates = 65536, EnableFrameMetricsReadback = true };
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            WorldUnitsPerCentimeter = 1,
            DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalResolutionLodBias = 0, DirectionalMovingResolutionLodBias = 0,
            DirectionalReceiverMask = true, DirectionalRayCount = 0, LocalRayCount = 0,
            DirectionalCoarsePageMode = VirtualShadowCoarsePageMode.Disabled,
            DynamicResolutionEnabled = false,
        };
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings);
        scene.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(3, 3, .1f) });
        TransformQvvs outside = new(new Vector3(20, 4, -4), Quaternion.Identity) { Stretch = new(1.6f, 1.6f, .1f) };
        scene.SetMeshTransform(1, outside);
        scene.AddGeometryMesh(plane, new(new Vector3(0, 2, -6), Quaternion.Identity) { Stretch = new(.6f, .6f, .1f) });
        // A zero-angle emitter keeps the first-frame cache comparison deterministic.
        scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)), new(SourceAngleDegrees: 0f));

        RasterImage exposed = await scene.ReadNextRasterImageAsync();
        for (int frame = 1; frame < 4; frame++) exposed = await scene.ReadNextRasterImageAsync();

        scene.SetMeshTransform(1, new(new Vector3(0, 4, -4), Quaternion.Identity) { Stretch = new(1.6f, 1.6f, .1f) });
        RasterImage covered = await scene.ReadNextRasterImageAsync();
        for (int frame = 1; frame < 4; frame++) covered = await scene.ReadNextRasterImageAsync();
        Assert.NotEqual(exposed.Color, covered.Color);

        scene.SetMeshTransform(1, outside);
        RasterImage restored = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(exposed, restored);
        for (int frame = 0; frame < 102; frame++) restored = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(exposed, restored);
        scene.SetMeshTransform(1, new(new Vector3(0, 4, -4), Quaternion.Identity) { Stretch = new(1.6f, 1.6f, .1f) });
        RasterImage coveredAgain = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(covered, coveredAgain);
        scene.SetMeshTransform(1, outside);
        RasterImage.AssertEqual(exposed, await scene.ReadNextRasterImageAsync());
    }
}
