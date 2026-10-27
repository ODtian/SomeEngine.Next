namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class RuntimeSwapchainResizeTests(SceneContentFixture content, Xunit.Abstractions.ITestOutputHelper output)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task Repeated_asynchronous_resize_keeps_all_scene_frames_and_presents_new_extents(string backend)
    {
        var actions = new List<int>();
        var images = new List<SceneImage>();
        var script = new SceneScript(new ApplicationScene(content.DirectoryPath)
            { RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register }, 6,
        [
            new(0, _ => actions.Add(0)),
            new(1, _ => actions.Add(1)),
            new(2, edit =>
            {
                actions.Add(2);
                edit.SetOutputSize(80, 48);
            }),
            new(3, edit =>
            {
                actions.Add(3);
                edit.CaptureImage(images.Add);
            }),
            new(4, edit =>
            {
                actions.Add(4);
                edit.SetOutputSize(96, 80);
            }),
            new(5, edit =>
            {
                actions.Add(5);
                edit.CaptureImage(images.Add);
            }),
        ]) { WaitForFrames = false };

        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", backend, "--no-taa", "--no-vsm"], script);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(6, result.CompletedFrames);
        Assert.Equal([0, 1, 2, 3, 4, 5], actions);
        Assert.Collection(images,
            image => AssertFrame(image, 3, 80, 48),
            image => AssertFrame(image, 5, 96, 80));
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task Initialization_resize_is_applied_before_the_first_frame_and_presented(string backend)
    {
        string[] args = ["--hardware-adapter", "--graphics-backend", backend, "--frames", "1",
            "--hidden-window", "--synchronous-frames", "--static-scene", "--no-debug-ui",
            "--no-vsync", "--no-taa", "--no-vsm"];
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            // The application overload uses these options rather than parsing args again.
            Options = RuntimeStartupOptions.Parse(args),
        };
        var events = new List<string>();
        SceneImage? captured = null;
        int initialWidth = 0, initialHeight = 0, requestedWidth = 0, requestedHeight = 0;
        application.Initialize = frame =>
        {
            events.Add("initialize");
            initialWidth = frame.Width;
            initialHeight = frame.Height;
            Assert.True(initialWidth > 0 && initialHeight > 0);
            (requestedWidth, requestedHeight) = (initialWidth, initialHeight) == (80, 48)
                ? (96, 80) : (80, 48);
            // The native swapchain already exists at the content's boot extent. Change
            // only the window, forcing the normal first-loop resize/admission path.
            frame.Scene.SetOutputSize(requestedWidth, requestedHeight);
        };
        application.UpdateFrame = frame =>
        {
            events.Add("update");
            output.WriteLine($"Initialization resize: frame={frame.Index}; " +
                $"boot={initialWidth}x{initialHeight}; requested={requestedWidth}x{requestedHeight}; " +
                $"actual={frame.Width}x{frame.Height}");
            Assert.Equal(0, frame.Index);
            Assert.Equal(requestedWidth, frame.Width);
            Assert.Equal(requestedHeight, frame.Height);
            // Initialization capture requests are cleared when the frame action begins.
            // Request the actual resized frame's readback at the public update boundary.
            frame.Scene.CaptureImage(image =>
            {
                events.Add("capture");
                Assert.Null(captured);
                captured = image;
            });
        };
        application.FrameCompleted = frame =>
        {
            events.Add("completed");
            Assert.Equal(0, frame.Index);
            Assert.Equal(0, frame.Timing.SimulationFrame);
            Assert.Equal(requestedWidth, frame.Width);
            Assert.Equal(requestedHeight, frame.Height);
        };
        application.Shutdown = _ => events.Add("shutdown");
        application.SceneCompleted = _ => events.Add("scene completed");

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, $"{result.Failure}; frames={result.CompletedFrames}; {result.Error}");
        Assert.Equal(1, result.CompletedFrames);
        Assert.NotEqual((initialWidth, initialHeight), (requestedWidth, requestedHeight));
        Assert.Equal(["initialize", "update", "capture", "completed", "shutdown", "scene completed"], events);
        Assert.NotNull(captured);
        AssertFrame(captured, 0, requestedWidth, requestedHeight);
    }

    [SceneTheory]
    [InlineData("d3d12", 80, 48)]
    [InlineData("d3d12", 320, 240)]
    public async Task Programmatic_resize_reports_exact_client_extent_before_rendering(string backend, int requestedWidth, int requestedHeight)
    {
        string[] args = ["--hardware-adapter", "--graphics-backend", backend, "--frames", "1",
            "--hidden-window", "--synchronous-frames", "--static-scene", "--no-debug-ui",
            "--no-vsync", "--no-taa", "--no-vsm"];
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(args),
        };
        var stopBeforeRendering = new InvalidOperationException("Window-size boundary observed.");
        int initialWidth = 0, initialHeight = 0, observedWidth = 0, observedHeight = 0;
        int observedFrame = -1, updateCalls = 0, completedCallbacks = 0;
        application.Initialize = frame =>
        {
            initialWidth = frame.Width;
            initialHeight = frame.Height;
            frame.Scene.SetOutputSize(requestedWidth, requestedHeight);
        };
        application.UpdateFrame = frame =>
        {
            updateCalls++;
            observedFrame = frame.Index;
            observedWidth = frame.Width;
            observedHeight = frame.Height;
            output.WriteLine($"Programmatic resize boundary: frame={observedFrame}; " +
                $"boot={initialWidth}x{initialHeight}; requested={requestedWidth}x{requestedHeight}; " +
                $"actual={observedWidth}x{observedHeight}");
            // This case tests window sizing only. Stop with our own exception before
            // renderer reconciliation; the separate full-frame smoke remains required.
            throw stopBeforeRendering;
        };
        application.FrameCompleted = _ => completedCallbacks++;

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.False(result.Succeeded);
        Assert.Equal(ApplicationFailure.SceneAction, result.Failure);
        Assert.Same(stopBeforeRendering, result.Error?.GetBaseException());
        Assert.Equal(0, result.CompletedFrames);
        Assert.Equal(1, updateCalls);
        Assert.Equal(0, observedFrame);
        Assert.Equal(0, completedCallbacks);
        Assert.True(initialWidth > 0 && initialHeight > 0);
        Assert.NotEqual((requestedWidth, requestedHeight), (initialWidth, initialHeight));
        Assert.Equal((requestedWidth, requestedHeight), (observedWidth, observedHeight));
    }

    private static void AssertFrame(SceneImage image, int frame, int width, int height)
    {
        Assert.Equal(frame, image.Frame);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.Equal(width * height * 4, image.Pixels.Length);
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        bool visibleVariation = false;
        for (int pixel = 0; pixel < pixels.Length; pixel += 4)
        {
            Assert.Equal((byte)255, pixels[pixel + 3]);
            visibleVariation |= pixels[pixel] != pixels[0] ||
                pixels[pixel + 1] != pixels[1] || pixels[pixel + 2] != pixels[2];
        }
        Assert.True(visibleVariation, "The resized frame must contain scene pixels, not only a clear color.");
    }
}
