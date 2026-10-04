using System.Numerics;

namespace SomeEngine.Runtime.Tests;

// #128: full Cluster scene composition requires hardware on this baseline.
[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class RenderingFeatureTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12", "--no-vsm", "--no-taa")]
    [InlineData("d3d12", "--vsm", "--no-taa")]
    [InlineData("d3d12", "--no-vsm", "--taa")]
    [InlineData("d3d12", "--vsm", "--taa")]
    [InlineData("vulkan", "--no-vsm", "--no-taa")]
    [InlineData("vulkan", "--vsm", "--no-taa")]
    [InlineData("vulkan", "--no-vsm", "--taa")]
    [InlineData("vulkan", "--vsm", "--taa")]
    public async Task VsmAndTemporalCombinationsPresentTheDeclaredSphereColors(string backend, string vsm, string temporal)
    {
        SceneImage? image = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(7, edit => edit.ResetCameraHistory()),
            new(11, edit => edit.CaptureImage(value => image = value)),
        ]), vsm, temporal);
        SceneImages.AssertColorRegion(image!, 37, 29, 6, 6, 186, 0, 0);
        SceneImages.AssertColorRegion(image!, 21, 29, 6, 6, 0, 186, 0);
        SceneImages.AssertColorRegion(image!, 0, 0, 8, 8, 0, 0, 0);
    }
    [SceneTheory]
    [InlineData("d3d12", "--no-vsm", "--no-taa")]
    [InlineData("d3d12", "--vsm", "--no-taa")]
    [InlineData("d3d12", "--no-vsm", "--taa")]
    [InlineData("d3d12", "--vsm", "--taa")]
    [InlineData("vulkan", "--no-vsm", "--no-taa")]
    [InlineData("vulkan", "--vsm", "--no-taa")]
    [InlineData("vulkan", "--no-vsm", "--taa")]
    [InlineData("vulkan", "--vsm", "--taa")]
    public async Task VsmAndTemporalCombinationsMatchTheFinalLightWithFreshCameraHistory(string backend, string vsm, string temporal)
    {
        SceneImage? actual = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(16, edit =>
            {
                edit.SetPointLight(edit.PointLights.Single(), new(new(-4, 2, 3), 20, Vector3.One, 30));
                edit.ResetCameraHistory();
                edit.CaptureImage(image => actual = image);
            }),
        ]) { ShadowSettings = new() { VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1 } }, vsm, temporal);
        await SceneImages.RunAsync(backend, new SceneScript(content.Shadows, 24,
        [
            new(0, edit => edit.SetPointLight(edit.PointLights.Single(), new(new(-4, 2, 3), 20, Vector3.One, 30))),
            new(16, edit => { edit.ResetCameraHistory(); edit.CaptureImage(image => expected = image); }),
        ]) { ShadowSettings = new() { VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1 } }, vsm, temporal);
        SceneImages.AssertEqual(expected!, actual!, 2);
    }
}
