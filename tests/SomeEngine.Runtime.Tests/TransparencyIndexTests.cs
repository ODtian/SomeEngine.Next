using System.Numerics;
using Game.Content;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Transparency content")]
public sealed class TransparencyIndexTests(TransparencyContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task XyzCandidatesMatchFullScanWhileModelsAndCameraMoveAcrossCells(string backend)
    {
        var images = new List<SceneLinearImage[]>();
        foreach (var (index, triangles, references) in new[]
        {
            ("full", 4096u, 4096u), ("xyz", 4096u, 4096u), ("xyz", 1u, 1u),
        })
        {
            var frames = new SceneLinearImage[2];
            await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
            [
                new(0, edit =>
                {
                    foreach (var entity in edit.MeshEntities)
                    {
                        var material = (UnlitMaterial)edit.GetMaterial(entity);
                        material.State = material.State with { Surface = SurfaceMode.Translucent };
                        material.BaseColorTint = material.Name == "red sphere" ? new(1, 0, 0, .5f) : new(0, 1, 0, .5f);
                    }
                }),
                new(7, edit => edit.CaptureLinearImage(image => frames[0] = image)),
                new(8, edit =>
                {
                    foreach (var entity in edit.MeshEntities)
                        edit.SetEntityTransform(entity, new TransformQvvs(new Vector3(.25f, 0, 9), Quaternion.Identity));
                    edit.SetCameraTransform(new Vector3(.1f, 0, -2), Quaternion.Identity);
                }),
                new(15, edit => edit.CaptureLinearImage(image => frames[1] = image)),
            ]) { BlendTriangleCapacity = triangles, BlendReferenceCapacity = references, WaitForFrames = false },
                "--blend-index=" + index, "--hardware-raster", "--no-taa", "--no-vsm");
            images.Add(frames);
        }
        foreach (SceneLinearImage[] indexed in images.Skip(1))
            for (int frame = 0; frame < 2; frame++)
                SceneImages.AssertLinearEqual(images[0][frame], indexed[frame], .001f);
    }
}
