using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Text editing through public input, components, view scopes, and frame updates.</summary>
internal static partial class TextEditingChecks
{
    internal static void RunInsertion(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "你");
        Keyboard.Text(frames.World, "好");
        frames.Frame();

        EditDisplay display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "你好" && display.Caret == 2 && display.SelectionStart == 2,
            "Text injected after focus inserts in event order and leaves a collapsed selection.");
        Require(display.EditedText == "你好" && display.ConsumedEdits == 1 && !display.SecondEdit,
            "One read consumes all new edits and returns the committed text.");

        frames.Frame();
        Require(frames.World.Read<EditDisplay>(root).ConsumedEdits == 1,
            "A later frame preserves the caller's consumed edit count.");
        Keyboard.Text(frames.World, "！");
        frames.Frame();
        Require(frames.World.Read<EditDisplay>(root).Text == "你好！" &&
            frames.World.Read<EditDisplay>(root).ConsumedEdits == 2,
            "A later input updates both the text and its caller through the public dependency.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    internal static void RunMovement(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "A😀e\u0301中");
        frames.Frame();

        (Key Key, int Caret)[] moves =
        [
            (Key.Left, 5), (Key.Left, 3), (Key.Left, 1), (Key.Right, 3),
            (Key.Home, 0), (Key.Left, 0), (Key.End, 6), (Key.Right, 6),
        ];
        foreach (var move in moves)
        {
            Keyboard.Key(frames.World, move.Key, true);
            Keyboard.Key(frames.World, move.Key, false);
            frames.Frame();
            EditDisplay display = frames.World.Read<EditDisplay>(root);
            Require(display.Text == "A😀e\u0301中" && display.Caret == move.Caret &&
                display.SelectionStart == move.Caret && display.ConsumedEdits == 1,
                "Caret movement uses whole characters and preserves the committed edit display.");
            Require(frames.World.HasSparse<Focused>(display.Node), "Caret movement retains focus on the editor.");
        }
    }

    private readonly record struct EditingExample(bool Rewritten = false, int Revision = 0) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity node = DeclareEditor(Absolute(), Rewritten);
            EditableText value = Scope.Read<EditableText>(node);
            ref ConsumedEdits count = ref Scope.ReadWrite<ConsumedEdits>(Scope.Entity);
            bool edited = Rewritten ? Rewrites.TextEditingFunctions.Edited(node, out string text) : node.Edited(out text);
            if (edited)
                count.Value++;
            bool secondEdit = Rewritten ? Rewrites.TextEditingFunctions.Edited(node, out _) : node.Edited(out string _);
            string committed = value.Text ?? string.Empty;
            string composition = value.Composition ?? string.Empty;
            int start = Math.Min(value.Caret, value.SelectionStart);
            int end = Math.Max(value.Caret, value.SelectionStart);
            string presented = composition.Length == 0 ? committed :
                string.Concat(committed.AsSpan(0, start), composition, committed.AsSpan(end));
            Scope.Write<EditDisplay>(Scope.Entity) = new EditDisplay(node, value.Text ?? string.Empty,
                value.Caret, value.SelectionStart, text, count.Value, secondEdit,
                composition, value.CompositionCaret, presented);
            return default;
        }
    }

    internal static void RunVerticalMovement(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "ab😀\nx\n甲乙丙");
        frames.Frame();
        (Key Key, int Caret)[] moves =
        [
            (Key.Up, 6), (Key.Up, 1), (Key.Down, 6), (Key.Down, 8), (Key.Down, 8),
            (Key.Home, 0), (Key.Up, 0), (Key.End, 10),
        ];
        foreach (var move in moves)
        {
            Keyboard.Key(frames.World, move.Key, true);
            frames.Frame();
            CheckEdit(frames, root, "ab😀\nx\n甲乙丙", move.Caret, 1);
        }
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Up, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Text(frames.World, "Z");
        frames.Frame();
        CheckEdit(frames, root, "ab😀\nxZ", 7, 2);

        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.End, true);
        Keyboard.Text(frames.World, "ab\r\nx\r\n甲乙");
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Key(frames.World, Key.Up, true);
        frames.Frame();
        CheckEdit(frames, root, "ab\r\nx\r\n甲乙", 5, 3);
    }

    internal static void RunSelection(bool rewritten = false)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingExample(rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "abcd");
        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        EditDisplay display = frames.World.Read<EditDisplay>(root);
        Require(display.Caret == 2 && display.SelectionStart == 0 && display.ConsumedEdits == 1,
            "Shift extends a forward selection while preserving the anchor and text edit display.");
        Keyboard.Text(frames.World, "XY");
        Keyboard.Key(frames.World, Key.Shift, false);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "XYcd" && display.Caret == 2 && display.SelectionStart == 2,
            "Input replaces the forward selection and collapses it after the inserted text.");

        Keyboard.Key(frames.World, Key.End, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Caret == 2 && display.SelectionStart == 4,
            "Shift also extends a reversed selection from its anchor.");
        Keyboard.Text(frames.World, "Z");
        Keyboard.Key(frames.World, Key.Shift, false);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Text == "XYZ" && display.Caret == 3 && display.SelectionStart == 3,
            "Input replaces the reversed selection at its lower boundary.");

        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Left, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Caret == 1 && display.SelectionStart == 1,
            "Left collapses an existing selection to its start.");
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        display = frames.World.Read<EditDisplay>(root);
        Require(display.Caret == 3 && display.SelectionStart == 3 && display.ConsumedEdits == 3,
            "Right collapses to the selection's end and movement preserves the committed edit count.");
    }

    private record struct ConsumedEdits(int Value) : IComponent;
    private static Entity DeclareEditor(Entity node, bool rewritten) => rewritten
        ? Rewrites.TextEditingFunctions.TextEditing(node) : TextEditingFunctions.TextEditing(node);
    private readonly record struct EditDisplay(Entity Node, string Text, int Caret, int SelectionStart,
        string EditedText, int ConsumedEdits, bool SecondEdit, string Composition, int CompositionCaret,
        string PresentedText) : IComponent;
}
