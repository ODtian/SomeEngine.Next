using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFieldFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>TextField behavior through input, caller data, displayed text, and public layout.</summary>
internal static partial class TextFieldChecks
{
    internal static void RunFeedback(bool rewritten = false)
    {
        using var frames = new ValueFrames(text: true);
        var text = new State<string>("AV");
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingField(text, Rewritten: rewritten));
        frames.Frame();
        Require(DisplayText(frames.World, root) == "AV", "The field displays the caller's initial text.");

        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "!");
        frames.Frame();
        FieldDisplay display = frames.World.Read<FieldDisplay>(root);
        Require(text.Value == "AV!" && display.Text == "AV!" && display.Edits == 1 && !display.Again,
            "Edited returns the new text once and the parent passes it back as its parameter.");
        Require(DisplayText(frames.World, root) == "AV!", "The accepted edit remains displayed in the same frame.");

        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Text(frames.World, "?");
        frames.Frame();
        Require(text.Value == "A?V!" && DisplayText(frames.World, root) == "A?V!",
            "An edit at a moved caret remains displayed after the parent acknowledges it.");
        Keyboard.Text(frames.World, ".");
        frames.Frame();
        Require(text.Value == "A?.V!" && DisplayText(frames.World, root) == "A?.V!",
            "Acknowledging the text retains the caret for the next insertion.");
        frames.Frame();
        Require(frames.World.Read<FieldDisplay>(root).Edits == 3, "A later frame retains the parent's accepted edit count.");
    }

    internal static void RunSelection(bool rewritten = false)
    {
        using var frames = new ValueFrames(text: true);
        var text = new State<string>("AV");
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingField(text, Rewritten: rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();

        Entity label = TextNode(frames.World, root);
        Rect labelRect = frames.World.Read<LayoutRect>(label).Value;
        Rect caret = ColoredRectangle(frames.World, root, new Color(1, 1, 1));
        Require(Near(caret.X, labelRect.X + 23.98f) && Near(caret.Y, labelRect.Y) &&
            Near(caret.Width, 1) && Near(caret.Height, labelRect.Height),
            "The focused caret follows the displayed line's end and height.");

        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        float secondGlyph = frames.World.Read<ShapedText>(label).Shape.Glyphs[1].Position.X;
        Rect selection = ColoredRectangle(frames.World, root, new Color(0.15f, 0.45f, 0.85f, 0.35f));
        caret = ColoredRectangle(frames.World, root, new Color(1, 1, 1));
        Require(Near(selection.X, labelRect.X) && Near(selection.Width, secondGlyph) &&
            Near(selection.Y, labelRect.Y) && Near(selection.Height, labelRect.Height) &&
            Near(caret.X, labelRect.X + secondGlyph),
            "The first selected character ends at the following glyph, including kerning.");

        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Key(frames.World, Key.End, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();
        selection = ColoredRectangle(frames.World, root, new Color(0.15f, 0.45f, 0.85f, 0.35f));
        caret = ColoredRectangle(frames.World, root, new Color(1, 1, 1));
        Require(Near(selection.X, labelRect.X + secondGlyph) && Near(selection.Width, 23.98f - secondGlyph) &&
            Near(caret.X, selection.X), "A reverse selection draws the same selected glyph range and its active caret.");
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Text(frames.World, "!");
        frames.Frame();
        Require(text.Value == "A!" && DisplayText(frames.World, root) == "A!",
            "Typing replaces the visibly selected character and the parent accepts the text.");
        Require(ButtonChecks.DescendantsWith<Surface>(frames.World, root).All(node =>
            frames.World.Read<Surface>(node).Fill != new Color(0.15f, 0.45f, 0.85f, 0.35f)),
            "The accepted replacement displays a collapsed selection.");
    }

    private static Rect ColoredRectangle(World world, Entity root, Color color) =>
        world.Read<LayoutRect>(ButtonChecks.DescendantsWith<Surface>(world, root)
            .Single(node => world.Read<Surface>(node).Fill == color)).Value;

    private static Entity TextNode(World world, Entity root) =>
        ButtonChecks.DescendantsWith<TextContent>(world, root).Single();

    private static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 0.001f;

    internal static void RunPlaceholder(bool rewritten = false)
    {
        using var frames = new ValueFrames(text: true);
        var text = new State<string>(string.Empty);
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingField(text, "Name", rewritten));
        frames.Frame();
        Require(DisplayText(frames.World, root) == "Name" &&
            frames.World.Read<ShapedText>(TextNode(frames.World, root)).Style.Color == new Color(1, 1, 1, 0.5f),
            "An empty field displays the placeholder with half the text theme's alpha.");
        Require(ButtonChecks.DescendantsWith<Surface>(frames.World, root).All(node =>
            frames.World.Read<Surface>(node).Fill != new Color(1, 1, 1)),
            "The inactive field displays its ordinary surface and placeholder.");

        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Rect caret = ColoredRectangle(frames.World, root, new Color(1, 1, 1));
        Rect label = frames.World.Read<LayoutRect>(TextNode(frames.World, root)).Value;
        Require(Near(caret.X, label.X), "The empty focused field displays its caret at the start of the placeholder.");
        Keyboard.Text(frames.World, "X");
        frames.Frame();
        Require(text.Value == "X" && DisplayText(frames.World, root) == "X" &&
            frames.World.Read<ShapedText>(TextNode(frames.World, root)).Style.Color == new Color(1, 1, 1),
            "Typing replaces the placeholder with committed text in the ordinary text color.");
        Keyboard.Key(frames.World, Key.Backspace, true);
        frames.Frame();
        Require(text.Value == string.Empty && DisplayText(frames.World, root) == "Name",
            "Deleting the last character displays the placeholder again.");
        frames.World.Replace(root, new EditingField(text, "Updated", rewritten));
        frames.Frame();
        Require(DisplayText(frames.World, root) == "Updated", "A changed placeholder appears in the same frame.");
    }

    private static string DisplayText(World world, Entity root) =>
        world.Read<TextContent>(TextNode(world, root)).Value;

    internal static void RunComposition(bool rewritten = false)
    {
        using var frames = new ValueFrames(text: true);
        var text = new State<string>("AV!");
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingField(text, Rewritten: rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Composition(frames.World, "XY", 1);
        frames.Frame();
        Require(DisplayText(frames.World, root) == "XYV!" && text.Value == "AV!" &&
            frames.World.Read<FieldDisplay>(root).Edits == 0,
            "IME candidates replace the selected presentation while the caller retains committed text.");
        Entity label = TextNode(frames.World, root);
        Rect labelRect = frames.World.Read<LayoutRect>(label).Value;
        float secondGlyph = frames.World.Read<ShapedText>(label).Shape.Glyphs[1].Position.X;
        Require(Near(ColoredRectangle(frames.World, root, new Color(1, 1, 1)).X, labelRect.X + secondGlyph),
            "The displayed caret follows the platform's position within the candidate text.");

        Keyboard.Composition(frames.World, "AB", 0);
        frames.Frame();
        Require(DisplayText(frames.World, root) == "ABV!" &&
            Near(ColoredRectangle(frames.World, root, new Color(1, 1, 1)).X, labelRect.X),
            "Updating a candidate refreshes the displayed text and composition caret together.");
        Keyboard.Composition(frames.World, string.Empty, 0);
        frames.Frame();
        Require(DisplayText(frames.World, root) == "AV!" && text.Value == "AV!",
            "Cancelling composition restores the committed selection's presentation.");
        Keyboard.Composition(frames.World, "candidate", 3);
        Keyboard.Text(frames.World, "Z");
        frames.Frame();
        Require(text.Value == "ZV!" && DisplayText(frames.World, root) == "ZV!" &&
            frames.World.Read<FieldDisplay>(root).Edits == 1,
            "Committed input replaces the selection and Edited gives that text to the parent.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    internal static void RunSingleLine(bool rewritten = false)
    {
        using var frames = new ValueFrames(text: true);
        var text = new State<string>(string.Empty);
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingField(text, Rewritten: rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "A\r\nB\n");
        frames.Frame();
        Require(text.Value == "AB" && DisplayText(frames.World, root) == "AB" &&
            frames.World.Read<FieldDisplay>(root).Edits == 1,
            "Single-line input retains characters and removes CR and LF before applying an edit.");

        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Key(frames.World, Key.Shift, false);
        Keyboard.Text(frames.World, "\r\n");
        frames.Frame();
        Require(text.Value == "AB" && frames.World.Read<FieldDisplay>(root).Edits == 1,
            "A line-break-only input retains the committed text and accepted edit count.");
        Keyboard.Key(frames.World, Key.Backspace, true);
        frames.Frame();
        Require(text.Value == "B", "The retained selection remains available to a later deletion.");

        Keyboard.Composition(frames.World, "X\r\nY", 2);
        frames.Frame();
        Require(DisplayText(frames.World, root) == "XYB" && text.Value == "B",
            "A composition candidate uses the same single-line input rule.");
        Entity label = TextNode(frames.World, root);
        Rect labelRect = frames.World.Read<LayoutRect>(label).Value;
        float secondGlyph = frames.World.Read<ShapedText>(label).Shape.Glyphs[1].Position.X;
        Require(Near(ColoredRectangle(frames.World, root, new Color(1, 1, 1)).X, labelRect.X + secondGlyph),
            "Removing line breaks adjusts the platform's composition caret to the displayed string.");
        Keyboard.Text(frames.World, "Z\n");
        frames.Frame();
        Require(text.Value == "ZB" && DisplayText(frames.World, root) == "ZB",
            "Committing a candidate applies the single-line characters to the caller's text.");

        text.Value = new string('A', 100);
        frames.Frame();
        label = TextNode(frames.World, root);
        Require(frames.World.Read<ShapedText>(label).Shape.LineCount == 1 &&
            frames.World.Read<LayoutRect>(label).Value.Width > 160,
            "Long text retains its natural single-line width inside the field viewport.");
    }

    internal static void RunParameters(bool rewritten = false)
    {
        using var frames = new ValueFrames(text: true);
        var text = new State<string>("AV");
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas,
            new EditingField(text, Rewritten: rewritten, AcceptEdits: false));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "!");
        frames.Frame();
        Require(text.Value == "AV" && DisplayText(frames.World, root) == "AV!",
            "The caller can consume an edit while retaining its current parameter.");
        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Right, true);
        Keyboard.Composition(frames.World, "XY", 1);
        frames.Frame();
        frames.World.Replace(root, new EditingField(text, "Name", rewritten, AcceptEdits: false));
        frames.Frame();
        Require(DisplayText(frames.World, root) == "AXYV!" && frames.World.Read<FieldDisplay>(root).Edits == 1,
            "An equal text parameter and a changed placeholder retain the editor's current text and composition.");

        text.Value = "Z";
        frames.Frame();
        Require(DisplayText(frames.World, root) == "Z" && frames.World.Read<FieldDisplay>(root).Edits == 1,
            "A changed caller parameter replaces the text and ends composition while retaining the accepted count.");
        Keyboard.Text(frames.World, "!");
        frames.Frame();
        Require(text.Value == "Z" && DisplayText(frames.World, root) == "Z!" &&
            frames.World.Read<FieldDisplay>(root).Edits == 2,
            "A later input starts at the end of the replacement parameter and produces a new edit.");
    }

    internal static void RunTheme(bool rewritten = false)
    {
        using var frames = new ValueFrames(text: true);
        var text = new State<string>("AV");
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new EditingField(text, Rewritten: rewritten));
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        TextStyle style = frames.World.Read<TextTheme>(frames.Canvas).Style;
        style.Size = 40;
        style.LineHeight = 60;
        style.Color = new Color(0.4f, 0.5f, 0.6f, 0.8f);
        frames.World.Replace(frames.Canvas, new TextTheme(style));
        frames.Frame();
        Entity label = TextNode(frames.World, root);
        Rect labelRect = frames.World.Read<LayoutRect>(label).Value;
        Rect caret = ColoredRectangle(frames.World, root, style.Color);
        Require(frames.World.Read<ShapedText>(label).Style.Color == style.Color &&
            Near(labelRect.Width, 47.96f) && Near(labelRect.Height, 60) &&
            Near(caret.X, labelRect.X + 47.96f) && Near(caret.Height, 60),
            "A theme update changes the text's color, size, line height, and caret in the same frame.");
        Keyboard.Key(frames.World, Key.Home, true);
        Keyboard.Key(frames.World, Key.Shift, true);
        Keyboard.Key(frames.World, Key.Right, true);
        frames.Frame();
        Rect selection = ColoredRectangle(frames.World, root, new Color(0.15f, 0.45f, 0.85f, 0.35f));
        Require(Near(selection.Width, frames.World.Read<ShapedText>(label).Shape.Glyphs[1].Position.X) &&
            Near(selection.Height, 60), "The selection uses the updated font geometry and line height.");
    }

    private readonly record struct EditingField(State<string> Value, string Placeholder = "", bool Rewritten = false,
        bool AcceptEdits = true) : IView, IComponent
    {
        public BuildTask Build()
        {
            ref AcceptedEdits edits = ref Scope.ReadWrite<AcceptedEdits>(Scope.Entity);
            using (Absolute().Content())
            {
                Entity field = Rewritten ? Rewrites.TextFieldFunctions.TextField(Value.Value, Placeholder)
                    : TextField(Value.Value, Placeholder);
                if (field.Edited(out string text))
                {
                    if (AcceptEdits)
                        Value.Value = text;
                    edits.Count++;
                }
                Scope.Write<FieldDisplay>(Scope.Entity) = new FieldDisplay(field, text, edits.Count, field.Edited(out string _));
            }
            return default;
        }
    }

    private struct AcceptedEdits : IComponent { public int Count; }
    private readonly record struct FieldDisplay(Entity Field, string Text, int Edits, bool Again) : IComponent;
}
