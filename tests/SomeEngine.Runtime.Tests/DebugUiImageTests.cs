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
        // This is the test executable's settings file, never the application's.
        // The rendering collection serializes these in-process startup checks.
        string layoutPath = Path.Combine(AppContext.BaseDirectory, "imgui.ini");
        byte[]? previousLayout = File.Exists(layoutPath) ? File.ReadAllBytes(layoutPath) : null;
        try
        {
            File.WriteAllText(layoutPath, "[Window][SomeEngine Test Platform]\nPos=24,24\nSize=460,580\nCollapsed=0\n");
            string[] startup = ["--hardware-adapter", "--graphics-backend", backend,
                "--hidden-window", "--static-scene", "--fixed-step", "0.016666667",
                "--no-vsync", "--no-taa", "--no-vsm", "--frames", "12"];
            SceneImage first = await CaptureAsync([.. startup, "--capture-frame", "7"], Path.Combine(directory, "first.bmp"), scene.DirectoryPath);
            SceneImage later = await CaptureAsync([.. startup, "--capture-frame", "11"], Path.Combine(directory, "later.bmp"), scene.DirectoryPath);
            SceneImage firstWithoutUi = await CaptureAsync([.. startup, "--capture-frame", "7", "--no-debug-ui"], Path.Combine(directory, "first-without-ui.bmp"), scene.DirectoryPath);
            SceneImage laterWithoutUi = await CaptureAsync([.. startup, "--capture-frame", "11", "--no-debug-ui"], Path.Combine(directory, "later-without-ui.bmp"), scene.DirectoryPath);
            // Verify that changes in the scene cannot satisfy the frame-upload assertion.
            // Compare complete images so font scaling does not move text outside a crop.
            SceneImages.AssertEqual(firstWithoutUi, laterWithoutUi);
            SceneImages.AssertNotEqual(laterWithoutUi, later);
            SceneImages.AssertNotEqual(first, later);
        }
        finally
        {
            try
            {
                if (previousLayout is null) File.Delete(layoutPath);
                else File.WriteAllBytes(layoutPath, previousLayout);
            }
            finally { Directory.Delete(directory, true); }
        }
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
