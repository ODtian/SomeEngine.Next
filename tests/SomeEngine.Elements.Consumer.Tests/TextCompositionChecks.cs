using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class TextEditingChecks
{
    internal static void RunCompositionKeys(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "原文");
        Keyboard.Composition(frames.World, "候选", 1);
        frames.Frame();
        foreach (Key key in new[] { Key.Left, Key.Right, Key.Home, Key.End, Key.Backspace, Key.Delete, Key.Enter, Key.Space })
            Keyboard.Key(frames.World, key, true);
        frames.Frame();
        EditDisplay display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "原文" && display.Caret == 2 && display.SelectionStart == 2 &&
            display.Composition == "候选" && display.CompositionCaret == 1 && display.ConsumedEdits == 1,
            "The input method's editing keys preserve both committed text and candidate presentation.");
        Require(frames.World.Read<Clicks>(display.Node).Count == 0,
            "The editor keeps Enter and Space for text input while holding a composition.");
        Keyboard.Composition(frames.World, string.Empty, 0);
        Keyboard.Key(frames.World, Key.Backspace, true);
        Keyboard.Key(frames.World, Key.Enter, true);
        Keyboard.Key(frames.World, Key.Space, true);
        Keyboard.Text(frames.World, " ");
        frames.Frame();
        CheckEdit(frames, root, "原 ", 2, 2);
        Require(frames.World.Read<Clicks>(display.Node).Count == 0,
            "After composition ends, ordinary editing resumes and keyboard text keys stay with the editor.");
    }

    internal static void RunComposition(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "前后");
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        frames.Frame();

        Keyboard.Composition(frames.World, "hao", 2);
        frames.Frame();
        EditDisplay display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "前后" && display.Composition == "hao" && display.CompositionCaret == 2 &&
            display.PresentedText == "前hao" && display.Caret == 2 && display.SelectionStart == 1 &&
            display.ConsumedEdits == 1,
            "Preedit displays its own caret and replaces the selected presentation while retaining committed text.");
        Keyboard.Composition(frames.World, "好", 1);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "前后" && display.PresentedText == "前好" && display.ConsumedEdits == 1,
            "A new candidate updates the caller's presentation while preserving committed editing batches.");
        Keyboard.Text(frames.World, "好");
        frames.Frame();
        CheckEdit(frames, root, "前好", 2, 2);
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Composition.Length == 0 && display.CompositionCaret == 0,
            "Confirmed text clears the composition and its caret.");

        Keyboard.Composition(frames.World, "hou", 3);
        frames.Frame();
        Keyboard.Composition(frames.World, string.Empty, 0);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "前好" && display.PresentedText == "前好" && display.Composition.Length == 0 &&
            display.ConsumedEdits == 2,
            "Ending a composition restores the committed presentation and editing batches.");
    }
}
