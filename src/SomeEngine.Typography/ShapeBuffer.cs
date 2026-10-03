using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Typography;

/// <summary>Caller-held reusable storage for one text shape.</summary>
public sealed class ShapeBuffer : IDisposable
{
    private Glyph[] glyphs = [];
    private int glyphCount;
    private TextLine[] lines = [];
    private Size size;
    private int lineCount;
    internal GlyphBuffer NativeBuffer { get; } = new();
    internal Span<Glyph> Glyphs => glyphs.AsSpan(0, glyphCount);
    internal int LineCount => lineCount;
    internal float Height => size.Height;
    public TextShape Shape => new(glyphs, glyphCount, lines, lineCount, size);

    internal void Reset()
    {
        DevelopmentCheck.Require(!NativeBuffer.IsClosed, "The shape buffer must be alive while shaping.");
        ResetGlyphs();
        lineCount = 0;
    }

    internal void Add(Glyph glyph)
    {
        if (glyphCount == glyphs.Length)
            Array.Resize(ref glyphs, Math.Max(32, glyphs.Length * 2));
        glyphs[glyphCount++] = glyph;
    }

    internal void Add(TextLine line)
    {
        EnsureLineCapacity();
        lines[lineCount++] = line;
    }

    internal void ResetGlyphs()
    {
        Array.Clear(glyphs, 0, glyphCount);
        glyphCount = 0;
        size = default;
    }

    internal void ReplaceLine(int index, TextLine line)
    {
        lines[index] = line;
        size = new Size(MathF.Max(size.Width, line.Width), size.Height + line.Height);
    }

    internal void TrimGlyphs(int count)
    {
        Array.Clear(glyphs, count, glyphCount - count);
        glyphCount = count;
    }

    internal void ChangeLineText(int index, int start, int end) =>
        lines[index] = lines[index] with { TextStart = start, TextLength = end - start };

    internal void InsertLineText(int index, int start, int end)
    {
        EnsureLineCapacity();
        Array.Copy(lines, index, lines, index + 1, lineCount - index);
        lines[index] = new TextLine(start, end - start, 0, 0, 0, 0, 0);
        lineCount++;
    }

    private void EnsureLineCapacity()
    {
        if (lineCount == lines.Length)
            Array.Resize(ref lines, Math.Max(8, lines.Length * 2));
    }

    public void Dispose()
    {
        NativeBuffer.Dispose();
        glyphs = [];
        lines = [];
        glyphCount = 0;
        size = default;
        lineCount = 0;
    }
}

internal sealed class GlyphBuffer : SafeHandle
{
    internal GlyphBuffer() : base(0, true) => SetHandle(HarfBuzz.CreateBuffer());
    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        HarfBuzz.DestroyBuffer(handle);
        return true;
    }
}
