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
        using var scene = new SceneContentCopy(content.Spheres);
        await SomeEngine.SceneContent.SceneContent.SetOutputSizeAsync(scene.DirectoryPath, 640, 480);
        string directory = Path.Combine(Path.GetTempPath(), "someengine-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] startup = ["--hardware-adapter", "--graphics-backend", backend,
                "--hidden-window", "--static-scene", "--fixed-step", "0.016666667",
                "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"];
            SceneImage first = await CaptureAsync([.. startup, "--capture-frame", "7"], Path.Combine(directory, "first.bmp"), scene.DirectoryPath);
            SceneImage later = await CaptureAsync([.. startup, "--capture-frame", "11"], Path.Combine(directory, "later.bmp"), scene.DirectoryPath);
            SceneImage firstWithoutUi = await CaptureAsync([.. startup, "--capture-frame", "7", "--no-debug-ui"], Path.Combine(directory, "first-without-ui.bmp"), scene.DirectoryPath);
            SceneImage laterWithoutUi = await CaptureAsync([.. startup, "--capture-frame", "11", "--no-debug-ui"], Path.Combine(directory, "later-without-ui.bmp"), scene.DirectoryPath);
            // Verify that changes in the scene cannot satisfy the frame-upload assertion.
            SceneImages.AssertEqual(FrameText(firstWithoutUi), FrameText(laterWithoutUi));
            SceneImages.AssertNotEqual(FrameText(laterWithoutUi), FrameText(later));
            SceneImages.AssertNotEqual(FrameText(first), FrameText(later));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static SceneImage FrameText(SceneImage image)
    {
        const int left = 32, top = 46, width = 224, height = 32;
        byte[] pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            image.Pixels.Span.Slice(((top + row) * image.Width + left) * 4, width * 4)
                .CopyTo(pixels.AsSpan(row * width * 4));
        return new(image.Frame, width, height, pixels);
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
