using System.Numerics;
using System.Runtime.InteropServices;
using Game.Content;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class SceneInitializationTests(SceneContentFixture content)
{
    [Fact]
    public async Task SceneInitializationAndFrameEditsMatchTheSameScriptedScene()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-scene-initialization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "frame.rgba16");
            string[] arguments = ["--hardware-adapter", "--graphics-backend", "d3d12", "--hardware-raster", "--hidden-window",
                "--static-scene", "--fixed-step", "0.016666667", "--no-vsync", "--no-taa", "--no-vsm",
                "--no-debug-ui", "--frames", "12", "--linear-frame-output", path];
            var platform = new SceneTestPlatform([new("Initialized scene", content.DirectoryPath)],
                RuntimeStartupOptions.Parse(arguments))
            {
                InitializeScene = (edit, _) =>
                {
                    var red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                    ((UnlitMaterial)edit.GetMaterial(red)).BaseColorTint = new(.1f, .8f, .2f, 1);
                    edit.AddEntity(edit.GetMesh(red), [edit.GetMaterial(red)],
                        new(new Vector3(0, 0, 3), Quaternion.Identity, .35f));
                },
                UpdateScene = (edit, _, frame) =>
                {
                    var red = edit.MeshEntities.First(entity => edit.GetMaterial(entity).Name == "red sphere");
                    if (frame == 6)
                        edit.AddEntity(edit.GetMesh(red), [edit.GetMaterial(red)],
                            new(new Vector3(0, 1, 4), Quaternion.Identity, .3f));
                    if (frame == 9)
                        ((UnlitMaterial)edit.GetMaterial(red)).BaseColorTint = new(.7f, .2f, .9f, 1);
                },
            };
            Assert.Equal(0, await Application.RunAsync(arguments, platform: platform));
            SceneLinearImage actual = SceneLinearImage.Load(path);
            SceneLinearImage? expected = null;
            await SceneImages.RunAsync("d3d12", new SceneScript(content.DirectoryPath, 12,
            [
                new(0, edit =>
                {
                    var red = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                    ((UnlitMaterial)edit.GetMaterial(red)).BaseColorTint = new(.1f, .8f, .2f, 1);
                    edit.AddEntity(red, new(new Vector3(0, 0, 3), Quaternion.Identity, .35f));
                }),
                new(6, edit =>
                {
                    var red = edit.MeshEntities.First(entity => edit.GetMaterial(entity).Name == "red sphere");
                    edit.AddEntity(red, new(new Vector3(0, 1, 4), Quaternion.Identity, .3f));
                }),
                new(9, edit =>
                {
                    var red = edit.MeshEntities.First(entity => edit.GetMaterial(entity).Name == "red sphere");
                    ((UnlitMaterial)edit.GetMaterial(red)).BaseColorTint = new(.7f, .2f, .9f, 1);
                }),
                new(11, edit => edit.CaptureLinearImage(image => expected = image)),
            ]) { RasterEdgePixels = 32, WaitForFrames = false }, "--hardware-raster", "--no-taa", "--no-vsm");
            SceneImages.AssertEqual(new(11, expected!.Frame.Width * 2, expected.Frame.Height,
                MemoryMarshal.AsBytes(expected.Pixels.Span).ToArray()),
                new(11, actual.Frame.Width * 2, actual.Frame.Height, MemoryMarshal.AsBytes(actual.Pixels.Span).ToArray()));
        }
        finally { Directory.Delete(directory, true); }
    }
}
