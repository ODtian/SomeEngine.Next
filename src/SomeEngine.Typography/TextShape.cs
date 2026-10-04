using System.Numerics;

namespace SomeEngine.Typography;

public readonly record struct Size(float Width, float Height);

public readonly record struct TextStyleData(Font Font, float FontSize, ReadOnlyMemory<Font> Fallbacks = default,
    float LineHeight = 0);

/// <summary>Position uses a top-left origin and a downward Y axis. TextIndex is a UTF-16 offset.</summary>
public readonly record struct Glyph(Font Font, uint Index, int TextIndex, Vector2 Position, Vector2 Advance, int LineIndex);

/// <summary>Text ranges use UTF-16 indices. Baseline is measured from the top of the whole shape.</summary>
public readonly record struct TextLine(
    int TextStart, int TextLength, int GlyphStart, int GlyphCount,
    float Width, float Height, float Baseline);

/// <summary>A view of the shape buffer, valid until that buffer is reused or disposed.</summary>
public readonly struct TextShape
{
    private readonly Glyph[]? glyphs;
    private readonly int glyphCount;
    private readonly TextLine[]? lines;
    public ReadOnlySpan<Glyph> Glyphs => glyphs.AsSpan(0, glyphCount);
    public ReadOnlySpan<TextLine> Lines => lines.AsSpan(0, LineCount);
    public Size Size { get; }
    public int LineCount { get; }

    internal TextShape(Glyph[] glyphs, int glyphCount, TextLine[] lines, int lineCount, Size size)
    {
        this.glyphs = glyphs;
        this.glyphCount = glyphCount;
        this.lines = lines;
        Size = size;
        LineCount = lineCount;
    }
}
