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
    [Fact]
    public async Task FarWorldOriginPreservesSubUnitCasterSpacingInRawAndBeautyImages()
    {
        SceneImage? near = null, far = null;
        SceneLinearImage? nearRaw = null, farRaw = null;
        var settings = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048,
            MaxShadowLights = 1, DirectionalFirstClipmapLevel = -4, DirectionalClipmapLevels = 16,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
            WorldUnitsPerCentimeter = 1, DynamicResolutionEnabled = false };
        void Configure(SceneEdit edit)
        {
            edit.SetDirectionalLight(edit.PointLights.Single(), new(Vector3.UnitZ, Vector3.One, 3));
            // Source sphere radius1: receiver front7.875 and caster back7.75
            // leave a1/8 world-unit gap, retained by local float transforms.
            edit.SetEntityTransform(edit.MeshEntities[0], new(new Vector3(0, 0, 8), Quaternion.Identity)
                { Stretch = new(4, 4, .125f) });
            edit.SetEntityTransform(edit.MeshEntities[1], new(new Vector3(0, 0, 7.25f), Quaternion.Identity, .5f));
        }
        await SceneImages.RunAsync("d3d12", new SceneScript(content.Shadows, 16,
        [
            new(0, edit => { Configure(edit); edit.SetWorldOrigin(new(.1875, 0, 0)); }),
            new(15, edit => { edit.CaptureImage(image => near = image); edit.CaptureLinearImage(image => nearRaw = image); }),
        ]) { ShadowSettings = settings }, "--no-taa", "--vsm", "--no-debug-ui");
        await SceneImages.RunAsync("d3d12", new SceneScript(content.Shadows, 16,
        [
            new(0, edit => { Configure(edit); edit.SetWorldOrigin(new(1099511627776.1875, 0, 0)); }),
            new(15, edit => { edit.CaptureImage(image => far = image); edit.CaptureLinearImage(image => farRaw = image); }),
        ]) { ShadowSettings = settings }, "--no-taa", "--vsm", "--no-debug-ui");
        SceneImages.AssertEqual(near!, far!);
        // The byte image retains every half-float bit and uses the same PNG failure artifacts.
        SceneImages.AssertEqual(new(15, nearRaw!.Frame.Width * 2, nearRaw.Frame.Height,
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(nearRaw.Pixels.Span).ToArray()),
            new(15, farRaw!.Frame.Width * 2, farRaw.Frame.Height,
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(farRaw.Pixels.Span).ToArray()));
        Assert.Equal(new WorldOrigin(1099511627776.1875, 0, 0), farRaw.Frame.WorldOrigin);
    }

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

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task CoarseDirectionalShadowsFollowTheCasterInTheFirstChangedFrame(string backend)
    {
        SceneImage? before = null, moved = null, expected = null, unshadowed = null;
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 512, MaxShadowLights = 1,
            DirectionalClipmapLevels = 1, DirectionalFirstClipmapLevel = 4,
            DirectionalFirstCoarseClipmapLevel = 4, DirectionalLastCoarseClipmapLevel = 4,
            DirectionalReceiverMask = false,
        };
        void Configure(SceneEdit edit)
        {
            edit.SetDirectionalLight(edit.PointLights.Single(), new(Vector3.Normalize(new(.4f, 0, 1)), Vector3.One, 3));
            // The scene authors the receiver first and the caster second.
            edit.SetEntityTransform(edit.MeshEntities[1],
                new(new Vector3(-.8f, 0, 6), Quaternion.Identity, .6f));
        }
        void Move(SceneEdit edit) => edit.SetEntityTransform(
            edit.MeshEntities[1],
            new(new Vector3(.8f, 0, 6), Quaternion.Identity, .6f));
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, Configure),
            new(15, edit => edit.CaptureImage(image => before = image)),
            new(16, edit => { Move(edit); edit.CaptureImage(image => moved = image); }),
        ]) { ShadowSettings = settings }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Configure(edit); Move(edit); }),
            new(23, edit => edit.CaptureImage(image => expected = image)),
        ]) { ShadowSettings = settings }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Configure(edit); Move(edit); }),
            new(23, edit => edit.CaptureImage(image => unshadowed = image)),
        ]) { ShadowSettings = settings }, "--no-taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), moved!.Pixels.ToArray());
        Assert.NotEqual(unshadowed!.Pixels.ToArray(), expected!.Pixels.ToArray());
        // Whole output, including both the old and new shadow footprints; no edge mask.
        SceneImages.AssertEqual(expected!, moved!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovingTheFrontCasterRecoversTheRearCasterShadowInTheSameFrame(string backend)
    {
        SceneImage? recovered = null, expected = null, unshadowed = null;
        Entity blocker = default;
        void Configure(SceneEdit edit)
        {
            blocker = edit.MeshEntities[1];
            edit.SetDirectionalLight(edit.PointLights.Single(), new(Vector3.Normalize(new(.4f, 0, 1)), Vector3.One, 3));
            edit.SetEntityTransform(blocker, new(new Vector3(0, 0, 6), Quaternion.Identity, 1.2f));
            edit.AddEntity(blocker, new(new Vector3(.4f, 0, 7), Quaternion.Identity, .6f));
        }
        void MoveBlocker(SceneEdit edit) => edit.SetEntityTransform(blocker,
            new(new Vector3(100, 0, 6), Quaternion.Identity, 1.2f));
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, Configure),
            new(16, edit => { MoveBlocker(edit); edit.CaptureImage(image => recovered = image); }),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Configure(edit); MoveBlocker(edit); }),
            new(23, edit => edit.CaptureImage(image => expected = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Configure(edit); MoveBlocker(edit); }),
            new(23, edit => edit.CaptureImage(image => unshadowed = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--no-vsm");
        Assert.NotEqual(unshadowed!.Pixels.ToArray(), expected!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, recovered!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task CanceledCasterFrameResumesWithTheSameShadowsAsFreshArrival(string backend)
    {
        SceneImage? resumed = null, expected = null;
        void Move(SceneEdit edit) => edit.SetEntityTransform(edit.MeshEntities[1],
            new(new Vector3(-1, 0, 6), Quaternion.Identity, .6f));
        var script = new SceneScript(content.Shadows, 24,
        [
            new(16, edit => { Move(edit); edit.CancelFrame(); }),
            new(17, edit => edit.CaptureImage(image => resumed = image)),
        ]) { ShadowSettings = CreateShadowSettings() };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, "--no-taa", "--vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(23, result.CompletedFrames);
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, Move),
            new(17, edit => edit.CaptureImage(image => expected = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        SceneImages.AssertEqual(expected!, resumed!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task StreamedCasterOutsideTheCameraStillShadowsTheVisibleReceiver(string backend)
    {
        SceneImage? shadowed = null, withoutCaster = null, unshadowed = null, unshadowedWithoutCaster = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.OffCameraShadows, 24,
            [new(23, edit => edit.CaptureImage(image => shadowed = image))])
            { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.OffCameraShadows, 24,
        [
            new(0, edit => edit.DeleteEntity(edit.MeshEntities[1])),
            new(23, edit => edit.CaptureImage(image => withoutCaster = image)),
        ]) { ShadowSettings = CreateShadowSettings() }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.OffCameraShadows, 24,
            [new(23, edit => edit.CaptureImage(image => unshadowed = image))]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.OffCameraShadows, 24,
        [
            new(0, edit => edit.DeleteEntity(edit.MeshEntities[1])),
            new(23, edit => edit.CaptureImage(image => unshadowedWithoutCaster = image)),
        ]), "--no-taa", "--no-vsm");
        // Removing the separate streamed mesh cannot change primary geometry output.
        SceneImages.AssertEqual(unshadowedWithoutCaster!, unshadowed!);
        // But it must change the shadow on the receiver, including its central interior.
        Assert.NotEqual(withoutCaster!.Pixels.ToArray(), shadowed!.Pixels.ToArray());
        int center = (shadowed.Height / 2 * shadowed.Width + shadowed.Width / 2) * 4;
        Assert.True(shadowed.Pixels.Span[center] < withoutCaster.Pixels.Span[center]);
    }

    private static VirtualShadowMapSettings CreateShadowSettings() => new()
    {
        VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
        DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
        DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
    };
}
