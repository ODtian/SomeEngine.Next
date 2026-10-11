using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowCacheMembershipRenderingTests
{
    [Fact]
    public async Task MovingACasterAfterUnrelatedMembershipChangesAndADiscardedFramePreservesItsShadow()
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
            DirectionalReceiverMask = true, MaxFilterRadiusTexels = 0,
            DirectionalCoarsePageMode = VirtualShadowCoarsePageMode.Disabled,
            DynamicResolutionEnabled = false,
        };
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings);
        Vector3 translation = new(0, 1000, 1000);
        scene.SetView(new(Matrix4x4.CreateTranslation(-translation),
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64));
        scene.SetMeshTransform(0, new(new Vector3(0, 0, -8) + translation, Quaternion.Identity) { Stretch = new(3, 3, .1f) });
        TransformQvvs outside = new(new Vector3(20, 4, -4) + translation, Quaternion.Identity) { Stretch = new(1.6f, 1.6f, .1f) };
        TransformQvvs coveredTransform = outside with { Position = new Vector3(0, 4, -4) + translation };
        scene.SetMeshTransform(1, outside);
        var unrelated = scene.AddGeometryMesh(plane, outside);
        var caster = scene.AddGeometryMesh(plane, outside);
        scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)), new(SourceAngleDegrees: 0f));
        RasterImage exposed = await scene.ReadNextRasterImageAsync();
        for (int frame = 1; frame < 4; frame++) exposed = await scene.ReadNextRasterImageAsync();

        scene.SetMeshTransform(caster, coveredTransform);
        RasterImage covered = await scene.ReadNextRasterImageAsync();
        for (int frame = 1; frame < 4; frame++) covered = await scene.ReadNextRasterImageAsync();
        Assert.NotEqual(exposed.Color, covered.Color);
        var added = scene.AddGeometryMesh(plane, outside);
        RasterImage.AssertEqual(covered, await scene.ReadNextRasterImageAsync());

        // Removing an earlier entity can move the surviving caster to another batch row.
        scene.RemoveMaterialMesh(unrelated);
        await scene.DiscardNextFrameAsync();
        RasterImage.AssertEqual(covered, await scene.ReadNextRasterImageAsync());
        scene.SetMeshTransform(caster, outside);
        RasterImage.AssertEqual(exposed, await scene.ReadNextRasterImageAsync());
        scene.RemoveMaterialMesh(added);
        await scene.DiscardNextFrameAsync();
        RasterImage.AssertEqual(exposed, await scene.ReadNextRasterImageAsync());
        scene.SetMeshTransform(caster, coveredTransform);
        RasterImage.AssertEqual(covered, await scene.ReadNextRasterImageAsync());
    }
}
