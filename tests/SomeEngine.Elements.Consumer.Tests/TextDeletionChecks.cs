using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class TextEditingChecks
{
    internal static void RunDeletion(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "A😀e\u0301中");
        frames.Frame();

        Keyboard.Key(frames.World, Key.Backspace, true);
        Keyboard.Key(frames.World, Key.Backspace, false);
        frames.Frame();
        CheckEdit(frames, root, "A😀e\u0301", 5, 2);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Delete, true);
        frames.Frame();
        CheckEdit(frames, root, "A😀", 3, 3);

        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Backspace, true);
        frames.Frame();
        CheckEdit(frames, root, "A😀", 0, 3);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Delete, true);
        Keyboard.Key(frames.World, Key.Delete, false);
        frames.Frame();
        CheckEdit(frames, root, "A", 1, 4);
        Keyboard.Key(frames.World, Key.End, true);
        Keyboard.Key(frames.World, Key.Delete, true);
        frames.Frame();
        CheckEdit(frames, root, "A", 1, 4);

        Keyboard.Text(frames.World, "BCD");
        frames.Frame();
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Backspace, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        frames.Frame();
        CheckEdit(frames, root, "AB", 2, 6);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Delete, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        frames.Frame();
        CheckEdit(frames, root, string.Empty, 0, 7);
    }

    private static void CheckEdit(FocusFrames frames, Entity root, string text, int caret, int consumedEdits)
    {
        EditDisplay display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == text && display.EditedText == text && display.Caret == caret &&
            display.SelectionStart == caret && display.ConsumedEdits == consumedEdits,
            $"The caller displays '{text}', caret {caret}, and {consumedEdits} consumed editing batches.");
    }
}
