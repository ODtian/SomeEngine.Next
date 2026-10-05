using System.Numerics;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class VsmWindowImageTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12", "default")]
    [InlineData("d3d12", "vertex")]
    [InlineData("d3d12", "software")]
    [InlineData("vulkan", "default")]
    [InlineData("vulkan", "vertex")]
    [InlineData("vulkan", "software")]
    public async Task SparseReceiversKeepOldAndNewCasterFootprintsAcrossWindowBoundaries(string backend, string raster)
    {
        SceneImage? before = null, first = null, settled = null, expected = null, unshadowed = null;
        string[] route = raster == "vertex" ? ["--hardware-raster", "--no-mesh-shaders"] : [];
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
            DynamicResolutionEnabled = false,
        };
        void Configure(SceneEdit edit)
        {
            var receiver = edit.MeshEntities[0];
            var caster = edit.MeshEntities[1];
            edit.SetEntityTransform(receiver, new(new Vector3(-1.5f, 0, 8), Quaternion.Identity)
                { Stretch = new(.8f, 2, .1f) });
            edit.AddEntity(receiver, new(new Vector3(1.5f, 0, 8), Quaternion.Identity)
                { Stretch = new(.8f, 2, .1f) });
            // Broad light-space bounds span the hole between receivers, but the caster
            // is outside the camera frustum and cannot itself request the intervening pages.
            edit.SetEntityTransform(caster, new(new Vector3(0, 4, 4), Quaternion.Identity)
                { Stretch = new(1.6f, .2f, .5f) });
            edit.SetDirectionalLight(edit.PointLights.Single(),
                new(Vector3.Normalize(new(0, -1, 1)), Vector3.One, 3));
        }
        void Move(SceneEdit edit) => edit.SetEntityTransform(edit.MeshEntities[1],
            new(new Vector3(.8f, 4, 4), Quaternion.Identity) { Stretch = new(1.6f, .2f, .5f) });
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, Configure),
            new(15, edit => edit.CaptureImage(image => before = image)),
            new(16, edit => { Move(edit); edit.CaptureImage(image => first = image); }),
            new(23, edit => edit.CaptureImage(image => settled = image)),
        ]) { ShadowSettings = settings, RasterEdgePixels = raster == "software" ? 64 : 4 },
            ["--no-taa", "--vsm", .. route]);
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Configure(edit); Move(edit); }),
            new(23, edit => edit.CaptureImage(image => expected = image)),
        ]) { ShadowSettings = settings with { DirectionalReceiverMask = false },
            RasterEdgePixels = raster == "software" ? 64 : 4 }, ["--no-taa", "--vsm", .. route]);
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Configure(edit); Move(edit); }),
            new(23, edit => edit.CaptureImage(image => unshadowed = image)),
        ]) { ShadowSettings = settings, RasterEdgePixels = raster == "software" ? 64 : 4 },
            ["--no-taa", "--no-vsm", .. route]);
        Assert.NotEqual(before!.Pixels.ToArray(), first!.Pixels.ToArray());
        Assert.NotEqual(unshadowed!.Pixels.ToArray(), expected!.Pixels.ToArray());
        // Whole images retain receiver edges and both footprints. The fresh arrival
        // disables receiver masking, not geometry, resolution or valid receiver pixels.
        SceneImages.AssertEqual(expected!, first!, 2);
        SceneImages.AssertEqual(expected!, settled!, 2);
    }
}
