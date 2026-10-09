using System.Numerics;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class VsmCoverageImageTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ExpandingReceiversDuringRepeatedCasterChangesPreservesFilteredShadows(string backend)
    {
        SceneImage? first = null, later = null, expected = null, unshadowed = null;
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            BlockerSampleCount = 16, FilterSampleCount = 16,
            BlockerSearchRadiusTexels = 4, MaxFilterRadiusTexels = 4,
            NormalBias = .1f, LocalReceiverMask = true, DynamicResolutionEnabled = false,
        };
        void Receiver(SceneEdit edit, float width) => edit.SetEntityTransform(edit.MeshEntities[0],
            new(new Vector3(0, 0, 8), Quaternion.Identity) { Stretch = new(width, 4, .1f) });
        void Caster(SceneEdit edit, float x) => edit.SetEntityTransform(edit.MeshEntities[1],
            new(new Vector3(x, 0, 6), Quaternion.Identity, .6f));
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => Receiver(edit, 1)),
            new(14, edit => Caster(edit, -.8f)),
            new(15, edit => Caster(edit, -.4f)),
            new(16, edit => { Receiver(edit, 4); Caster(edit, .8f); edit.CaptureImage(image => first = image); }),
            new(17, edit => Caster(edit, .4f)),
            new(18, edit => Caster(edit, -.4f)),
            new(19, edit => Caster(edit, -.8f)),
            new(20, edit => Caster(edit, -.4f)),
            new(21, edit => Caster(edit, .4f)),
            new(22, edit => Caster(edit, .6f)),
            new(23, edit => { Caster(edit, .8f); edit.CaptureImage(image => later = image); }),
        ]) { ShadowSettings = settings }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Receiver(edit, 4); Caster(edit, .8f); }),
            new(23, edit => edit.CaptureImage(image => expected = image)),
        ]) { ShadowSettings = settings with { LocalReceiverMask = false } }, "--no-taa", "--vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => { Receiver(edit, 4); Caster(edit, .8f); }),
            new(23, edit => edit.CaptureImage(image => unshadowed = image)),
        ]), "--no-taa", "--no-vsm");
        Assert.NotEqual(unshadowed!.Pixels.ToArray(), expected!.Pixels.ToArray());
        // All receiver pixels and filter edges, including the newly exposed area.
        SceneImages.AssertEqual(expected!, first!, 2);
        SceneImages.AssertEqual(expected!, later!, 2);
    }
}
