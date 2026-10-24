namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationSceneTests(SceneContentFixture content)
{
    [Fact]
    public async Task FrameActionsCaptureSelectedFrames()
    {
        SceneImage? captured = null;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => captured = image);
            },
        };

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.NotNull(captured);
        Assert.Equal(2, captured.Frame);
        Assert.Equal(64 * 64 * 4, captured.Pixels.Length);
    }

    [Fact]
    public async Task ReloadRestartsFramesWithUpdatedSettings()
    {
        var frames = new List<int>();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame => frames.Add(frame.Index),
        };
        scene.FrameCompleted = frame =>
        {
            if (frame.Index != 1) return;
            scene.Options = scene.Options with { FrameLimit = 1 };
            scene.Reload();
        };

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.Equal([0, 1, 0], frames);
    }
}
