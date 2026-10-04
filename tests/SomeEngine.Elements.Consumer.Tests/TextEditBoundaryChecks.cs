using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class TextEditingChecks
{
    internal static void RunEmptyInput(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "ab");
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        frames.Frame();
        Keyboard.Text(frames.World, string.Empty);
        frames.Frame();
        EditDisplay display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "ab" && display.Caret == 1 && display.SelectionStart == 2 &&
            display.ConsumedEdits == 1,
            "Empty character input preserves committed text and its selection.");
        Keyboard.Composition(frames.World, "候", 1);
        Keyboard.Text(frames.World, string.Empty);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "ab" && display.Composition == "候" && display.CompositionCaret == 1 &&
            display.ConsumedEdits == 1,
            "Empty character input preserves candidate presentation until the platform ends composition.");
        Keyboard.Text(frames.World, "b");
        frames.Frame();
        CheckEdit(frames, root, "ab", 2, 1);
        Require(frames.World.Read<EditDisplay>(root).Composition.Length == 0,
            "Confirming the selected text consumes the composition and retains the committed edit display.");
    }

    internal static void RunJoinedCharacters(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "👩👧");
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Text(frames.World, "\u200d");
        frames.Frame();
        CheckEdit(frames, root, "👩‍👧", 5, 1);
        Keyboard.Key(frames.World, Key.Backspace, true);
        frames.Frame();
        CheckEdit(frames, root, string.Empty, 0, 2);
    }
}
