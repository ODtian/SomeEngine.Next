using SomeEngine.Resources;
using System.Numerics;
using DefaultGame.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class XrayRenderingTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task OccludedXraySurfacesKeepTheirOwnHardwareMaterialShadows(string backend)
    {
        SceneLinearImage? hardware = null, xray = null, unshadowed = null;
        Entity part = default, panel = default, caster = default;
        await SceneImages.RunAsync(backend, new SceneScript(content.Lit, 32,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                    switch (edit.GetMaterial(entity))
                    {
                        case StandardMaterial material when material.Name == "lit panel":
                            part = entity;
                            material.BaseColorTint = material.BaseColorTint with { W = 1 };
                            break;
                        case UnlitMaterial material when material.Name == "unlit panel":
                            panel = entity;
                            material.BaseColorTint = material.BaseColorTint with { W = 1 };
                            edit.SetEntityTransform(panel, new(new Vector3(20, 0, 6), Quaternion.Identity));
                            break;
                        case StandardMaterial material when material.Name == "shadow caster":
                            caster = entity;
                            edit.SetEntityTransform(caster, new(new Vector3(2.25f, 1, 5.625f), Quaternion.Identity, .25f));
                            break;
                    }
                foreach (Entity light in edit.PointLights)
                    edit.SetPointLight(light, new(new Vector3(4, 2, 5), 20, Vector3.One, 40));
            }),
            new(11, edit => edit.CaptureLinearImage(image => hardware = image)),
            new(12, edit =>
            {
                edit.SetEntityTransform(panel, new(new Vector3(0, 0, 6), Quaternion.Identity));
                edit.SetXray(part, new(new XrayBlend(0), new XrayBlend(1), 0, 20));
            }),
            new(23, edit => edit.CaptureLinearImage(image => xray = image)),
            new(24, edit => edit.SetEntityTransform(caster, new(new Vector3(20, 1, 5.625f), Quaternion.Identity, .25f))),
            new(31, edit => edit.CaptureLinearImage(image => unshadowed = image)),
        ]) { WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa");

        const int width = 16, height = 20;
        Half[] expected = new Half[width * height * 4], actual = new Half[expected.Length];
        int shadowPixels = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int source = ((54 + y) * 128 + 46 + x) * 4;
            int target = (y * width + x) * 4;
            hardware!.Pixels.Span.Slice(source, 4).CopyTo(expected.AsSpan(target));
            xray!.Pixels.Span.Slice(source, 4).CopyTo(actual.AsSpan(target));
            if (MathF.Abs((float)unshadowed!.Pixels.Span[source] - (float)hardware.Pixels.Span[source]) > .005f)
                shadowPixels++;
        }
        Assert.True(shadowPixels > 10);
        SceneFrame frame = xray!.Frame with { Width = width, Height = height };
        SceneImages.AssertLinearEqual(new(frame, expected), new(frame, actual), .003f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task XraySidesKeepTheirOwnTexturedMaterialThroughMotionAndEffectChanges(string backend)
    {
        SceneLinearImage? background = null, visible = null, surface = null, xray = null;
        SceneLinearImage? removed = null, moved = null, returned = null;
        Entity part = default, panel = default;
        TransformQvvs inside = new(new Vector3(0, 0, 6), Quaternion.Identity);
        TransformQvvs outside = new(new Vector3(20, 0, 6), Quaternion.Identity);
        var rules = new Xray(new XrayBlend(.25f), new XrayBlend(.7f), 0, 20);
        await SceneImages.RunAsync(backend, new SceneScript(content.Lit, 56,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                {
                    switch (edit.GetMaterial(entity))
                    {
                        case StandardMaterial material when material.Name == "lit panel":
                            part = entity;
                            material.BaseColorTint = material.BaseColorTint with { W = 1 };
                            edit.SetEntityTransform(entity, outside);
                            break;
                        case UnlitMaterial material when material.Name == "unlit panel":
                            panel = entity;
                            material.BaseColorTint = material.BaseColorTint with { W = 1 };
                            break;
                    }
                }
            }),
            new(7, edit => edit.CaptureLinearImage(image => background = image)),
            new(8, edit => edit.SetEntityTransform(part, inside)),
            new(15, edit => edit.CaptureLinearImage(image => visible = image)),
            new(16, edit => edit.SetEntityTransform(panel, outside)),
            new(23, edit => edit.CaptureLinearImage(image => surface = image)),
            new(24, edit =>
            {
                edit.SetEntityTransform(panel, inside);
                edit.SetXray(part, rules);
            }),
            new(31, edit => edit.CaptureLinearImage(image => xray = image)),
            new(32, edit => edit.RemoveXray(part)),
            new(39, edit => edit.CaptureLinearImage(image => removed = image)),
            new(40, edit =>
            {
                Entity previous = part;
                part = edit.AddEntity(previous, new(new Vector3(.75f, 0, 6), Quaternion.Identity));
                edit.SetXray(part, rules);
                edit.DeleteEntity(previous);
            }),
            new(47, edit => edit.CaptureLinearImage(image => moved = image)),
            new(48, edit => edit.SetEntityTransform(part, inside)),
            new(55, edit => edit.CaptureLinearImage(image => returned = image)),
        ]) { WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");

        const int first = 52, size = 24;
        Half[] expected = new Half[size * size * 4], actual = new Half[expected.Length];
        int visiblePixels = 0, occludedPixels = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int source = ((first + y) * 128 + first + x) * 4;
            int target = (y * size + x) * 4;
            bool front = true;
            for (int channel = 0; channel < 3; channel++)
                front &= MathF.Abs((float)visible!.Pixels.Span[source + channel] - (float)surface!.Pixels.Span[source + channel]) < .001f;
            if (front) visiblePixels++; else occludedPixels++;
            float alpha = front ? .25f : .7f;
            for (int channel = 0; channel < 3; channel++)
                expected[target + channel] = (Half)(alpha * (float)surface!.Pixels.Span[source + channel] +
                    (1 - alpha) * (float)background!.Pixels.Span[source + channel]);
            expected[target + 3] = background!.Pixels.Span[source + 3];
            xray!.Pixels.Span.Slice(source, 4).CopyTo(actual.AsSpan(target));
        }
        SceneFrame frame = xray!.Frame with { Width = size, Height = size };
        SceneImages.AssertLinearEqual(new(frame, expected), new(frame, actual), .003f);
        Assert.True(visiblePixels > 100 && occludedPixels > 100, $"Visible pixels: {visiblePixels}; occluded pixels: {occludedPixels}.");
        SceneImages.AssertLinearEqual(visible!, removed!, .003f);
        SceneImages.AssertLinearEqual(xray, returned!, .003f);
        int changedPixels = 0;
        for (int index = 0; index < moved!.Pixels.Length; index += 4)
            if (MathF.Abs((float)moved.Pixels.Span[index] - (float)xray.Pixels.Span[index]) > .01f) changedPixels++;
        Assert.True(changedPixels > 100);
    }
}
