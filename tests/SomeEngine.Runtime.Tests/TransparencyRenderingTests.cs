using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class TransparencyRenderingTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task IntersectingTransparentModelsMatchCompleteDepthSortedCompositing(string backend)
    {
        SceneLinearImage? image = null, reversed = null, bounded = null;
        using var blue = new UnlitMaterial();
        var models = new List<(Entity Entity, TransformQvvs Transform)>();
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    var material = (UnlitMaterial)edit.GetMaterial(entity);
                    material.State = material.State with { Surface = SurfaceMode.Translucent };
                    material.BaseColorTint = material.Name == "red sphere" ? new(1, 0, 0, .5f) : new(0, 1, 0, .5f);
                    models.Add((entity, new(new Vector3(0, 0, 6), Quaternion.Identity)));
                }
                var first = models[0].Entity;
                var source = (UnlitMaterial)edit.GetMaterial(first);
                blue.Shader = source.Shader;
                blue.AlbedoMap = source.AlbedoMap;
                blue.MaterialSampler = source.MaterialSampler;
                blue.BaseColorTint = new(0, 0, 1, .5f);
                blue.State = source.State;
                for (int layer = 0; layer < 4; layer++)
                {
                    TransformQvvs transform = new(new Vector3(0, 0, 7 + layer), Quaternion.Identity);
                    Entity entity = edit.AddEntity(first, transform);
                    edit.ReplaceMaterial(entity, blue);
                    models.Add((entity, transform));
                }
            }),
            new(7, edit => edit.CaptureLinearImage(value => image = value)),
            new(8, edit =>
            {
                foreach (var model in models.AsEnumerable().Reverse()) edit.AddEntity(model.Entity, model.Transform);
                foreach (var model in models) edit.DeleteEntity(model.Entity);
            }),
            new(11, edit => edit.CaptureLinearImage(value => reversed = value)),
            new(12, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    Material material = edit.GetMaterial(entity);
                    material.State = material.State with { MaximumError = .04f };
                }
            }),
            new(15, edit => edit.CaptureLinearImage(value => bounded = value)),
        ]) { RasterEdgePixels = 32, WaitForFrames = false }, "--hardware-raster", "--no-taa", "--no-vsm");

        // Six planes cover these pixels. The first two give red/green (.5,.25)
        // or (.25,.5); four blue layers behind them add .25 * (1 - .5^4).
        Matrix4x4.Invert(image!.Frame.View * image.Frame.Projection, out Matrix4x4 inverse);
        var expected = image.Pixels.ToArray();
        for (int y = 25; y < 39; y++)
        for (int x = 23; x < 41; x++)
        {
            Vector4 world = Vector4.Transform(new Vector4((x + .5f) / 32 - 1, 1 - (y + .5f) / 32, .5f, 1), inverse);
            bool redNear = world.X / world.W < 0;
            int pixel = (y * 64 + x) * 4;
            expected[pixel] = (Half)(redNear ? .5f : .25f);
            expected[pixel + 1] = (Half)(redNear ? .25f : .5f);
            expected[pixel + 2] = (Half).234375f;
            expected[pixel + 3] = (Half)1;
        }
        SceneImages.AssertLinearEqual(image with { Pixels = expected }, image, .001f);
        SceneImages.AssertLinearEqual(image, reversed!, .001f);
        SceneImages.AssertLinearEqual(image, bounded!, .04f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TransparentUserMaterialsMatchOpaqueForwardLightingAndShadowsAtFullOpacity(string backend)
    {
        SceneLinearImage? opaque = null, transparent = null;
        using var nearest = new StandardMaterial();
        await SceneImages.RunAsync(backend, new SceneScript(content.Lit, 24,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                    switch (edit.GetMaterial(entity))
                    {
                        case StandardMaterial material when material.Name == "lit panel":
                            material.BaseColorTint = material.BaseColorTint with { W = 1 };
                            break;
                        case UnlitMaterial material:
                            material.BaseColorTint = material.BaseColorTint with { W = 1 };
                            break;
                    }
                Entity panel = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "lit panel");
                nearest.Update(edit.GetMaterial(panel));
                nearest.Name = "lit panel nearest";
                nearest.MaterialSampler = nearest.MaterialSampler with
                {
                    MinFilter = FilterType.Nearest, MagFilter = FilterType.Nearest, MipFilter = FilterType.Nearest,
                };
                Entity copy = edit.AddEntity(panel, new(new Vector3(-1.6f, 0, 4), Quaternion.Identity, .3f));
                edit.ReplaceMaterial(copy, nearest);
            }),
            new(11, edit => edit.CaptureLinearImage(value => opaque = value)),
            new(12, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    Material material = edit.GetMaterial(entity);
                    if (material.Name is "lit panel" or "lit panel nearest" or "unlit panel")
                        material.State = material.State with { Surface = SurfaceMode.Translucent };
                }
            }),
            new(23, edit => edit.CaptureLinearImage(value => transparent = value)),
        ]) { RasterEdgePixels = 32, WaitForFrames = false }, "--hardware-raster", "--shading-path", "forward", "--no-taa");
        Assert.True((float)opaque!.Pixels.Span[(64 * 128 + 80) * 4] > .01f);
        SceneImages.AssertLinearEqual(opaque, transparent!, .003f);
    }
}
