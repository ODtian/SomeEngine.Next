using System.Numerics;
using SomeEngine.Render.Components;

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
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => captured = image);
            },
        };

        ApplicationResult result = await Application.RunAsync([], application: scene);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.NotNull(captured);
        Assert.Equal(2, captured.Frame);
        Assert.Equal(64 * 64 * 4, captured.Pixels.Length);
    }

    [Fact]
    public async Task DefaultVirtualShadowsRenderAndCaptureCompleteFrames()
    {
        var completed = new List<int>();
        var captures = new List<SceneImage>();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--frames", "3"]),
            Initialize = frame =>
            {
                frame.World.CreateEntity(new DirectionalLight(
                    Vector3.Normalize(new Vector3(-1, -2, 1)), Vector3.One, 3));
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => captures.Add(image));
            },
            FrameCompleted = frame => completed.Add(frame.Index),
        };
        Assert.True(scene.Options.VirtualShadows);

        ApplicationResult result = await Application.RunAsync([], application: scene);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([0, 1, 2], completed);
        SceneImage captured = Assert.Single(captures);
        Assert.Equal(2, captured.Frame);
        Assert.Equal(64, captured.Width);
        Assert.Equal(64, captured.Height);
        Assert.Equal(64 * 64 * 4, captured.Pixels.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReloadRestartsFramesWithUpdatedSettings(bool synchronousFrames)
    {
        var frames = new List<int>();
        string[] args = ["--hidden-window", "--static-scene", "--no-debug-ui", "--no-vsync",
            "--no-taa", "--no-vsm", "--frames", "3"];
        if (synchronousFrames) args = [.. args, "--synchronous-frames"];
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(args),
            UpdateFrame = frame => frames.Add(frame.Index),
        };
        scene.FrameCompleted = frame =>
        {
            if (frame.Index != 1) return;
            scene.Options = scene.Options with { FrameLimit = 1 };
            scene.Reload();
        };

        ApplicationResult result = await Application.RunAsync([], application: scene);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([0, 1, 0], frames);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ReloadPreservesRenderedMaterialPixels(string backend)
    {
        var captures = new List<SceneImage>();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--graphics-backend", backend, "--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(captures.Add);
            },
        };
        scene.AddView(new(8, 32, 32));
        bool reloaded = false;
        scene.FrameCompleted = frame =>
        {
            if (frame.Index != 2 || reloaded) return;
            reloaded = true;
            scene.Reload();
        };

        ApplicationResult result = await Application.RunAsync([], application: scene);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(2, captures.Count);
        SceneImages.AssertEqual(captures[0], captures[1]);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task ExitFinishesThePresentedFrameAndReleasesTheScene(string backend)
    {
        var frames = new List<int>();
        var completion = new List<string>();
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--graphics-backend", backend, "--hidden-window",
                "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            Shutdown = _ => completion.Add("shutdown"),
            SceneCompleted = _ => completion.Add("completed"),
        };
        scene.FrameCompleted = frame =>
        {
            frames.Add(frame.Index);
            if (frame.Index == 1)
            {
                scene.Reload();
                scene.Exit();
            }
        };

        ApplicationResult result = await Application.RunAsync([], application: scene);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(2, result.CompletedFrames);
        Assert.Equal([0, 1], frames);
        Assert.Equal(["shutdown", "completed"], completion);
    }
}
