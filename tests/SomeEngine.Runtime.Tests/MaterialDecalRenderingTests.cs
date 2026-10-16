using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;

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
}
