namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class DebugViewRenderingTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task IndependentModesLeaveMainColorUnchangedAndClosingRestoresPresentation(string backend)
    {
        using var scene = new SceneContentCopy(content.DirectoryPath);
        await SomeEngine.SceneContent.SceneContent.SetOutputSizeAsync(scene.DirectoryPath, 640, 480);
        var images = new List<SceneImage>();
        var linear = new List<SceneLinearImage>();
        var script = new SceneScript(scene.DirectoryPath, 14,
        [
            new(3, edit => { edit.CaptureImage(images.Add); edit.CaptureLinearImage(linear.Add); }),
            new(4, edit => edit.SetDebugViews(new(9, 200, 128), new(10, 200, 128))),
            new(7, edit => { edit.CaptureImage(images.Add); edit.CaptureLinearImage(linear.Add); }),
            new(8, edit => edit.SetDebugViews(new(9, 200, 128), new(9, 200, 128))),
            new(11, edit => edit.CaptureImage(images.Add)),
            new(12, edit => edit.SetDebugViews()),
            new(13, edit => { edit.CaptureImage(images.Add); edit.CaptureLinearImage(linear.Add); }),
        ]);
        await SceneImages.RunAsync(backend, script, "--no-taa", "--no-vsm");
        Assert.Equal(4, images.Count);
        SceneImages.AssertEqual(images[0], images[3]);
        SceneImages.AssertNotEqual(images[0], images[1]);
        // The first normal view stays unchanged when only the second view changes mode.
        // These interior rectangles exclude labels and window decoration.
        SceneImages.AssertEqualRegion(images[1], images[2], 440, 80, 160, 70);
        Assert.False(Region(images[1], 440, 240, 160, 70).SequenceEqual(Region(images[2], 440, 240, 160, 70)));
        Assert.Equal(3, linear.Count);
        Assert.All(linear, image => { Assert.Equal(640, image.Frame.Width); Assert.Equal(480, image.Frame.Height); });
        Assert.Equal(linear[0].Pixels.ToArray(), linear[1].Pixels.ToArray());
        Assert.Equal(linear[0].Pixels.ToArray(), linear[2].Pixels.ToArray());
    }

    private static byte[] Region(SceneImage image, int x, int y, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            image.Pixels.Span.Slice(((y + row) * image.Width + x) * 4, width * 4)
                .CopyTo(pixels.AsSpan(row * width * 4));
        return pixels;
    }
}
