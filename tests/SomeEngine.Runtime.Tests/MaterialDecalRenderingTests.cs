using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class MaterialDecalRenderingTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task OpaqueSurfacesBlendDecalLayersInSpecifiedOrder(string backend)
    {
        SceneLinearImage? image = null;
        using var red = new StandardMaterial { BaseColorTint = new(1, 0, 0, .5f) };
        using var green = new StandardMaterial { BaseColorTint = new(0, 1, 0, .5f) };
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 8,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                    ((UnlitMaterial)edit.GetMaterial(entity)).BaseColorTint = new(0, 0, 1, 1);
                var source = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities[0]);
                red.Shader = green.Shader = source.Shader;
                red.AlbedoMap = green.AlbedoMap = source.AlbedoMap;
                TransformQvvs volume = new(new Vector3(0, 0, 7), Quaternion.Identity) { Stretch = new(4, 4, 4) };
                edit.AddMaterialDecal(green, volume, 2);
                edit.AddMaterialDecal(red, volume, 1);
            }),
            new(7, edit => edit.CaptureLinearImage(value => image = value)),
        ]) { WaitForFrames = false }, "--hardware-raster", "--no-taa", "--no-vsm");
        Half[] expected = image!.Pixels.ToArray();
        for (int y = 25; y < 39; y++)
        for (int x = 23; x < 41; x++)
        {
            int pixel = (y * 64 + x) * 4;
            expected[pixel] = (Half).25f;
            expected[pixel + 1] = (Half).5f;
            expected[pixel + 2] = (Half).25f;
            expected[pixel + 3] = (Half)1;
        }
        SceneImages.AssertLinearEqual(image with { Pixels = expected }, image, .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task TransparentHitsFollowMaterialDecalVolumesAcrossSceneEdits(string backend)
    {
        SceneLinearImage? before = null, rear = null, front = null, removed = null;
        Entity decal = default;
        using var red = new StandardMaterial { BaseColorTint = new(1, 0, 0, 1) };
        TransformQvvs volume = new(new Vector3(0, 0, 6), Quaternion.Identity) { Stretch = new(3, 3, .1f) };
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                foreach (Entity entity in edit.MeshEntities)
                {
                    var material = (UnlitMaterial)edit.GetMaterial(entity);
                    bool nearest = material.Name == "red sphere";
                    material.State = material.State with { Surface = SurfaceMode.Translucent };
                    material.BaseColorTint = new(0, 0, 1, .5f);
                    edit.SetEntityTransform(entity, new(new Vector3(0, 0, nearest ? 4 : 6),
                        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan(nearest ? .5f : -.5f))));
                }
                var source = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities[0]);
                red.Shader = source.Shader;
                red.AlbedoMap = source.AlbedoMap;
            }),
            new(3, edit => edit.CaptureLinearImage(value => before = value)),
            new(4, edit => decal = edit.AddMaterialDecal(red, volume)),
            new(7, edit => edit.CaptureLinearImage(value => rear = value)),
            new(8, edit =>
            {
                TransformQvvs moved = volume;
                moved.Position = new(0, 0, 4);
                edit.SetEntityTransform(decal, moved);
            }),
            new(11, edit => edit.CaptureLinearImage(value => front = value)),
            new(12, edit => edit.DeleteEntity(decal)),
            new(15, edit => edit.CaptureLinearImage(value => removed = value)),
        ]) { BlendReferenceCapacity = 1, WaitForFrames = false }, "--hardware-raster", "--no-taa", "--no-vsm");

        foreach ((SceneLinearImage image, Vector3 color) in new[]
        {
            (before!, new Vector3(0, 0, .75f)),
            (rear!, new Vector3(.25f, 0, .5f)),
            (front!, new Vector3(.5f, 0, .25f)),
        })
        {
            Half[] expected = image.Pixels.ToArray();
            for (int y = 25; y < 39; y++)
            for (int x = 23; x < 41; x++)
            {
                int pixel = (y * 64 + x) * 4;
                expected[pixel] = (Half)color.X;
                expected[pixel + 1] = (Half)color.Y;
                expected[pixel + 2] = (Half)color.Z;
                expected[pixel + 3] = (Half)1;
            }
            SceneImages.AssertLinearEqual(image with { Pixels = expected }, image, .001f);
        }
        SceneImages.AssertLinearEqual(before!, removed!, .001f);
    }
}
