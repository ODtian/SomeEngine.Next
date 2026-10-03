using System.Globalization;
using System.Numerics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Typography;

public static class Shaper
{
    /// <summary>Shapes left-to-right text into caller-held storage. Font size is measured in pixels per em.</summary>
    public static void Shape(ReadOnlySpan<char> text, in TextStyleData style, float wrapWidth, ref ShapeBuffer output) =>
        Shape(text, style, wrapWidth, output);

    /// <summary>Shapes text and wraps whole glyph clusters. Nonpositive widths and positive infinity use natural width.</summary>
    public static void Shape(ReadOnlySpan<char> text, in TextStyleData style, float wrapWidth, ShapeBuffer output)
    {
        DevelopmentCheck.Require(output is not null, "Text shaping needs a buffer.");
        DevelopmentCheck.Require(style.Font is not null, "Text needs a font.");
        DevelopmentCheck.Require(!style.Font.Data.IsClosed, "The font must be alive while shaping.");
        DevelopmentCheck.Require(float.IsFinite(style.FontSize) && style.FontSize > 0, "Font size must be positive and finite.");
        DevelopmentCheck.Require(!float.IsNaN(wrapWidth), "Wrap width must be a number.");
        output.Reset();
        if (text.IsEmpty) return;

        int start = 0;
        bool hasWrappedLines = false;
        while (true)
        {
            int breakOffset = text[start..].IndexOfAny('\r', '\n');
            int end = breakOffset >= 0 ? start + breakOffset : text.Length;
            int firstGlyph = output.Glyphs.Length;
            AppendParagraph(text, start, end, style, output);
            hasWrappedLines |= Layout(text, start, end, firstGlyph, style, wrapWidth, output);
            if (end == text.Length) break;
            start = end + 1;
            if (text[end] == '\r' && start < text.Length && text[start] == '\n')
                start++;
        }
        if (hasWrappedLines)
            ShapeLines(text, style, wrapWidth, output);
        else
            PlaceLines(style, output);
    }

    private static void AppendParagraph(
        ReadOnlySpan<char> text, int start, int textEnd, in TextStyleData style, ShapeBuffer output)
    {
        if (start == textEnd) return;
        int end = start + StringInfo.GetNextTextElementLength(text.Slice(start, textEnd - start));
        Font font = SelectFont(text[start..end], style);
        while (end < textEnd)
        {
            int length = StringInfo.GetNextTextElementLength(text.Slice(end, textEnd - end));
            Font next = SelectFont(text.Slice(end, length), style);
            if (!ReferenceEquals(font, next))
            {
                AppendText(text, start, end - start, font, style.FontSize, output);
                start = end;
                font = next;
            }
            end += length;
        }
        AppendText(text, start, end - start, font, style.FontSize, output);
    }

    private static Font SelectFont(ReadOnlySpan<char> text, in TextStyleData style)
    {
        if (style.Font.Supports(text)) return style.Font;
        foreach (Font font in style.Fallbacks.Span)
        {
            DevelopmentCheck.Require(font is not null, "Each fallback entry needs a font.");
            if (font.Supports(text)) return font;
        }
        return style.Font;
    }

    private static unsafe void AppendText(
        ReadOnlySpan<char> text, int start, int length, Font font, float fontSize, ShapeBuffer output)
    {
        float scale = fontSize / font.UnitsPerEm;
        GlyphBuffer buffer = output.NativeBuffer;
        bool retained = false;
        buffer.DangerousAddRef(ref retained);
        try
        {
            HarfBuzz.ResetBuffer(buffer);
            fixed (char* characters = text)
                HarfBuzz.AddText(buffer, characters, text.Length, (uint)start, length);
            HarfBuzz.SetDirection(buffer, HarfBuzz.LeftToRight);
            HarfBuzz.GuessSegmentProperties(buffer);
            HarfBuzz.Shape(font.Data, buffer, 0, 0);
            if (HarfBuzz.IsBufferAllocated(buffer) == 0)
                throw new OutOfMemoryException();

            GlyphInfo* info = HarfBuzz.GetGlyphInfo(buffer, out uint count);
            GlyphPosition* positions = HarfBuzz.GetGlyphPositions(buffer, out _);
            for (int index = 0; index < count; index++)
            {
                GlyphPosition position = positions[index];
                var advance = new Vector2(position.AdvanceX, -position.AdvanceY) * scale;
                output.Add(new Glyph(font, info[index].Index, (int)info[index].Cluster,
                    new Vector2(position.OffsetX, -position.OffsetY) * scale, advance, 0));
            }
        }
        finally
        {
            if (retained) buffer.DangerousRelease();
        }
    }

