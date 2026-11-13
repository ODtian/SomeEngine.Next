using DefaultGame.Content;
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
        var materials = new List<Material>();
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 8,
        [
            new(0, edit =>
            {
                foreach (var entity in edit.MeshEntities)
                {
                    materials.Add(edit.GetMaterial(entity));
                }
            }),
            new(7, edit => edit.CaptureLinearImage(frame => image = frame)),
        ])
        {
            CreateFrameFeatures = rendering =>
            {
                var pass = rendering.Layers!.AddPass(new("Shader lifetime layers"));
                foreach (Material material in materials) material.Layer = new(pass, 0, new());
                return [];
            },
        }, "--hardware-raster", "--no-taa", "--no-vsm", "--static-scene");
        return Assert.IsType<SceneLinearImage>(image);
    }
}
