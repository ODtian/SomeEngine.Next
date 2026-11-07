using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
public sealed class ProjectedTransparencyRenderingTests(SavedTransparencyContent content) : IClassFixture<SavedTransparencyContent>
{
    [SceneTheory]
    [InlineData("vulkan")]
    [InlineData("d3d12")]
    public async Task SavedCurvedGeometryMatchesAcrossRasterSourcesAndBufferBatches(string backend)
    {
        Shader shader = await content.GetCurvedShaderAsync();
        var images = new List<SceneLinearImage>();
        foreach (var (edgePixels, triangles, references, options) in new[]
        {
            (8f, 4096u, 4096u, new[] { "--hardware-raster" }),
            (256f, 4096u, 4096u, Array.Empty<string>()),
            (256f, 1u, 1u, Array.Empty<string>()),
        })
        {
            UnlitMaterial material = content.CreateMaterial(shader, new(.3f, .8f, .5f, .6f));
            material.State = material.State with { Surface = SurfaceMode.Translucent };
            await SceneImages.RunAsync(backend, new SceneScript(content.CreateApplication(), 8,
            [
                new(0, edit => edit.AddEntity(content.CurvedMesh, [material],
                    new(new Vector3(0, 0, 6), Quaternion.Identity), boundsExpansion: 1.31f)),
                new(7, edit => edit.CaptureLinearImage(images.Add)),
            ]) { RasterEdgePixels = edgePixels, BlendTriangleCapacity = triangles, BlendReferenceCapacity = references, WaitForFrames = false },
                ["--shading-path", "forward", "--no-taa", "--no-vsm", .. options]);
        }
        foreach (SceneLinearImage image in images.Skip(1)) SceneImages.AssertLinearEqual(images[0], image, .001f);
    }

    [SceneTheory]
    [InlineData("vulkan")]
    [InlineData("d3d12")]
    public async Task SavedUnorderedColorsMatchCompleteTransparencyAcrossProjectionBudgets(string backend)
    {
        var images = new List<SceneLinearImage>();
        foreach (uint capacity in new[] { 4096u, 1u })
        {
            UnlitMaterial first = content.CreateMaterial(content.Complete, new(.9f, .1f, .2f, .4f));
            UnlitMaterial second = content.CreateMaterial(content.Complete, new(.1f, .8f, .3f, .6f));
            first.State = first.State with { Surface = SurfaceMode.Translucent };
            second.State = second.State with { Surface = SurfaceMode.Translucent };
            Entity front = default, back = default;
            SceneLinearImage? background = null, complete = null, unordered = null;
            await SceneImages.RunAsync(backend, new SceneScript(content.CreateApplication(), 16,
            [
                new(0, edit =>
                {
                    front = edit.AddEntity(content.Panel, [first], new(new Vector3(20, 0, 6), Quaternion.Identity));
                    back = edit.AddEntity(content.Panel, [second], new(new Vector3(20, 0, 7), Quaternion.Identity));
                }),
                new(3, edit => edit.CaptureLinearImage(image => background = image)),
                new(4, edit =>
                {
                    edit.SetEntityTransform(front, new(new Vector3(0, 0, 6), Quaternion.Identity));
                    edit.SetEntityTransform(back, new(new Vector3(0, 0, 7), Quaternion.Identity));
                }),
                new(7, edit => edit.CaptureLinearImage(image => complete = image)),
                new(8, _ => { first.Shader = content.Unordered; second.Shader = content.Unordered; }),
                new(15, edit => edit.CaptureLinearImage(image => unordered = image)),
            ]) { BlendTriangleCapacity = capacity, BlendReferenceCapacity = capacity, WaitForFrames = false },
                "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
            SceneImages.AssertLinearNotEqual(background!, complete!);
            SceneImages.AssertLinearEqual(complete!, unordered!, .001f);
            images.Add(unordered!);
        }
        SceneImages.AssertLinearEqual(images[0], images[1], .001f);
    }

    [SceneTheory]
    [InlineData("vulkan")]
    [InlineData("d3d12")]
    public async Task SavedXrayKeepsVisibleAndOccludedTexturesThroughMotion(string backend)
    {
        UnlitMaterial part = content.CreateMaterial(content.Complete, new(.8f, 1, .6f, 1));
        UnlitMaterial blocker = content.CreateMaterial(content.Complete, new(.08f, .08f, .08f, 1));
        part.AlbedoMap = blocker.AlbedoMap = content.Checker;
        Entity entity = default;
        SceneLinearImage? complete = null, unordered = null, moved = null, movedComplete = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.CreateApplication(), 32,
        [
            new(0, edit =>
            {
                entity = edit.AddEntity(content.Panel, [part], new(new Vector3(0, 0, 6), Quaternion.Identity));
                edit.AddEntity(content.Panel, [blocker], new(new Vector3(1.5f, 0, 4), Quaternion.Identity, .7f));
                edit.SetXray(entity, new(new XrayBlend(.25f), new XrayBlend(.7f), 0, 20));
            }),
            new(7, edit => edit.CaptureLinearImage(image => complete = image)),
            new(8, _ => { part.Shader = content.Unordered; blocker.Shader = content.Unordered; }),
            new(15, edit => edit.CaptureLinearImage(image => unordered = image)),
            new(16, edit => edit.SetEntityTransform(entity, new(new Vector3(-.4f, 0, 6), Quaternion.Identity))),
            new(23, edit => edit.CaptureLinearImage(image => moved = image)),
            new(24, _ => { part.Shader = content.Complete; blocker.Shader = content.Complete; }),
            new(31, edit => edit.CaptureLinearImage(image => movedComplete = image)),
        ]) { BlendTriangleCapacity = 1, BlendReferenceCapacity = 1, WaitForFrames = false },
            "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearEqual(complete!, unordered!, .003f);
        SceneImages.AssertLinearNotEqual(unordered!, moved!);
        SceneImages.AssertLinearEqual(movedComplete!, moved!, .003f);
    }

