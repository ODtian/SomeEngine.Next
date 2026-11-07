using SomeEngine.Render.Components;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ViewInstanceSetRenderingTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12", "--instance-groups")]
    [InlineData("d3d12", "--no-taa")]
    public async Task SelectingAnInstanceSetMatchesKeepingOnlyItsMeshes(string backend, string option)
    {
        SceneImage? complete = null, selected = null, expected = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Initialize = frame =>
            {
                foreach (var entity in frame.Scene.MeshEntities)
                {
                    uint set = frame.Scene.GetMaterial(entity).Name == "green sphere" ? 7u : 3u;
                    frame.World.Add(entity, new InstanceSet(set));
                }
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(application, 12,
        [
            new(6, edit => edit.CaptureImage(image => complete = image)),
            new(7, edit => edit.SetCameraInstanceSets(new uint[] { 3 })),
            new(11, edit => edit.CaptureImage(image => selected = image)),
        ]), option, "--no-taa", "--no-vsm");
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 12,
        [
            new(0, edit => edit.DeleteEntity(edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "green sphere"))),
            new(11, edit => edit.CaptureImage(image => expected = image)),
        ]), option, "--no-taa", "--no-vsm");
        SceneImages.AssertColorRegion(complete!, 37, 29, 6, 6, 186, 0, 0);
        SceneImages.AssertColorRegion(complete!, 21, 29, 6, 6, 0, 186, 0);
        Assert.NotEqual(complete!.Pixels.ToArray(), selected!.Pixels.ToArray());
        SceneImages.AssertEqual(expected!, selected);
    }
}
