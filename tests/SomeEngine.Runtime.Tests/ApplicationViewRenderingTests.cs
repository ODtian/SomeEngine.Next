using ImGuiNET;
using System.Numerics;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationViewRenderingTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task Captured_overlay_preserves_red_and_blue_channels(string backend)
    {
        SceneImage? captured = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--hardware-adapter", "--graphics-backend", backend,
                "--hidden-window", "--synchronous-frames", "--static-scene", "--frames", "3",
                "--no-debug-ui", "--no-vsync"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => captured = image);
            },
            DrawOverlay = () =>
            {
                ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
                draw.AddRectFilled(Vector2.Zero, new Vector2(16, 16), 0xff0000ff);
                draw.AddRectFilled(new Vector2(16, 0), new Vector2(32, 16), 0xffff0000);
            },
        };

        ApplicationResult result = await Application.RunAsync([], application: application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(3, result.CompletedFrames);
        Assert.NotNull(captured);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, captured.Pixels.Span.Slice((8 * captured.Width + 8) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, captured.Pixels.Span.Slice((8 * captured.Width + 24) * 4, 4).ToArray());
    }

    [Fact]
    public async Task Overlay_draws_separated_by_empty_frames_match_the_same_visible_items()
    {
        SceneImage? empty = null;
        SceneImage? overlay = null;
        int referenceFrame = 0;
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "4"]),
            UpdateFrame = frame =>
            {
                referenceFrame = frame.Index;
                if (frame.Index == 2) frame.Scene.CaptureImage(image => empty = image);
                if (frame.Index == 3) frame.Scene.CaptureImage(image => overlay = image);
            },
            DrawOverlay = () =>
            {
                if (referenceFrame != 3) return;
                ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
                draw.AddRectFilled(Vector2.Zero, new Vector2(16, 16), 0xff00ff00);
                draw.AddText(new Vector2(24, 24), 0xffffffff, "A");
            },
        };
        ApplicationResult referenceResult = await Application.RunAsync([], application: reference);
        Assert.True(referenceResult.Succeeded, referenceResult.Error?.ToString());

        var frames = new List<SceneImage>();
        int overlayFrame = 0;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = reference.Options with { FrameLimit = 8 },
            UpdateFrame = frame =>
            {
                overlayFrame = frame.Index;
                if (frame.Index > 0) frame.Scene.CaptureImage(frames.Add);
            },
            DrawOverlay = () =>
            {
                if (overlayFrame % 2 == 0) return;
                ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
                for (int rectangle = 0; rectangle < 3500; rectangle++)
                    draw.AddRectFilled(Vector2.Zero, new Vector2(16, 16), 0xff00ff00);
                draw.AddText(new Vector2(24, 24), 0xffffffff, "A");
            },
        };
        ApplicationResult result = await Application.RunAsync([], application: application);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.NotNull(empty);
        Assert.NotNull(overlay);
        Assert.Equal(7, frames.Count);
        foreach (SceneImage frame in frames)
            SceneImages.AssertEqual(frame.Frame % 2 == 0 ? empty : overlay, frame);
    }

    [Fact]
    public async Task Image_font_image_commands_render_like_the_same_nonoverlapping_items_in_font_first_order()
    {
        SceneImage? expected = null;
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => expected = image);
            },
        };
        ApplicationView referenceView = reference.AddView(new(8, 32, 32));
        reference.DrawOverlay = () =>
        {
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            draw.AddText(new Vector2(24, 24), 0xffffffff, "A");
            draw.AddImage(referenceView.ImageId, Vector2.Zero, new Vector2(16, 16));
            draw.AddImage(referenceView.ImageId, new Vector2(48, 48), new Vector2(64, 64));
            draw.AddImage(ImGui.GetIO().Fonts.TexID, new Vector2(80, 0), new Vector2(96, 16));
        };
        ApplicationResult referenceResult = await Application.RunAsync([], application: reference);
        Assert.True(referenceResult.Succeeded, referenceResult.Error?.ToString());

        SceneImage? actual = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = reference.Options,
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => actual = image);
            },
        };
        ApplicationView view = application.AddView(new(8, 32, 32));
        application.DrawOverlay = () =>
        {
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            draw.AddImage(view.ImageId, Vector2.Zero, new Vector2(16, 16));
            draw.AddText(new Vector2(24, 24), 0xffffffff, "A");
            draw.AddImage(view.ImageId, new Vector2(48, 48), new Vector2(64, 64));
            draw.AddImage(0, new Vector2(80, 0), new Vector2(96, 16));
        };
        ApplicationResult result = await Application.RunAsync([], application: application);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        SceneImages.AssertEqual(expected, actual);
    }

    [Fact]
    public async Task Removing_and_replacing_an_output_matches_registering_the_final_view_directly()
    {
        SceneImage? expected = null;
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--static-scene", "--no-debug-ui",
                "--no-vsync", "--no-taa", "--no-vsm", "--frames", "4"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 3) frame.Scene.CaptureImage(image => expected = image);
            },
        };
        ApplicationView referenceView = reference.AddView(new(9, 24, 24));
        reference.DrawOverlay = () => ImGui.GetBackgroundDrawList().AddImage(referenceView.ImageId,
            Vector2.Zero, new Vector2(32, 32));
        ApplicationResult referenceResult = await Application.RunAsync([], application: reference);
        Assert.True(referenceResult.Succeeded, referenceResult.Error?.ToString());

        SceneImage? actual = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = reference.Options,
        };
        ApplicationView view = application.AddView(new(8, 16, 16));
        application.UpdateFrame = frame =>
        {
            if (frame.Index == 1)
            {
                Assert.True(application.RemoveView(view));
                view = application.AddView(new(9, 24, 24));
            }
            if (frame.Index == 3) frame.Scene.CaptureImage(image => actual = image);
        };
        application.DrawOverlay = () => ImGui.GetBackgroundDrawList().AddImage(view.ImageId,
            Vector2.Zero, new Vector2(32, 32));
        ApplicationResult result = await Application.RunAsync([], application: application);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        SceneImages.AssertEqual(expected, actual);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task Resizing_a_registered_output_matches_a_view_created_at_its_final_dimensions(string backend)
    {
        SceneImage? expected = null;
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--static-scene", "--no-debug-ui",
                "--no-vsync", "--no-taa", "--no-vsm", "--frames", "4", "--graphics-backend", backend]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 3) frame.Scene.CaptureImage(image => expected = image);
            },
        };
        ApplicationView referenceView = reference.AddView(new(8, 32, 24));
        reference.DrawOverlay = () => ImGui.GetBackgroundDrawList().AddImage(referenceView.ImageId,
            Vector2.Zero, new Vector2(32, 24));
        ApplicationResult referenceResult = await Application.RunAsync([], application: reference);
        Assert.True(referenceResult.Succeeded, referenceResult.Error?.ToString());

        SceneImage? actual = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            RegisterSceneComponents = DefaultGame.SampleSceneSerialization.Register,
            Options = reference.Options,
        };
        ApplicationView view = application.AddView(new(8, 16, 16));
        application.UpdateFrame = frame =>
        {
            if (frame.Index == 1) view.Settings = new(8, 32, 24);
            if (frame.Index == 3) frame.Scene.CaptureImage(image => actual = image);
        };
        application.DrawOverlay = () => ImGui.GetBackgroundDrawList().AddImage(view.ImageId,
            Vector2.Zero, new Vector2(32, 24));
        ApplicationResult result = await Application.RunAsync([], application: application);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        SceneImages.AssertEqual(expected, actual);
    }
}
