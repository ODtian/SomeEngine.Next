using SkiaSharp;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class DebugUiImageTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task OrdinaryDebugWindowRendersVisiblePixelsAcrossGraphFrameUploads(string backend)
    {
        // The script overload deliberately hides debug UI. Exercise ordinary
        // public startup instead, with its default debug window enabled.
        string directory = Path.Combine(Path.GetTempPath(), "someengine-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] startup = ["--hardware-adapter", "--graphics-backend", backend,
                "--hidden-window", "--static-scene", "--fixed-step", "0.016666667", "--no-vsync", "--frames", "4"];
            SceneImage first = await CaptureAsync([.. startup, "--capture-frame", "1"], Path.Combine(directory, "first.bmp"), content.Spheres);
            SceneImage later = await CaptureAsync([.. startup, "--capture-frame", "3"], Path.Combine(directory, "later.bmp"), content.Spheres);
            SceneImage withoutUi = await CaptureAsync([.. startup, "--capture-frame", "3", "--no-debug-ui"], Path.Combine(directory, "without-ui.bmp"), content.Spheres);
            SceneImages.AssertNotEqual(withoutUi, later);
            SceneImages.AssertNotEqual(first, later);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<SceneImage> CaptureAsync(string[] arguments, string path, string contentDirectory)
    {
        string[] capture = [.. arguments, "--frame-output", path];
        var platform = new SceneTestPlatform([new TestScene("UI scene", contentDirectory)], RuntimeStartupOptions.Parse(capture));
        Assert.Equal(0, await Application.RunAsync(capture, platform: platform));
        using var image = SKBitmap.Decode(path);
        byte[] rgba = new byte[image.Width * image.Height * 4];
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            SKColor color = image.GetPixel(x, y);
            int pixel = (y * image.Width + x) * 4;
            rgba[pixel] = color.Red;
            rgba[pixel + 1] = color.Green;
            rgba[pixel + 2] = color.Blue;
            rgba[pixel + 3] = color.Alpha;
        }
        return new(0, image.Width, image.Height, rgba);
    }
}
