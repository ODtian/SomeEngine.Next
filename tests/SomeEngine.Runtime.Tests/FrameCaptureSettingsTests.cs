namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class FrameCaptureSettingsTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task OrdinaryLinearCaptureReportsTheRenderedRasterThreshold(string backend)
    {
        string directory = Path.Combine(Path.GetTempPath(), "someengine-capture-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "frame.rgba16");
            string[] arguments = ["--hardware-adapter", "--graphics-backend", backend,
                "--hidden-window", "--static-scene", "--fixed-step", "0.016666667",
                "--no-vsync", "--no-taa", "--no-vsm", "--no-debug-ui",
                "--frames", "4", "--linear-frame-output", path];
            var platform = new SceneTestPlatform([new TestScene("Capture settings", content.Spheres)],
                RuntimeStartupOptions.Parse(arguments));
            Assert.Equal(0, await Application.RunAsync(arguments, platform: platform));
            SceneLinearImage captured = SceneLinearImage.Load(path);
            Assert.Equal(32, captured.Frame.RasterEdgePixels);
        }
        finally { Directory.Delete(directory, true); }
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ScriptedLinearCaptureReportsItsSelectedRasterThreshold(string backend)
    {
        SceneLinearImage? captured = null;
        var script = new SceneScript(content.Spheres, 4,
            [new(3, edit => edit.CaptureLinearImage(image => captured = image))])
        {
            RasterEdgePixels = 12,
        };
        await SceneImages.RunAsync(backend, script, "--no-taa", "--no-vsm");
        Assert.Equal(12, captured!.Frame.RasterEdgePixels);
    }
}
