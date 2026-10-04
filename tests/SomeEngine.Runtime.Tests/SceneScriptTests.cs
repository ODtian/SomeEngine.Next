using System.Numerics;

namespace SomeEngine.Runtime.Tests;

// #128: the WARP Cluster composition times out during first-frame completion; these scenarios use a hardware adapter.
[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class SceneScriptTests(SceneContentFixture content)
{
    [Fact]
    public async Task ImagesArriveAtRequestedFramesWithPackedPixelsThatSurviveShutdown()
    {
        var images = new List<SceneImage>();
        var script = new SceneScript(content.DirectoryPath, 5,
        [
            new(4, edit => edit.CaptureImage(images.Add)),
            new(2, edit => edit.CaptureImage(images.Add)),
        ]);
        ApplicationResult result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(5, result.CompletedFrames);
        Assert.Equal([2, 4], images.Select(image => image.Frame));
        Assert.All(images, image =>
        {
            Assert.Equal(64, image.Width);
            Assert.Equal(64, image.Height);
            Assert.Equal(64 * 64 * 4, image.Pixels.Length);
        });
        Assert.Equal(images[0].Pixels.ToArray(), images[1].Pixels.ToArray());
        // Entire interior regions are colored by the declared spheres, independently of background coverage.
        for (int y = 29; y <= 34; y++)
        for (int x = 37; x <= 42; x++)
        {
            int pixel = (y * 64 + x) * 4;
            Assert.True(images[0].Pixels.Span[pixel] > images[0].Pixels.Span[pixel + 1] + 100);
        }
    }

    [Fact]
    public async Task CameraTransformAtFrameThreeMatchesStartingWithThatTransform()
    {
        var images = new List<SceneImage>();
        var script = new SceneScript(content.DirectoryPath, 5,
        [
            new(2, edit => edit.CaptureImage(images.Add)),
            new(3, edit =>
            {
                edit.SetCameraTransform(new(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f));
                edit.CaptureImage(images.Add);
            }),
        ]);
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        SceneImage? expected = null;
        var direct = new SceneScript(content.DirectoryPath, 5,
        [
            new(0, edit => edit.SetCameraTransform(new(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f))),
            new(3, edit => edit.CaptureImage(image => expected = image)),
        ]);
        var reference = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], direct);
        Assert.True(reference.Succeeded, reference.Error?.ToString());
        Assert.False(images[0].Pixels.Span.SequenceEqual(images[1].Pixels.Span));
        Assert.Equal(expected!.Pixels.ToArray(), images[1].Pixels.ToArray());
    }

    [Fact]
    public async Task ActionFailuresReportTheirCategoryAndCompletedFrames()
    {
        var script = new SceneScript(content.DirectoryPath, 4,
            [new(2, _ => throw new InvalidOperationException())]);
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.Equal(ApplicationFailure.SceneAction, result.Failure);
        Assert.Equal(2, result.CompletedFrames);
    }

    [Fact]
    public async Task ImageCallbackFailuresReportTheirCategoryAndCompletedFrames()
    {
        var script = new SceneScript(content.DirectoryPath, 4,
            [new(2, edit => edit.CaptureImage(_ => throw new InvalidOperationException()))]);
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.Equal(ApplicationFailure.ImageCallback, result.Failure);
        Assert.Equal(3, result.CompletedFrames);
    }

    [Fact]
    public async Task ImagesAtUnalignedWidthsContainPackedRows()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Image-{Guid.NewGuid():N}");
        try
        {
            foreach (string file in Directory.EnumerateFiles(content.DirectoryPath, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(directory, Path.GetRelativePath(content.DirectoryPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            await SomeEngine.SceneContent.SceneContent.SetOutputSizeAsync(directory, 80, 48);
            SceneImage? image = null;
            var script = new SceneScript(directory, 4, [new(3, edit => edit.CaptureImage(value => image = value))]);
            var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
            Assert.True(result.Succeeded, result.Error?.ToString());
            Assert.Equal(80, image!.Width);
            Assert.Equal(48, image.Height);
            Assert.Equal(80 * 48 * 4, image.Pixels.Length);
            for (int alpha = 3; alpha < image.Pixels.Length; alpha += 4)
                Assert.Equal((byte)255, image.Pixels.Span[alpha]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact(Skip = "#129: window resize fails swapchain reconfiguration; Cluster output also retains startup dimensions.")]
    public async Task OutputSizeChangeMatchesStartingAtTheNewSize()
    {
        SceneImage? resized = null;
        var script = new SceneScript(content.DirectoryPath, 5,
        [
            new(3, edit => edit.SetOutputSize(80, 48)),
            new(4, edit => edit.CaptureImage(image => resized = image)),
        ]);
        var result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
        Assert.True(result.Succeeded, result.Error?.ToString());
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Resize-{Guid.NewGuid():N}");
        try
        {
            foreach (string file in Directory.EnumerateFiles(content.DirectoryPath, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(directory, Path.GetRelativePath(content.DirectoryPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            await SomeEngine.SceneContent.SceneContent.SetOutputSizeAsync(directory, 80, 48);
            SceneImage? expected = null;
            var direct = new SceneScript(directory, 5, [new(4, edit => edit.CaptureImage(image => expected = image))]);
            var reference = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], direct);
            Assert.True(reference.Succeeded, reference.Error?.ToString());
            Assert.Equal(80, resized!.Width);
            Assert.Equal(48, resized.Height);
            Assert.Equal(expected!.Pixels.ToArray(), resized.Pixels.ToArray());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}

[Trait("Category", "Software")]
public sealed class SceneScriptFailureTests
{
    [Fact]
    public async Task MissingContentReportsContentLoadFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Missing-{Guid.NewGuid():N}");
        var result = await Application.RunAsync([], new SceneScript(directory, 1, []));
        Assert.Equal(ApplicationFailure.ContentLoad, result.Failure);
        Assert.Equal(0, result.CompletedFrames);
    }

    [Fact]
    public async Task InvalidStartupArgumentsReportStartupFailure()
    {
        var result = await Application.RunAsync(["--graphics-backend", "unknown"], new SceneScript(Path.GetTempPath(), 1, []));
        Assert.Equal(ApplicationFailure.Startup, result.Failure);
        Assert.Equal(0, result.CompletedFrames);
    }

    [Fact]
    public void ActionsRequireFramesWithinTheScript()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneScript(Path.GetTempPath(), 3, [new(3, _ => { })]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneScript(Path.GetTempPath(), 3, [new(-1, _ => { })]));
    }
}
