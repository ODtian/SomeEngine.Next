using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class ValueChecks
{
    internal static void RunDragOrder(bool knob)
    {
        using var frames = new ValueFrames();
        Entity settings = frames.World.CreateEntity(new AudioSettings { Volume = 0.25f });
        Views.Views.Spawn(frames.World, frames.Canvas, new ValueExample(settings, 0, 1, 0, knob));
        frames.Frame();
        Vector2 start = knob ? new Vector2(24, 24) : new Vector2(43, 12);
        Vector2 direction = knob ? -Vector2.UnitY : Vector2.UnitX;
        for (int gesture = 0; gesture < 2; gesture++)
        {
            Pointer.Move(frames.World, frames.Canvas, start);
            Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
            Pointer.Move(frames.World, frames.Canvas, start + 37 * direction);
            Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        }
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.75f),
            "Two complete drags in one input frame each edit the preceding value.");
    }

    internal static void RunLimits(bool knob)
    {
        using var frames = new ValueFrames();
        Entity settings = frames.World.CreateEntity(new AudioSettings { Volume = 0.9f });
        Views.Views.Spawn(frames.World, frames.Canvas, new ValueExample(settings, 0, 1, 0.3f, knob));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 1),
            "A step crossing the upper limit reaches that endpoint when it lies between step positions.");
        uint version = frames.World.WriteVersion<AudioSettings>(settings);
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        Require(frames.World.Read<AudioSettings>(settings).Volume == 1 &&
            frames.World.WriteVersion<AudioSettings>(settings) == version,
            "An outward step at the endpoint retains the value and its public write version.");
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.6f),
            "Opposing keys at an endpoint apply in arrival order before quantization.");
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();
        Require(frames.World.Read<AudioSettings>(settings).Volume == 0,
            "Repeated negative steps reach and retain the lower limit.");
    }

    internal static void RunContinuous(bool knob)
    {
        using var frames = new ValueFrames();
        Entity settings = frames.World.CreateEntity(new AudioSettings { Volume = 0.25f });
        Views.Views.Spawn(frames.World, frames.Canvas, new ValueExample(settings, 0, 1, 0, knob));
        frames.Frame();
        Vector2 start = knob ? new Vector2(24, 24) : new Vector2(43, 12);
        Vector2 direction = knob ? -Vector2.UnitY : Vector2.UnitX;
        Pointer.Move(frames.World, frames.Canvas, start);
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, start + 18.5f * direction);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.375f),
            "Step zero preserves a continuous drag value.");
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Keyboard.Key(frames.World, Key.Up, true);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.425f),
            "Step zero uses five percent of the range for a keyboard step.");
    }

    internal static void RunEqualRange(bool knob)
    {
        using var frames = new ValueFrames();
        Entity settings = frames.World.CreateEntity(new AudioSettings { Volume = 0.7f });
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ValueExample(settings, 0.7f, 0.7f, 0, knob));
        frames.Frame();
        Entity control = frames.World.Read<ValueDisplay>(root).Control;
        uint version = frames.World.WriteVersion<AudioSettings>(settings);
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Key(frames.World, Key.Up, true);
        Vector2 start = knob ? new Vector2(24, 24) : new Vector2(6, 12);
        Vector2 direction = knob ? -Vector2.UnitY : Vector2.UnitX;
        Pointer.Move(frames.World, frames.Canvas, start);
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, start + 100 * direction);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(frames.World.Read<AudioSettings>(settings).Volume == 0.7f &&
            frames.World.WriteVersion<AudioSettings>(settings) == version &&
            frames.World.Read<ValueRange>(control).Ratio == 0,
            "An equal-limit range retains its single value and reports a zero presentation ratio.");
    }

    internal static void RunHalfStep(bool knob)
    {
        using var frames = new ValueFrames();
        Entity settings = frames.World.CreateEntity(new AudioSettings { Volume = 0 });
        Views.Views.Spawn(frames.World, frames.Canvas, new ValueExample(settings, 0, 1, 0.25f, knob));
        frames.Frame();
        Vector2 start = knob ? new Vector2(24, 24) : new Vector2(6, 12);
        Vector2 direction = knob ? -Vector2.UnitY : Vector2.UnitX;
        Pointer.Move(frames.World, frames.Canvas, start);
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, start + 18.5f * direction);
        frames.Frame();
        Require(Near(frames.World.Read<AudioSettings>(settings).Volume, 0.25f),
            "A candidate exactly halfway between steps rounds toward the increasing value.");
    }
}
