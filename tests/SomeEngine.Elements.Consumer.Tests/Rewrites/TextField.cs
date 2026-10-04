using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user text editor built entirely from public behavior, view, layout, and typography APIs.</summary>
public partial record struct TextField(string Text, string Placeholder = "") : IView, ISparseComponent
{
    public static BuildMode Mode => BuildMode.Once;

    public BuildTask Build()
    {
        TextEditingFunctions.TextEditing(Scope.Entity);
        Scope.Ensure<SingleLineText>(Scope.Entity);
        Memo<string> text = Scope.Memo(ReadText);
        Scope.Compute(() => ApplyText(text.Value));
        Scope.View(new FieldContent(Scope.Entity), 1);
        return default;
    }

    private static string ReadText()
    {
        TextField field = Scope.Read<TextField>(Scope.Entity);
        DevelopmentCheck.Require(field.Text is not null && field.Text.AsSpan().IndexOfAny('\r', '\n') < 0,
            "TextField.Text requires a single-line string.");
        DevelopmentCheck.Require(field.Placeholder is not null && field.Placeholder.AsSpan().IndexOfAny('\r', '\n') < 0,
            "TextField.Placeholder requires a single-line string.");
        Scope.Entity.Accessible(Role.TextField, field.Placeholder);
        return field.Text;
    }

    private static void ApplyText(string text)
    {
        ref EditableText value = ref Scope.Write<EditableText>(Scope.Entity);
        if (value.Text == text)
            return;
        value.Text = text;
        value.Caret = text.Length;
        value.SelectionStart = text.Length;
        value.Composition = string.Empty;
        value.CompositionCaret = 0;
    }

    private static void BuildContent(Entity editor)
    {
        EditableText value = Scope.Read<EditableText>(editor);
        string placeholder = Scope.Read<TextField>(editor).Placeholder;
        string composition = value.Composition ?? string.Empty;
        bool hasComposition = composition.Length > 0;
        int start = Math.Min(value.Caret, value.SelectionStart);
        int end = Math.Max(value.Caret, value.SelectionStart);
        string text = hasComposition
            ? string.Concat(value.Text.AsSpan(0, start), composition, value.Text.AsSpan(end)) : value.Text;
        bool empty = text.Length == 0;
        string displayed = empty ? placeholder : text;
        TextStyle style = Scope.ReadUp<TextTheme>().Style;
        bool focused = editor.Focused();
        using var buffer = new ShapeBuffer();
        Shaper.Shape(displayed.Length == 0 ? " " : displayed,
            new TextStyleData(style.Font!, style.Size, LineHeight: style.LineHeight), float.PositiveInfinity, buffer);
        TextShape shape = buffer.Shape;
        float width = displayed.Length == 0 ? 0 : shape.Size.Width;
        float height = shape.Size.Height;
        int caretPosition = hasComposition ? start + value.CompositionCaret : value.Caret;
        float caret = empty ? 0 : MeasurePosition(shape, text.Length, caretPosition);
        float anchor = empty ? 0 : MeasurePosition(shape, text.Length, value.SelectionStart);
        using (ScrollViewFunctions.ScrollView().Size(160, height + 16).Padding(8)
            .Background(new Color(0.2f, 0.2f, 0.2f)).Corner(4)
            .Border(new Color(0.15f, 0.45f, 0.85f), focused ? 1 : 0).Content())
        using (Absolute().MinSize(width + 1, height).Content())
        {
            if (focused && !hasComposition && value.Caret != value.SelectionStart)
                Absolute().Position(MathF.Min(caret, anchor), 0).Size(MathF.Abs(caret - anchor), height)
                    .Background(new Color(0.15f, 0.45f, 0.85f, 0.35f));
            TextFunctions.Text(displayed).Size(width, height)
                .TextColor(empty ? style.Color with { A = style.Color.A * 0.5f } : style.Color);
            if (focused)
                Absolute().Position(caret, 0).Size(1, height).Background(style.Color);
        }
    }

    private static float MeasurePosition(TextShape shape, int length, int position)
    {
        float x = 0;
        ReadOnlySpan<Glyph> glyphs = shape.Glyphs;
        for (int first = 0; first < glyphs.Length;)
        {
            int start = glyphs[first].TextIndex;
            if (position <= start)
                return x;
            int end = first + 1;
            float advance = glyphs[first].Advance.X;
            while (end < glyphs.Length && glyphs[end].TextIndex == start)
                advance += glyphs[end++].Advance.X;
            int next = end < glyphs.Length ? glyphs[end].TextIndex : length;
            if (position < next)
                return x + advance * (position - start) / (next - start);
            x += advance;
            first = end;
        }
        return x;
    }

    private readonly record struct FieldContent(Entity Editor) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            BuildContent(Editor);
            return default;
        }
    }
}
