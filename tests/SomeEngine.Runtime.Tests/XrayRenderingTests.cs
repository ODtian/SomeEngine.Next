using System.Numerics;
using Game.Content;
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
    public async Task XraySidesBlendTheirOwnTexturedMaterialAgainstHardwareDepthVisibility(string backend)
    {
        SceneLinearImage? background = null, visible = null, surface = null, xray = null;
        Entity part = default, panel = default;
        TransformQvvs inside = new(new Vector3(0, 0, 6), Quaternion.Identity);
        TransformQvvs outside = new(new Vector3(20, 0, 6), Quaternion.Identity);
        await SceneImages.RunAsync(backend, new SceneScript(content.Lit, 32,
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
                edit.SetXray(part, new Xray(new XrayBlend(.25f), new XrayBlend(.7f), 0, 20));
            }),
            new(31, edit => edit.CaptureLinearImage(image => xray = image)),
        ]) { WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");

        const int first = 52, size = 24;
        Half[] expected = new Half[size * size * 4], actual = new Half[expected.Length];
        int visiblePixels = 0, occludedPixels = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int source = ((first + y) * 128 + first + x) * 4;
            int target = (y * size + x) * 4;
            bool front = MathF.Abs((float)visible!.Pixels.Span[source] - (float)surface!.Pixels.Span[source]) < .001f;
            if (front) visiblePixels++; else occludedPixels++;
            float alpha = front ? .25f : .7f;
            for (int channel = 0; channel < 3; channel++)
                expected[target + channel] = (Half)(alpha * (float)surface!.Pixels.Span[source + channel] +
                    (1 - alpha) * (float)background!.Pixels.Span[source + channel]);
            expected[target + 3] = background!.Pixels.Span[source + 3];
            xray!.Pixels.Span.Slice(source, 4).CopyTo(actual.AsSpan(target));
        }
        Assert.True(visiblePixels > 100 && occludedPixels > 100);
        SceneFrame frame = xray!.Frame with { Width = size, Height = size };
        SceneImages.AssertLinearEqual(new(frame, expected), new(frame, actual), .003f);
    }
}
