using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class LocalCasterDepthChangeTests
{
    [Fact]
    public async Task AnAlphaCasterChangeUpdatesItsShadowAndKeepsTheOtherCubeFaceCached()
    {
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, ForceHardwareRaster = true,
            EnableFrameMetricsReadback = true };
        var settings = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048,
            MaxShadowLights = 1, WorldUnitsPerCentimeter = 1, LocalReceiverMask = false,
            LocalResolutionLodBias = -4, LocalCoarsePageMode = VirtualShadowCoarsePageMode.Dynamic,
            DynamicResolutionEnabled = false, WorkLoadBudget = 1_000_000 };
        await using var changed = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings);
        changed.SetView(new(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 64, 64));
        changed.SetPointLight(new(new(0, 0, -4), 20, Vector3.One, 30));
        changed.SetMeshTransform(0, new(new Vector3(0, 0, -10), Quaternion.Identity) { Stretch = new(8, 4, .1f) });
        changed.SetMeshTransform(1, new(new Vector3(-4, 0, -6), Quaternion.Identity, .6f));
        var alpha = Assert.IsType<Game.Content.UnlitMaterial>(changed.CreateRasterMaterial(RasterMaterialKind.Ordinary, Vector4.One));
        changed.UseMaterial(1, alpha);
        var other = changed.AddGeometryMesh(plane, new(new Vector3(4, 0, -6), Quaternion.Identity, .6f));
        changed.UseEntityMaterial(other, changed.CreateRasterMaterial(RasterMaterialKind.Ordinary, Vector4.One));
        var before = await changed.ReadShadowQualityAsync();
        uint otherWork = 0;
        for (int frame = 1; frame <= 12; frame++)
        {
            before = await changed.ReadShadowQualityAsync();
            var face = before.Quality.Views.Single(view => view.FaceIndex == 0);
            otherWork += face.HardwareVisibleItems + face.SoftwareVisibleItems;
        }
        Assert.True(otherWork > 0);
        var otherFace = before.Quality.Views.Single(view => view.FaceIndex == 0);
        Assert.Contains(otherFace.Mips, mip => mip.WholeDepthPages > 0);
        alpha.AlbedoMap = Assert.IsType<Game.Content.UnlitMaterial>(changed.CreateRasterMaterial(RasterMaterialKind.Ordinary,
            new Vector4(1, 1, 1, 0))).AlbedoMap;
        var after = await changed.ReadShadowQualityAsync();
        Assert.NotEqual(before.Image.Color, after.Image.Color);
        var cachedFace = after.Quality.Views.Single(view => view.FaceIndex == 0);
        Assert.Contains(cachedFace.Mips, mip => mip.WholeDepthPages > 0);
        Assert.Equal(0u, cachedFace.HardwareVisibleItems + cachedFace.SoftwareVisibleItems);

        await using var fresh = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings);
        fresh.SetView(new(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 64, 64));
        fresh.SetPointLight(new(new(0, 0, -4), 20, Vector3.One, 30));
        fresh.SetMeshTransform(0, new(new Vector3(0, 0, -10), Quaternion.Identity) { Stretch = new(8, 4, .1f) });
        fresh.SetMeshTransform(1, new(new Vector3(-4, 0, -6), Quaternion.Identity, .6f));
        fresh.UseMaterial(1, fresh.CreateRasterMaterial(RasterMaterialKind.Ordinary, new Vector4(1, 1, 1, 0)));
        var freshOther = fresh.AddGeometryMesh(plane, new(new Vector3(4, 0, -6), Quaternion.Identity, .6f));
        fresh.UseEntityMaterial(freshOther, fresh.CreateRasterMaterial(RasterMaterialKind.Ordinary, Vector4.One));
        RasterImage expected = await fresh.ReadNextRasterImageAsync();
        for (int frame = 1; frame <= 12; frame++) expected = await fresh.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(expected, after.Image);
    }
}
