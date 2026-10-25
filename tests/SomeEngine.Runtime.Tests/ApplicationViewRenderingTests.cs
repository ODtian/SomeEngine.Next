using ImGuiNET;
using System.Numerics;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ApplicationViewRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task Image_font_image_commands_render_like_the_same_nonoverlapping_items_in_font_first_order()
    {
        SceneImage? expected = null;
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "3"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => expected = image);
            },
        };
        ApplicationView referenceView = reference.AddView(new(9, 32, 32));
        reference.DrawOverlay = () =>
        {
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            draw.AddText(new Vector2(24, 24), 0xffffffff, "A");
            draw.AddImage(referenceView.ImageId, Vector2.Zero, new Vector2(16, 16));
            draw.AddImage(referenceView.ImageId, new Vector2(48, 48), new Vector2(64, 64));
            draw.AddImage(ImGui.GetIO().Fonts.TexID, new Vector2(80, 0), new Vector2(96, 16));
        };
        Assert.Equal(0, await Application.RunAsync([], application: reference));

        SceneImage? actual = null;
        var application = new ApplicationScene(content.DirectoryPath)
        {
            Options = reference.Options,
            UpdateFrame = frame =>
            {
                if (frame.Index == 2) frame.Scene.CaptureImage(image => actual = image);
            },
        };
        ApplicationView view = application.AddView(new(9, 32, 32));
        application.DrawOverlay = () =>
        {
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            draw.AddImage(view.ImageId, Vector2.Zero, new Vector2(16, 16));
            draw.AddText(new Vector2(24, 24), 0xffffffff, "A");
            draw.AddImage(view.ImageId, new Vector2(48, 48), new Vector2(64, 64));
            draw.AddImage(0, new Vector2(80, 0), new Vector2(96, 16));
        };
        Assert.Equal(0, await Application.RunAsync([], application: application));
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
        Assert.Equal(0, await Application.RunAsync([], application: reference));

        SceneImage? actual = null;
        var application = new ApplicationScene(content.DirectoryPath) { Options = reference.Options };
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
        Assert.Equal(0, await Application.RunAsync([], application: application));
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        SceneImages.AssertEqual(expected, actual);
    }

    [Fact]
    public async Task Resizing_a_registered_output_matches_a_view_created_at_its_final_dimensions()
    {
        SceneImage? expected = null;
        var reference = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--static-scene", "--no-debug-ui",
                "--no-vsync", "--no-taa", "--no-vsm", "--frames", "4"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 3) frame.Scene.CaptureImage(image => expected = image);
            },
        };
        ApplicationView referenceView = reference.AddView(new(10, 32, 24));
        reference.DrawOverlay = () => ImGui.GetBackgroundDrawList().AddImage(referenceView.ImageId,
            Vector2.Zero, new Vector2(32, 24));
        Assert.Equal(0, await Application.RunAsync([], application: reference));

        SceneImage? actual = null;
        var application = new ApplicationScene(content.DirectoryPath) { Options = reference.Options };
        ApplicationView view = application.AddView(new(10, 16, 16));
        application.UpdateFrame = frame =>
        {
            if (frame.Index == 1) view.Settings = new(10, 32, 24);
            if (frame.Index == 3) frame.Scene.CaptureImage(image => actual = image);
        };
        application.DrawOverlay = () => ImGui.GetBackgroundDrawList().AddImage(view.ImageId,
            Vector2.Zero, new Vector2(32, 24));
        Assert.Equal(0, await Application.RunAsync([], application: application));
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        SceneImages.AssertEqual(expected, actual);
    }
}
