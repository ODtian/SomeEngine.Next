using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Elements;
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
        Assert.True(input.Capture.Pointer);
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
        Assert.True(input.Capture.Keyboard);
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
        Assert.False(input.Capture.Pointer);
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