    [SceneTheory]
    [InlineData("vulkan")]
    [InlineData("d3d12")]
    public async Task SavedOrderedColorsFollowUnorderedSurfacesAndKeepStateEdits(string backend)
    {
        UnlitMaterial first = content.CreateMaterial(content.Unordered, new(.1f, .3f, .9f, .4f));
        UnlitMaterial second = content.CreateMaterial(content.Unordered, new(.9f, .2f, .1f, .6f));
        first.State = first.State with { Surface = SurfaceMode.Translucent };
        second.State = second.State with { Surface = SurfaceMode.Translucent };
        SceneLinearImage? before = null, ordered = null, complete = null, removed = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.CreateApplication(), 32,
        [
            new(0, edit =>
            {
                edit.AddEntity(content.Panel, [first], new(new Vector3(0, 0, 6), Quaternion.Identity));
                edit.AddEntity(content.Panel, [second], new(new Vector3(0, 0, 7), Quaternion.Identity));
            }),
            new(7, edit => edit.CaptureLinearImage(image => before = image)),
            new(8, _ => second.State = second.State with
            {
                Ordered = true, DrawOrder = 10, DepthStencil = new(),
                Blend = new(Enabled: true, SourceColor: BlendFactor.SourceAlpha, DestinationColor: BlendFactor.OneMinusSourceAlpha,
                    SourceAlpha: BlendFactor.Zero, DestinationAlpha: BlendFactor.One),
            }),
            new(15, edit => edit.CaptureLinearImage(image => ordered = image)),
            new(16, _ => { first.Shader = content.Complete; second.Shader = content.Complete; }),
            new(23, edit => edit.CaptureLinearImage(image => complete = image)),
            new(24, _ =>
            {
                first.Shader = second.Shader = content.Unordered;
                second.State = second.State with { Ordered = false };
            }),
            new(31, edit => edit.CaptureLinearImage(image => removed = image)),
        ]) { WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearNotEqual(before!, ordered!);
        SceneImages.AssertLinearEqual(complete!, ordered!, .001f);
        SceneImages.AssertLinearEqual(before!, removed!, .001f);
    }

    [SceneTheory]
    [InlineData("vulkan")]
    [InlineData("d3d12")]
    public async Task SavedOrderedStencilReachesHardwareAfterLateAdditionAndRemoval(string backend)
    {
        UnlitMaterial blue = content.CreateMaterial(content.Unordered, new(.1f, .3f, .8f, .6f));
        blue.State = blue.State with { Surface = SurfaceMode.Translucent };
        UnlitMaterial mask = content.CreateMaterial(content.Unordered, Vector4.One);
        Entity writer = default;
        Matrix4x4 view = default;
        var read = new StencilFaceState(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Keep, CompareOperation.Equal);
        MaterialState state = MaterialState.Default with
        {
            TwoSided = true, StencilRef = 1,
            DepthStencil = new(StencilTest: true, StencilWriteMask: 0, Front: read, Back: read),
        };
        var hardware = new HardwareOrderedDraw([
            new(-1.8f, -1.8f, 4), new(-1.8f, 1.8f, 4), new(1.8f, -1.8f, 4),
            new(1.8f, -1.8f, 4), new(-1.8f, 1.8f, 4), new(1.8f, 1.8f, 4)], new(.8f, .2f, .1f, 1), state) { Enabled = true };
        SceneLinearImage? before = null, written = null, removed = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.CreateApplication(), 24,
        [
            new(0, edit =>
            {
                view = edit.CameraView;
                edit.AddEntity(content.Panel, [blue], new(new Vector3(0, 0, 6), Quaternion.Identity));
                writer = edit.AddEntity(content.Panel, [mask], new(new Vector3(20, 0, 4), Quaternion.Identity));
            }),
            new(7, edit => edit.CaptureLinearImage(image => before = image)),
            new(8, edit =>
            {
                var write = new StencilFaceState(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Replace, CompareOperation.Always);
                mask.State = mask.State with
                {
                    Ordered = true, StencilRef = 1, Blend = new(WriteMask: ColorWriteMasks.None),
                    DepthStencil = new(StencilTest: true, Front: write, Back: write),
                };
                edit.SetEntityTransform(writer, new(new Vector3(0, 0, 4), Quaternion.Identity));
            }),
            new(15, edit => edit.CaptureLinearImage(image => written = image)),
            new(16, edit =>
            {
                mask.State = mask.State with { Ordered = false };
                edit.SetEntityTransform(writer, new(new Vector3(20, 0, 4), Quaternion.Identity));
            }),
            new(23, edit => edit.CaptureLinearImage(image => removed = image)),
        ])
        {
            BlendTriangleCapacity = 1, BlendReferenceCapacity = 1, WaitForFrames = false,
            CreateColorFeature = rendering => new HardwareOrderedFeature(rendering, [hardware], view),
        }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearNotEqual(before!, written!);
        Assert.InRange((float)written!.Pixels.Span[(32 * 64 + 32) * 4], .79f, .81f);
        SceneImages.AssertLinearEqual(before!, removed!, .001f);
    }
}
