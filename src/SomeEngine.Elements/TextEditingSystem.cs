using System.Globalization;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements;

/// <summary>Edits the focused node at its event's position in the shared input sequence.</summary>
public static class TextEditingSystem
{
    /// <summary>Handles keyboard events for a focused editable node and reports whether it consumed the event.</summary>
    public static bool HandleInput(World world, Entity node, in FocusInput input, bool selecting = false)
    {
        DevelopmentCheck.Require(world is not null, "TextEditingSystem requires a World.");
        if (node == Entity.Null || !world.Has<EditableText>(node) || !world.HasSparse<Focused>(node))
            return false;
        if (input.Kind is not (FocusInputKind.Key or FocusInputKind.Text or FocusInputKind.Composition))
            return false;

        EditableText value = world.Read<EditableText>(node);
        string text = value.Text ?? string.Empty;
        DevelopmentCheck.Require((uint)value.Caret <= (uint)text.Length &&
            (uint)value.SelectionStart <= (uint)text.Length,
            "EditableText requires caret and selection positions inside its text.");
        DevelopmentCheck.Require(FindTextBoundaryAtOrAfter(text, value.Caret) == value.Caret &&
            (value.SelectionStart == value.Caret || FindTextBoundaryAtOrAfter(text, value.SelectionStart) == value.SelectionStart),
            "EditableText requires positions at complete text element boundaries.");
        if (input.Kind == FocusInputKind.Composition)
        {
            string composition = input.Text!;
            int compositionCaret = input.CompositionCaret;
            if (world.Has<SingleLineText>(node))
            {
                foreach (char character in composition.AsSpan(0, compositionCaret))
                    if (character is '\r' or '\n')
                        compositionCaret--;
                composition = RemoveLineBreaks(composition);
            }
            if (value.Composition != composition || value.CompositionCaret != compositionCaret)
            {
                value.Composition = composition;
                value.CompositionCaret = compositionCaret;
                world.Replace(node, value);
            }
            return true;
        }
        if (input.Kind == FocusInputKind.Text)
        {
            string inserted = world.Has<SingleLineText>(node) ? RemoveLineBreaks(input.Text!) : input.Text!;
            if (inserted.Length == 0)
                return true;
            ReplaceSelection(ref value, inserted);
            world.Replace(node, value);
            return true;
        }
        if (input.Kind != FocusInputKind.Key ||
            input.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or
                Key.Backspace or Key.Delete or Key.Enter or Key.Space))
            return false;
        if (!input.Pressed || !string.IsNullOrEmpty(value.Composition) || input.Key is Key.Enter or Key.Space)
            return true;

        if (input.Key is Key.Backspace or Key.Delete)
        {
            if (value.Caret == value.SelectionStart)
                value.SelectionStart = input.Key == Key.Backspace
                    ? FindPreviousTextBoundary(text, value.Caret) : FindNextTextBoundary(text, value.Caret);
            if (value.Caret != value.SelectionStart)
            {
                ReplaceSelection(ref value, string.Empty);
                world.Replace(node, value);
            }
            return true;
        }

        int caret = input.Key switch
        {
            Key.Left => !selecting && value.Caret != value.SelectionStart
                ? Math.Min(value.Caret, value.SelectionStart) : FindPreviousTextBoundary(text, value.Caret),
            Key.Right => !selecting && value.Caret != value.SelectionStart
                ? Math.Max(value.Caret, value.SelectionStart) : FindNextTextBoundary(text, value.Caret),
            Key.Up => FindAdjacentLineCaret(text, value.Caret, down: false),
            Key.Down => FindAdjacentLineCaret(text, value.Caret, down: true),
            Key.Home => 0,
            _ => text.Length,
        };
        int selectionStart = selecting ? value.SelectionStart : caret;
        if (caret != value.Caret || selectionStart != value.SelectionStart)
        {
            value.Caret = caret;
            value.SelectionStart = selectionStart;
            world.Replace(node, value);
        }
        return true;
    }

    private static string RemoveLineBreaks(string text) =>
        text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

    /// <summary>Ends a node's candidate presentation while preserving its committed text and selection.</summary>
    public static void CancelComposition(World world, Entity node)
    {
        DevelopmentCheck.Require(world is not null, "TextEditingSystem requires a World.");
        if (node == Entity.Null || !world.Has<EditableText>(node))
            return;
        EditableText value = world.Read<EditableText>(node);
        if (string.IsNullOrEmpty(value.Composition) && value.CompositionCaret == 0)
            return;
        value.Composition = string.Empty;
        value.CompositionCaret = 0;
        world.Replace(node, value);
    }

    private static void ReplaceSelection(ref EditableText value, string inserted)
    {
        string previous = value.Text ?? string.Empty;
        int start = Math.Min(value.Caret, value.SelectionStart);
        int end = Math.Max(value.Caret, value.SelectionStart);
        string text = string.Concat(previous.AsSpan(0, start), inserted, previous.AsSpan(end));
        value.Text = text;
        value.Caret = FindTextBoundaryAtOrAfter(text, start + inserted.Length);
        value.SelectionStart = value.Caret;
        value.Composition = string.Empty;
        value.CompositionCaret = 0;
        if (text != previous)
            value.Edits = unchecked(value.Edits + 1);
    }

    private static int FindPreviousTextBoundary(string text, int caret)
    {
        if (caret == 0)
            return 0;
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        int index = Array.BinarySearch(starts, caret);
        index = index >= 0 ? index - 1 : ~index - 1;
        return index >= 0 ? starts[index] : 0;
    }

    private static int FindTextBoundaryAtOrAfter(string text, int caret)
    {
        if (caret == text.Length)
            return caret;
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        int index = Array.BinarySearch(starts, caret);
        if (index >= 0)
            return caret;
        index = ~index;
        return index < starts.Length ? starts[index] : text.Length;
    }

    private static int FindAdjacentLineCaret(string text, int caret, bool down)
    {
        int lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        int targetStart;
        int targetEnd;
        if (down)
        {
            int newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
                return caret;
            targetStart = newline + 1;
            int nextNewline = text.IndexOf('\n', targetStart);
            targetEnd = nextNewline < 0 ? text.Length : nextNewline;
        }
        else
        {
            if (lineStart == 0)
                return caret;
            targetEnd = lineStart - 1;
            targetStart = targetEnd == 0 ? 0 : text.LastIndexOf('\n', targetEnd - 1) + 1;
        }
        if (targetEnd > targetStart && text[targetEnd - 1] == '\r')
            targetEnd--;

        int column = 0;
        for (int position = lineStart; position < caret; column++)
            position += StringInfo.GetNextTextElementLength(text.AsSpan(position));
        int target = targetStart;
        while (column > 0 && target < targetEnd)
        {
            target += StringInfo.GetNextTextElementLength(text.AsSpan(target, targetEnd - target));
            column--;
        }
        return target;
    }

    private static int FindNextTextBoundary(string text, int caret)
    {
        if (caret == text.Length)
            return caret;
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        int index = Array.BinarySearch(starts, caret);
        index = index >= 0 ? index + 1 : ~index;
        return index < starts.Length ? starts[index] : text.Length;
    }
}
