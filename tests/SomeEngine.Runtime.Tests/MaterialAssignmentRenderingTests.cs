using SomeEngine.ECS.Entities;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class MaterialAssignmentRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task ReplacingOneSharedBindingMatchesStartingWithTheFinalCombination()
    {
        var blue = new UnlitMaterial { Name = "blue", BaseColorTint = new(0, 0, 1, 1) };
        SceneImage? shared = null, changed = null, expected = null;
        Entity left = default;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 4,
        [
            new(0, edit => left = ShareBindings(edit)),
            new(1, edit => edit.CaptureImage(image => shared = image)),
            new(2, edit => edit.ReplaceMaterial(left, blue)),
            new(3, edit => edit.CaptureImage(image => changed = image)),
        ]), "--no-taa", "--no-vsm");
        await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 4,
        [
            new(0, edit => edit.ReplaceMaterial(ShareBindings(edit), blue)),
            new(3, edit => edit.CaptureImage(image => expected = image)),
        ]), "--no-taa", "--no-vsm");

        Assert.NotEqual(shared!.Pixels.ToArray(), changed!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, changed);

        Entity ShareBindings(SceneEdit edit)
        {
            Entity red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
            Entity green = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere");
            var material = (UnlitMaterial)edit.GetMaterial(red);
            blue.Shader = material.Shader;
            blue.AlbedoMap = material.AlbedoMap;
            edit.ReplaceMaterial(green, material);
            return red;
        }
    }
}
