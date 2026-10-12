using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class FirstPersonShadowRenderingTests
{
    [Fact]
    public async Task APlayersWorldRepresentationCastsIntoTheWorldThroughItsFirstPersonShadowFamily()
    {
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, ForceHardwareRaster = true,
            EnableFrameMetricsReadback = true };
        var settings = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048,
            MaxShadowLights = 1, DirectionalFirstClipmapLevel = 10, DirectionalClipmapLevels = 3,
            FirstPersonFirstClipmapLevel = 10, FirstPersonLastClipmapLevel = 12, DirectionalMinCameraViewportWidth = 2048,
            DirectionalCoarsePageMode = VirtualShadowCoarsePageMode.Disabled,
            DirectionalReceiverMask = false, DirectionalResolutionLodBias = 0, DirectionalMovingResolutionLodBias = 0,
            MaxFilterRadiusTexels = 0, DynamicResolutionEnabled = false };
        var view = new RenderView(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100),
            64, 64, PlayerId: 1);
        await using var firstPerson = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings);
        firstPerson.SetView(view);
        firstPerson.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(2, 2, .1f) });
        firstPerson.SetMeshTransform(1, new(new Vector3(0, 6, -4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .1f) });
        firstPerson.UseMaterial(1, firstPerson.CreateRasterMaterial(RasterMaterialKind.Ordinary, Vector4.One));
        firstPerson.SetMeshFirstPerson(1, new(FirstPersonMeshKind.WorldRepresentation, 1));
        firstPerson.UseDirectionalLight(Vector3.Normalize(new(0, -1.5f, -1)));
        RasterImage actual = await firstPerson.ReadNextRasterImageAsync();
        actual = await firstPerson.ReadNextRasterImageAsync();

        // A regular world caster outside the camera gives the independently
        // equivalent world shadow. At eight meters, log2(800) is below level ten.
        // The explicit 2048 density reference makes 0.5*4096/2048 equal one,
        // so both families request the same level-ten frustum with zero bias.
        await using var world = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings);
        world.SetView(view);
        world.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(2, 2, .1f) });
        world.SetMeshTransform(1, new(new Vector3(0, 6, -4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .1f) });
        world.UseMaterial(1, world.CreateRasterMaterial(RasterMaterialKind.Ordinary, Vector4.One));
        world.UseDirectionalLight(Vector3.Normalize(new(0, -1.5f, -1)));
        RasterImage expected = await world.ReadNextRasterImageAsync();
        expected = await world.ReadNextRasterImageAsync();
        world.SetMeshTransform(1, new(new Vector3(20, 6, -4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .1f) });
        RasterImage unshadowed = await world.ReadNextRasterImageAsync();
        Assert.NotEqual(unshadowed.Color, expected.Color);
        RasterImage.AssertEqual(expected, actual);
    }
}
