using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

// #128: WARP complete Cluster frames time out; VSM scene images require hardware.
[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class ShadowImageTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovingLightShadowsMatchStartingWithTheFinalLight(string backend)
    {
        SceneImage? before = null, first = null, settled = null, expected = null, unshadowed = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(15, edit => edit.CaptureImage(image => before = image)),
            new(16, edit =>
            {
                edit.SetPointLight(edit.PointLights.Single(), new(new(-4, 2, 3), 20, Vector3.One, 30));
                edit.CaptureImage(image => first = image);
            }),
            new(23, edit => edit.CaptureImage(image => settled = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => edit.SetPointLight(edit.PointLights.Single(), new(new(-4, 2, 3), 20, Vector3.One, 30))),
            new(23, edit => edit.CaptureImage(image => expected = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => edit.SetPointLight(edit.PointLights.Single(), new(new(-4, 2, 3), 20, Vector3.One, 30))),
            new(23, edit => edit.CaptureImage(image => unshadowed = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), first!.Pixels.ToArray());
        Assert.NotEqual(unshadowed!.Pixels.ToArray(), expected!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, first!, 2);
        SceneImages.AssertEqual(expected!, settled!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task DirectionalShadowsAfterCameraScrollMatchRefreshedShadows(string backend)
    {
        SceneImage? first = null, settled = null, refreshed = null, refreshedSettled = null, unshadowed = null;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.Atan(1f / 8));
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => edit.SetDirectionalLight(edit.PointLights.Single(), new(Vector3.Normalize(new(0, -.2f, 1)), Vector3.One, 3))),
            new(16, edit => { edit.SetCameraTransform(new(1, 0, 0), rotation); edit.CaptureImage(image => first = image); }),
            new(23, edit => edit.CaptureImage(image => settled = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => edit.SetDirectionalLight(edit.PointLights.Single(), new(Vector3.Normalize(new(0, -.2f, 1)), Vector3.One, 3))),
            new(16, edit => { edit.SetCameraTransform(new(1, 0, 0), rotation); edit.RefreshShadowMaps(); edit.CaptureImage(image => refreshed = image); }),
            new(23, edit => { edit.RefreshShadowMaps(); edit.CaptureImage(image => refreshedSettled = image); }),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => edit.SetDirectionalLight(edit.PointLights.Single(), new(Vector3.Normalize(new(0, -.2f, 1)), Vector3.One, 3))),
            new(16, edit => edit.SetCameraTransform(new(1, 0, 0), rotation)),
            new(23, edit => edit.CaptureImage(image => unshadowed = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--no-vsm");
        Assert.NotEqual(unshadowed!.Pixels.ToArray(), settled!.Pixels.ToArray());
        SceneImages.AssertEqual(refreshed!, first!, 2);
        SceneImages.AssertEqual(refreshedSettled!, settled!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12", .01f, "vertex")]
    [InlineData("d3d12", .01f, "mesh")]
    [InlineData("d3d12", .01f, "software")]
    [InlineData("d3d12", .1f, "vertex")]
    [InlineData("d3d12", .1f, "mesh")]
    [InlineData("d3d12", .1f, "software")]
    [InlineData("vulkan", .01f, "vertex")]
    [InlineData("vulkan", .01f, "mesh")]
    [InlineData("vulkan", .01f, "software")]
    [InlineData("vulkan", .1f, "vertex")]
    [InlineData("vulkan", .1f, "mesh")]
    [InlineData("vulkan", .1f, "software")]
    public async Task ReceiverMasksPreserveNormalOffsetShadows(string backend, float normalBias, string raster)
    {
        SceneImage? maskedFirst = null, masked = null, completeFirst = null, complete = null;
        string[] rasterOptions = raster switch
        {
            "vertex" => ["--no-mesh-shaders", "--hardware-raster"],
            "mesh" => ["--hardware-raster"],
            _ => [],
        };
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(16, edit => { edit.SetPointLight(edit.PointLights.Single(), new(new(-4, 2, 3), 20, Vector3.One, 30)); edit.CaptureImage(image => maskedFirst = image); }),
            new(23, edit => edit.CaptureImage(image => masked = image)),
        ]) { ShadowSettings = CreateShadowSettings() with { NormalBias = normalBias, LocalReceiverMask = true },
            RasterEdgePixels = raster == "software" ? 64 : 4 }, ["--no-taa", "--vsm", .. rasterOptions]);
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(16, edit => { edit.SetPointLight(edit.PointLights.Single(), new(new(-4, 2, 3), 20, Vector3.One, 30)); edit.CaptureImage(image => completeFirst = image); }),
            new(23, edit => edit.CaptureImage(image => complete = image)),
        ]) { ShadowSettings = CreateShadowSettings() with { NormalBias = normalBias, LocalReceiverMask = false },
            RasterEdgePixels = raster == "software" ? 64 : 4 }, ["--no-taa", "--vsm", .. rasterOptions]);
        SceneImages.AssertEqual(completeFirst!, maskedFirst!, 2);
        SceneImages.AssertEqual(complete!, masked!, 2);
    }

    private static VirtualShadowMapSettings CreateShadowSettings() => new()
    {
        VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
        DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
        DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
    };
}
