using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class TextEditingChecks
{
    internal static void RunInactive(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Keyboard.Text(frames.World, "early");
        Keyboard.Composition(frames.World, "候", 1);
        Keyboard.Key(frames.World, Key.Backspace, true);
        Keyboard.Key(frames.World, Key.Delete, true);
        frames.Frame();
        CheckEdit(frames, root, string.Empty, 0, 0);
        Require(frames.World.Read<EditDisplay>(root).Composition.Length == 0,
            "Inactive text input preserves the editor's empty candidate presentation.");
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "kept");
        frames.Frame();
        CheckEdit(frames, root, "kept", 4, 1);

        Entity node = frames.World.Read<EditDisplay>(root).Node;
        frames.World.RemoveSparse<Focused>(node);
        Keyboard.Text(frames.World, "lost");
        Keyboard.Composition(frames.World, "选", 1);
        Keyboard.Key(frames.World, Key.Backspace, true);
        frames.Frame();
        CheckEdit(frames, root, "kept", 4, 1);
        Require(frames.World.Read<EditDisplay>(root).Composition.Length == 0,
            "A released editor retains its committed text and empty candidate presentation.");
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "+");
        frames.Frame();
        CheckEdit(frames, root, "kept+", 5, 2);
    }

    internal static void RunRepeatedDeclaration(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "原文");
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Composition(frames.World, "候", 1);
        frames.Frame();
        EditDisplay previous = frames.World.Read<EditDisplay>(root);
        frames.World.Replace(root, new EditingExample(rewritten, Revision: 1));
        frames.Frame();
        Require(frames.World.Read<EditDisplay>(root) == previous,
            "Declaring the behavior again preserves its text, selection, composition, and consumed editing display.");
    }

    internal static void RunWorlds(bool rewritten = false)
    {
        using var first = new FocusFrames();
        using var second = new FocusFrames();
        Entity firstRoot = Views.Views.Spawn(first.World, first.Canvas, new EditingExample(rewritten));
        Entity secondRoot = Views.Views.Spawn(second.World, second.Canvas, new EditingExample(rewritten));
        first.Frame();
        second.Frame();
        Gamepad.Navigate(first.World, Direction.Right);
        Keyboard.Text(first.World, "one");
        Keyboard.Key(first.World, Key.Shift, true);
        Keyboard.Key(first.World, Key.Left, true);
        Keyboard.Composition(first.World, "一", 1);
        Gamepad.Navigate(second.World, Direction.Right);
        Keyboard.Text(second.World, "two");
        Keyboard.Key(second.World, Key.Left, true);
        first.Frame();
        second.Frame();
        EditDisplay firstDisplay = first.World.Read<EditDisplay>(firstRoot);
        EditDisplay secondDisplay = second.World.Read<EditDisplay>(secondRoot);
        Require(firstDisplay.Text == "one" && firstDisplay.Caret == 2 && firstDisplay.SelectionStart == 3 &&
            firstDisplay.Composition == "一" && secondDisplay.Text == "two" &&
            secondDisplay.Caret == 2 && secondDisplay.SelectionStart == 2 && secondDisplay.Composition.Length == 0,
            "Each World maintains its own focused text, selection extension, and candidate presentation.");
        Keyboard.Text(first.World, "1");
        Keyboard.Text(second.World, "2");
        first.Frame();
        second.Frame();
        CheckEdit(first, firstRoot, "on1", 3, 2);
        CheckEdit(second, secondRoot, "tw2o", 3, 2);
    }

    internal static void RunFocusabilityRemoval(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "kept");
        Keyboard.Composition(frames.World, "候", 1);
        frames.Frame();
        Entity node = frames.World.Read<EditDisplay>(root).Node;
        frames.World.Remove<Focusable>(node);
        Keyboard.Text(frames.World, "lost");
        frames.Frame();
        Require(frames.World.Read<EditableText>(node).Text == "kept" &&
            string.IsNullOrEmpty(frames.World.Read<EditableText>(node).Composition) &&
            frames.World.Read<EditableText>(node).CompositionCaret == 0 && !frames.World.HasSparse<Focused>(node),
            "Focusability removal ends candidate presentation and retains committed text.");
    }
}