    private static bool Layout(
        ReadOnlySpan<char> text, int textStart, int textEnd, int firstGlyph,
        in TextStyleData style, float wrapWidth, ShapeBuffer output)
    {
        Span<Glyph> glyphs = output.Glyphs;
        int lineStart = firstGlyph;
        bool wrapped = false;
        do
        {
            int end = FindLineEnd(text, glyphs, lineStart, wrapWidth);
            int lineTextEnd = end < glyphs.Length ? glyphs[end].TextIndex : textEnd;
            output.Add(new TextLine(textStart, lineTextEnd - textStart,
                lineStart, end - lineStart, 0, 0, 0));
            wrapped |= end < glyphs.Length;
            lineStart = end;
            textStart = lineTextEnd;
        } while (lineStart < glyphs.Length);
        return wrapped;
    }

    private static int FindLineEnd(ReadOnlySpan<char> text, ReadOnlySpan<Glyph> glyphs, int firstGlyph, float wrapWidth)
    {
        int first = firstGlyph;
        int breakAfter = firstGlyph;
        float width = 0;
        while (first < glyphs.Length)
        {
            int end = first + 1;
            float advance = glyphs[first].Advance.X;
            while (end < glyphs.Length && glyphs[end].TextIndex == glyphs[first].TextIndex)
                advance += glyphs[end++].Advance.X;
            if (wrapWidth > 0 && width + advance > wrapWidth && first > firstGlyph)
                return breakAfter > firstGlyph ? breakAfter : first;
            if (text[glyphs[first].TextIndex] is ' ' or '\t')
                breakAfter = end;
            width += advance;
            first = end;
        }
        return glyphs.Length;
    }

    private static void ShapeLines(ReadOnlySpan<char> text, in TextStyleData style, float wrapWidth, ShapeBuffer output)
    {
        output.ResetGlyphs();
        for (int index = 0; index < output.LineCount; index++)
        {
            TextLine line = output.Shape.Lines[index];
            int first = output.Glyphs.Length;
            int originalEnd = line.TextStart + line.TextLength;
            int end = originalEnd;
            while (true)
            {
                AppendParagraph(text, line.TextStart, end, style, output);
                int glyphEnd = FindLineEnd(text, output.Glyphs, first, wrapWidth);
                if (glyphEnd == output.Glyphs.Length) break;
                end = output.Glyphs[glyphEnd].TextIndex;
                output.TrimGlyphs(first);
            }
            output.ReplaceLine(index, CompleteLine(style, output, first, output.Glyphs.Length,
                line.TextStart, end, index));
            if (end < originalEnd)
            {
                int nextIndex = index + 1;
                if (nextIndex < output.LineCount && output.Shape.Lines[nextIndex].TextStart == originalEnd)
                {
                    TextLine next = output.Shape.Lines[nextIndex];
                    output.ChangeLineText(nextIndex, end, next.TextStart + next.TextLength);
                }
                else
                {
                    output.InsertLineText(nextIndex, end, originalEnd);
                }
            }
        }
    }

    private static void PlaceLines(in TextStyleData style, ShapeBuffer output)
    {
        for (int index = 0; index < output.LineCount; index++)
        {
            TextLine line = output.Shape.Lines[index];
            output.ReplaceLine(index, CompleteLine(style, output, line.GlyphStart,
                line.GlyphStart + line.GlyphCount, line.TextStart, line.TextStart + line.TextLength, index));
        }
    }

    private static TextLine CompleteLine(
        in TextStyleData style, ShapeBuffer output, int first, int end, int textStart, int textEnd, int lineIndex)
    {
        Span<Glyph> glyphs = output.Glyphs;
        float ascent = 0;
        float descent = 0;
        float gap = 0;
        if (first == end)
            MeasureFont(style.Font, style.FontSize, ref ascent, ref descent, ref gap);
        for (int index = first; index < end; index++)
            MeasureFont(glyphs[index].Font, style.FontSize, ref ascent, ref descent, ref gap);
        float baseline = output.Height + ascent;
        float height = ascent + descent + gap;
        var point = new Vector2(0, baseline);
        for (int index = first; index < end; index++)
        {
            Glyph glyph = glyphs[index];
            glyphs[index] = glyph with { Position = point + glyph.Position, LineIndex = lineIndex };
            point += glyph.Advance;
        }
        return new TextLine(textStart, textEnd - textStart, first, end - first, point.X, height, baseline);
    }

    private static void MeasureFont(Font font, float fontSize, ref float ascent, ref float descent, ref float gap)
    {
        float scale = fontSize / font.UnitsPerEm;
        ascent = MathF.Max(ascent, font.Extents.Ascent * scale);
        descent = MathF.Max(descent, -font.Extents.Descent * scale);
        gap = MathF.Max(gap, font.Extents.LineGap * scale);
    }
}
