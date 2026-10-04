using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Job;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Runtime.Tests;

public sealed class WindowInputTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Window_pixels_click_a_scaled_screen_button_before_game_input(int button)
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600), Scale = 2 });
        Entity node = CreateButton(world, canvas);
        using var frames = new GameFrameSystems(world);
        using var input = new WindowInput(world);

        input.ChangePointerButton(button, true, new Vector2(40, 60));
        input.ChangePointerButton(button, false, new Vector2(40, 60));
        frames.UpdateInput();

        Assert.Equal(1u, world.Read<Clicks>(node).Count);
        Assert.True(world.Read<Hover>(node).Hovered);
        Assert.True(input.InputCapture.Pointer);
    }

    [Fact]
    public void Navigation_and_repeated_confirm_keys_reach_the_focused_button()
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity node = CreateButton(world, canvas);
        world.Add(node, new Focusable());
        using var frames = new GameFrameSystems(world);
        using var input = new WindowInput(world);

        input.ChangeKey(0x28, true);
        input.ChangeKey(0x28, false);
        input.ChangeKey(0x0D, true);
        input.ChangeKey(0x0D, true);
        input.ChangeKey(0x0D, false);
        input.ChangeKey(0x20, true);
        input.ChangeKey(0x20, false);
        frames.UpdateInput();

        Assert.True(world.HasSparse<Focused>(node));
        Assert.Equal(3u, world.Read<Clicks>(node).Count);
        Assert.True(input.InputCapture.Keyboard);
    }

    [Fact]
    public void Losing_window_focus_releases_a_held_button_and_returns_pointer_input_to_the_game()
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity node = CreateButton(world, canvas);
        using var frames = new GameFrameSystems(world);
        using var input = new WindowInput(world);
        input.ChangePointerButton(0, true, new Vector2(20, 30));
        frames.UpdateInput();
        Assert.True(world.Read<Hover>(node).Pressed);

        input.ReleaseInput();
        frames.UpdateInput();
        frames.UpdateInput();

        Assert.False(world.Read<Hover>(node).Pressed);
        Assert.False(world.Read<Hover>(node).Hovered);
        Assert.Equal(0u, world.Read<Clicks>(node).Count);
        Assert.False(input.InputCapture.Pointer);
    }

    [Fact]
    public void Wheel_input_uses_canvas_units_and_screen_buttons_are_separate_from_world_canvases()
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600), Scale = 2 });
        Entity node = CreateButton(world, canvas);
        world.Add(node, new ScrollInput());
        world.Add(node, new ScrollOffset { Max = new Vector2(100, 100) });
        Entity worldCanvas = world.CreateEntity(new Canvas
        {
            Size = new Size(800, 600), Target = CanvasTarget.WorldSpace(canvas),
        });
        Entity worldButton = CreateButton(world, worldCanvas);
        using var frames = new GameFrameSystems(world);
        using var input = new WindowInput(world);

        input.ScrollPointer(new Vector2(40, 60), new Vector2(2, 4));
        input.ChangePointerButton(0, true, new Vector2(40, 60));
        input.ChangePointerButton(0, false, new Vector2(40, 60));
        frames.UpdateInput();

        Assert.Equal(new Vector2(1, 2), world.Read<ScrollInput>(node).Delta);
        Assert.Equal(1u, world.Read<Clicks>(node).Count);
        Assert.Equal(0u, world.Read<Clicks>(worldButton).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Window_text_selection_deletion_and_composition_reach_the_focused_editor(bool jobs)
    {
        using var world = new World(execution: jobs ? new WorldJobs(JobSystem.Default) : null);
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity editor = CreateButton(world, canvas);
        world.Add(editor, new Focusable());
        world.Add(editor, new EditableText { Text = "", Composition = "" });
        using var frames = new GameFrameSystems(world);
        using var input = new WindowInput(world);
        input.ChangeKey(0x28, true);
        input.EnterText("AB");
        input.ChangeKey(0x10, true);
        input.ChangeKey(0x25, true);
        input.ChangeKey(0x10, false);
        input.EnterText("中");
        input.ChangeKey(0x23, true);
        input.ChangeKey(0x08, true);
        frames.UpdateInput();
        Assert.Equal("A", world.Read<EditableText>(editor).Text);
        Assert.Equal(1, world.Read<EditableText>(editor).Caret);

        input.ChangeComposition("你好", 1);
        frames.UpdateInput();
        Assert.Equal("A", world.Read<EditableText>(editor).Text);
        Assert.Equal("你好", world.Read<EditableText>(editor).Composition);
        Assert.Equal(1, world.Read<EditableText>(editor).CompositionCaret);
        input.EnterText("你好");
        input.ChangeComposition("", 0);
        frames.UpdateInput();
        Assert.Equal("A你好", world.Read<EditableText>(editor).Text);
        Assert.Equal("", world.Read<EditableText>(editor).Composition);
        Assert.True(input.InputCapture.Keyboard);
    }

    [Fact]
    public void Window_utf16_characters_form_complete_text_and_control_characters_preserve_it()
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity editor = CreateButton(world, canvas);
        world.Add(editor, new Focusable());
        world.Add(editor, new EditableText { Text = "", Composition = "" });
        using var frames = new GameFrameSystems(world);
        using var input = new WindowInput(world);
        input.ChangeKey(0x28, true);
        input.EnterCharacter('\uD83D');
        frames.UpdateInput();
        Assert.Equal("", world.Read<EditableText>(editor).Text);
        input.EnterCharacter('\uDE00');
        input.EnterCharacter('\b');
        input.EnterCharacter('\r');
        frames.UpdateInput();
        Assert.Equal("😀", world.Read<EditableText>(editor).Text);
        Assert.Equal(2, world.Read<EditableText>(editor).Caret);
    }

    private static Entity CreateButton(World world, Entity canvas)
    {
        Entity node = world.CreateEntity(new LayoutRect { Value = new Rect(10, 20, 100, 50) });
        world.Add(node, new Pressable());
        world.Add(node, new Hover());
        world.Add(node, new Clicks());
        Hierarchy<ElementTree>.SetParent(world, node, canvas);
        return node;
    }
}
