using System.Numerics;
using SomeEngine.Resources;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Frame;

namespace SomeEngine.Runtime.Tests;

// #128: the full scene uses hardware although the former isolated Temporal pass ran on WARP.
[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class TemporalImageTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TemporalHistoryBlendsConsecutiveCompletedColors(string backend)
    {
        SceneImage? first = null, second = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(7, edit => edit.ResetCameraHistory()),
            new(8, edit =>
            {
                ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.8f, 0, 0, 1);
                edit.CaptureImage(image => first = image);
            }),
            new(9, edit => edit.CaptureImage(image => second = image)),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        SceneImages.AssertColorRegion(first!, 37, 29, 6, 6, MappedColor(.98f), 0, 0);
        SceneImages.AssertColorRegion(second!, 37, 29, 6, 6, MappedColor(.962f), 0, 0);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task CanceledTemporalFrameLeavesTheSphereColorsEqualToTheCompletedSequence(string backend)
    {
        SceneImage? warmup = null, referenceWarmup = null, actual = null, expected = null;
        var canceled = new SceneScript(content.Spheres, 13,
        [
            new(7, edit => { edit.ResetCameraHistory(); edit.CaptureImage(image => warmup = image); }),
            new(8, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.8f, 0, 0, 1)),
            new(9, edit =>
            {
                ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.1f, 0, 0, 1);
                edit.CancelFrame();
            }),
            new(10, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.8f, 0, 0, 1)),
            new(12, edit => edit.CaptureImage(image => actual = image)),
        ]) { TemporalSettings = new(.9f, 2, 1, 0), WaitForFrames = false };
        await SceneImages.RunAsync(backend, canceled, "--taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 12,
        [
            new(7, edit => { edit.ResetCameraHistory(); edit.CaptureImage(image => referenceWarmup = image); }),
            new(8, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.8f, 0, 0, 1)),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]) { TemporalSettings = new(.9f, 2, 1, 0), WaitForFrames = false }, "--taa", "--no-vsm");
        SceneImages.AssertColorRegion(warmup!, 37, 29, 6, 6, 186, 0, 0);
        SceneImages.AssertEqual(referenceWarmup!, warmup!);
        SceneImages.AssertColorRegion(actual!, 37, 29, 6, 6, MappedColor(.93122f), 0, 0);
        SceneImages.AssertEqualRegion(expected!, actual!, 37, 29, 6, 6);
        SceneImages.AssertEqualRegion(expected!, actual!, 21, 29, 6, 6);
        SceneImages.AssertEqualRegion(expected!, actual!, 0, 0, 8, 8);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task DisabledTemporalResolveShowsTheCurrentMaterialColor(string backend)
    {
        SceneImage? actual = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(8, edit =>
            {
                ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.2f, 0, 0, 1);
                edit.CaptureImage(image => actual = image);
            }),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.2f, 0, 0, 1)),
            new(8, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");
        SceneImages.AssertEqual(expected!, actual!);
        SceneImages.AssertColorRegion(actual!, 37, 29, 6, 6, MappedColor(.2f), 0, 0);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ResetCameraHistoryShowsTheNewColorAsAFreshTemporalSample(string backend)
    {
        SceneImage? actual = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(8, edit =>
            {
                ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.4f, 0, 0, 1);
                edit.ResetCameraHistory();
                edit.CaptureImage(image => actual = image);
            }),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit => ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(.4f, 0, 0, 1)),
            new(8, edit => { edit.ResetCameraHistory(); edit.CaptureImage(image => expected = image); }),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        SceneImages.AssertEqual(expected!, actual!);
        SceneImages.AssertColorRegion(actual!, 37, 29, 6, 6, MappedColor(.4f), 0, 0);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ChangingCameraWithAHistoryResetMatchesStartingAtThatCamera(string backend)
    {
        SceneImage? before = null, actual = null, expected = null;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.1f);
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(7, edit => edit.CaptureImage(image => before = image)),
            new(8, edit =>
            {
                edit.SetCameraTransform(new(1, 0, 0), rotation);
                edit.ResetCameraHistory();
                edit.CaptureImage(image => actual = image);
            }),
        ]), "--taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit => edit.SetCameraTransform(new(1, 0, 0), rotation)),
            new(8, edit => { edit.ResetCameraHistory(); edit.CaptureImage(image => expected = image); }),
        ]), "--taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), actual!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, actual);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task RemovedForegroundLeavesTheCurrentBackgroundColor(string backend)
    {
        SceneImage? actual = null, expected = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(7, edit => edit.ResetCameraHistory()),
            new(8, edit =>
            {
                edit.DeleteEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"));
                edit.CaptureImage(image => actual = image);
            }),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit => edit.DeleteEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))),
            new(7, edit => edit.ResetCameraHistory()),
            new(8, edit => edit.CaptureImage(image => expected = image)),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        SceneImages.AssertColorRegion(actual!, 37, 29, 6, 6, 0, 0, 0);
        SceneImages.AssertEqualRegion(expected!, actual!, 37, 29, 6, 6);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovingForegroundReprojectsHistoryAndExposesTheCurrentBackground(string backend)
    {
        SceneImage? actual = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(7, edit => edit.ResetCameraHistory()),
            new(8, edit =>
            {
                edit.SetEntityTransform(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"),
                    new(new Vector3(-3, 0, 6), Quaternion.Identity));
                edit.CaptureImage(image => actual = image);
            }),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        SceneImages.AssertColorRegion(actual!, 37, 29, 6, 6, 0, 0, 0);
        SceneImages.AssertColorRegion(actual!, 57, 29, 4, 6, 186, 0, 0);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task DefaultTemporalClampRejectsAnObsoleteMaterialColor(string backend)
    {
        SceneImage? actual = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(7, edit => edit.ResetCameraHistory()),
            new(8, edit =>
            {
                ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(0, 0, 0, 1);
                edit.CaptureImage(image => actual = image);
            }),
        ]), "--taa", "--no-vsm");
        // Default clamp bounds the red residue to .008 linear, or 28 after presentation.
        SceneImages.AssertColorRegion(actual!, 37, 29, 6, 6, 0, 0, 0, 28);
    }

    [SceneTheory]
    [InlineData("d3d12", 0.000000059604645f)]
    [InlineData("d3d12", 0.00003f)]
    [InlineData("d3d12", -0.00003f)]
    [InlineData("d3d12", -2f)]
    [InlineData("d3d12", 4f)]
    [InlineData("d3d12", 32752f)]
    [InlineData("vulkan", 0.000000059604645f)]
    [InlineData("vulkan", 0.00003f)]
    [InlineData("vulkan", -0.00003f)]
    [InlineData("vulkan", -2f)]
    [InlineData("vulkan", 4f)]
    [InlineData("vulkan", 32752f)]
    public async Task TemporalPreservesPresentedColorsAcrossRepeatedFrames(string backend, float color)
    {
        var images = new List<SceneImage>();
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(7, edit =>
            {
                ((UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere"))).BaseColorTint = new(color, color, color, 1);
                edit.ResetCameraHistory();
                edit.CaptureImage(images.Add);
            }),
            new(8, edit => edit.CaptureImage(images.Add)),
            new(9, edit => edit.CaptureImage(images.Add)),
        ]), "--taa", "--no-vsm");
        Assert.Equal(3, images.Count);
        byte expected = MappedColor((float)(Half)color);
        foreach (SceneImage image in images) SceneImages.AssertColorRegion(image, 37, 29, 6, 6, expected, expected, expected);
    }

    [SceneTheory]
    [InlineData("d3d12", 6f)]
    [InlineData("d3d12", 90f)]
    [InlineData("vulkan", 6f)]
    [InlineData("vulkan", 90f)]
    public async Task MovingForegroundEdgesMatchMovingTheCameraByTheOppositeOffset(string backend, float distance)
    {
        float scale = distance / 6;
        SceneImage? before = null, actual = null, expected = null;
        Entity foreground = default;
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit =>
            {
                foreground = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                edit.DeleteEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"));
                edit.SetEntityTransform(foreground, new(new Vector3(-scale, 0, distance), Quaternion.Identity, scale));
            }),
            new(7, edit => { edit.ResetCameraHistory(); edit.CaptureImage(image => before = image); }),
            new(8, edit =>
            {
                edit.SetEntityTransform(foreground, new(new Vector3(-1.1f * scale, 0, distance), Quaternion.Identity, scale));
                edit.CaptureImage(image => actual = image);
            }),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.Spheres, 10,
        [
            new(0, edit =>
            {
                Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                edit.DeleteEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"));
                edit.SetEntityTransform(red, new(new Vector3(-scale, 0, distance), Quaternion.Identity, scale));
            }),
            new(7, edit => edit.ResetCameraHistory()),
            new(8, edit =>
            {
                edit.SetCameraTransform(new(.1f * scale, 0, 0), Quaternion.Identity);
                edit.CaptureImage(image => expected = image);
            }),
        ]) { TemporalSettings = new(.9f, 2, 1, 0) }, "--taa", "--no-vsm");
        Assert.NotEqual(before!.Pixels.ToArray(), actual!.Pixels.ToArray());
        SceneImages.AssertColorRegion(actual!, 38, 29, 5, 6, 186, 0, 0);
        SceneImages.AssertEqual(expected!, actual!, 2);
    }

    private static byte MappedColor(float linear)
    {
        float positive = Math.Max(0, linear);
        return (byte)MathF.Round(255 * MathF.Pow(positive / (positive + 1), 1f / 2.2f));
    }
}
