using System.Numerics;
using System.Text.Json;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowGeometryRenderingTests
{
    [Fact]
    public async Task MainCameraGeometryProjectionPreservesOffCameraCasterShadows()
    {
        var (positions, triangles, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, triangles,
            [new MeshMaterialSlot(Guid.Empty)], "Shadow LOD sphere");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        var caster = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(caster, Enumerable.Range(0, caster.PageCount));
        var receiver = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, ForceHardwareRaster = true,
            EnableFrameMetricsReadback = true };
        var shadows = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048,
            MaxShadowLights = 1, DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11, DynamicResolutionEnabled = false };

        RasterImage expected;
        await using (var fine = await FrameStageScene.CreateAsync("D3D12", true, options, receiver, shadows))
        {
            fine.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(2, 2, .1f) });
            fine.SetMeshTransform(1, new(new Vector3(20, 4, -4), Quaternion.Identity));
            var casterEntity = fine.AddGeometryMesh(caster, new(new Vector3(0, 4, -4), Quaternion.Identity));
            fine.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)));
            fine.SetView(new(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100),
                64, 64, GeometryProjection: Matrix4x4.CreateOrthographic(.001f, .001f, .1f, 100)));
            expected = await fine.ReadNextRasterImageAsync();
            for (int frame = 1; frame < 6; frame++)
                expected = await fine.ReadNextRasterImageAsync();
            fine.RemoveMaterialMesh(casterEntity);
            Assert.NotEqual(expected.Color, (await fine.ReadNextRasterImageAsync()).Color);
        }
        await using var coarse = await FrameStageScene.CreateAsync("D3D12", true, options, receiver, shadows);
        coarse.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(2, 2, .1f) });
        coarse.SetMeshTransform(1, new(new Vector3(20, 4, -4), Quaternion.Identity));
        coarse.AddGeometryMesh(caster, new(new Vector3(0, 4, -4), Quaternion.Identity));
        coarse.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)));
        coarse.SetView(new(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100),
            64, 64, GeometryProjection: Matrix4x4.CreateOrthographic(10000, 10000, .1f, 100)));
        RasterImage actual = await coarse.ReadNextRasterImageAsync();
        for (int frame = 1; frame < 6; frame++)
            actual = await coarse.ReadNextRasterImageAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { SourceTriangles = triangles.Length / 3,
            SourceVertices = positions.Length, SourcePages = caster.PageCount,
            Expected = expected.Metrics, Actual = actual.Metrics, Feedback = coarse.FeedbackStatistics }));
        Assert.True(actual.Metrics.Work.Complete);
        RasterImage.AssertEqual(expected, actual);
        coarse.SetView(new(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100),
            64, 64, GeometryProjection: Matrix4x4.CreateOrthographic(.001f, .001f, .1f, 100)));
        RasterImage cached = await coarse.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(expected, cached);
        Assert.Equal(0ul, cached.Metrics.Work.TargetCandidateTests);
    }
}
