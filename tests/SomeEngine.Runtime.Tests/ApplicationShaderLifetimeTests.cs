using Game.Content;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class ApplicationShaderLifetimeTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ApplicationsRenderTheSameSceneAcrossShaderCollection(string backend)
    {
        SceneLinearImage first = await RenderAndCloseApplication(backend);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        SceneLinearImage second = await RenderAndCloseApplication(backend);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        SceneImages.AssertLinearEqual(first, second, .001f);
    }

    private async Task<SceneLinearImage> RenderAndCloseApplication(string backend)
    {
        SceneLinearImage? image = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 8,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    Material material = edit.GetMaterial(entity);
                    material.State = material.State with { Surface = SurfaceMode.Translucent };
                }
            }),
            new(7, edit => edit.CaptureLinearImage(frame => image = frame)),
        ]), "--hardware-raster", "--no-taa", "--no-vsm", "--static-scene");
        return Assert.IsType<SceneLinearImage>(image);
    }
}
