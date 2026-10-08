using System.Numerics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DepthBucketRenderingTests
{
    [Fact]
    public async Task DepthBucketsPreserveOverlappingUserMaterialColorAndDepth()
    {
        RasterImage expected;
        var mesh = await RasterTestGeometry.CreatePlaneAsync();
        await using (var scene = await FrameStageScene.CreateAsync("D3D12", true,
            new ClusterPipelineOptions { MaxCandidates = 1024, EnableFrameMetricsReadback = true, EnableDepthBuckets = false }, mesh))
        {
            scene.UseMaterial(0, scene.CreateRasterMaterial(RasterMaterialKind.Ordinary, new(.8f, .1f, .2f, 1)));
            scene.UseMaterial(1, scene.CreateRasterMaterial(RasterMaterialKind.Ordinary, new(.1f, .2f, .8f, 1)));
            scene.SetMeshTransform(0, new(new Vector3(0, 0, -6), Quaternion.Identity));
            scene.SetMeshTransform(1, new(new Vector3(.3f, 0, -4), Quaternion.Identity));
            expected = await scene.ReadRasterImageAsync();
        }
        await using var ordered = await FrameStageScene.CreateAsync("D3D12", true,
            new ClusterPipelineOptions { MaxCandidates = 1024, EnableFrameMetricsReadback = true, EnableDepthBuckets = true }, mesh);
        ordered.UseMaterial(0, ordered.CreateRasterMaterial(RasterMaterialKind.Ordinary, new(.8f, .1f, .2f, 1)));
        ordered.UseMaterial(1, ordered.CreateRasterMaterial(RasterMaterialKind.Ordinary, new(.1f, .2f, .8f, 1)));
        ordered.SetMeshTransform(0, new(new Vector3(0, 0, -6), Quaternion.Identity));
        ordered.SetMeshTransform(1, new(new Vector3(.3f, 0, -4), Quaternion.Identity));
        RasterImage actual = await ordered.ReadRasterImageAsync();
        ImageArtifacts.AssertEqual(new(64, 64, SomeEngine.Graphics.Format.R16G16B16A16Float, expected.Color),
            new(64, 64, SomeEngine.Graphics.Format.R16G16B16A16Float, actual.Color));
        Assert.Equal(expected.Depth, actual.Depth);
    }
}
