using SomeEngine.Typography;

namespace SomeEngine.Typography.Tests;

public sealed class ShaperTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void InvalidFontBytesReportALoadError(int length)
    {
        Assert.Throws<InvalidDataException>(() => Font.Load(new byte[length]));

        using Font font = LoadFont("Latin.ttf");
        using var output = new ShapeBuffer();
        Shaper.Shape("AV", new TextStyleData(font, 20), 0, output);
        Assert.Equal(23.98f, output.Shape.Size.Width, 3);
    }

    [Fact]
    public void RefShapeBufferEntryPointMatchesHarfBuzzReference()
    {
        using Font font = LoadFont("Cjk.otf");
        var output = new ShapeBuffer();
        try
        {
            Shaper.Shape("Hello世界", new TextStyleData(font, 20), 0, ref output);

            Assert.Equal(7, output.Shape.Glyphs.Length);
            Assert.Equal(89.12f, output.Shape.Size.Width, 3);
        }
        finally
        {
            output.Dispose();
        }
    }

    [Fact]
    public void ShapeBufferCanBeReusedAcrossFontsSizesAndEmptyText()
    {
        using Font cjk = LoadFont("Cjk.otf");
        using Font latin = LoadFont("Latin.ttf");
        using var output = new ShapeBuffer();
        Shaper.Shape("中文你好", new TextStyleData(cjk, 20), 45, output);

        Shaper.Shape("AV", new TextStyleData(latin, 10), 0, output);

        Assert.Equal(new uint[] { 34, 55 }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.Index));
        Assert.Equal(11.99f, output.Shape.Size.Width, 3);
        Assert.Equal(13.62f, output.Shape.Size.Height, 3);
        Assert.Single(output.Shape.Lines.ToArray());

        Shaper.Shape("", new TextStyleData(cjk, 20), 0, output);

        Assert.Empty(output.Shape.Glyphs.ToArray());
        Assert.Empty(output.Shape.Lines.ToArray());
        Assert.Equal(new Size(0, 0), output.Shape.Size);
    }

    [Fact]
    public void LoadedFontKeepsItsBytesAfterTheAssetMemoryChanges()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf"));
        using Font font = Font.Load(bytes);
        Array.Clear(bytes);
        using var output = new ShapeBuffer();

        Shaper.Shape("office", new TextStyleData(font, 20), 0, output);

        Assert.Equal(new uint[] { 80, 115, 68, 70 }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.Index));
        Assert.Equal(51.90f, output.Shape.Size.Width, 3);
    }

    [Fact]
    public async Task SharedFontsCanBeShapedOnDifferentThreads()
    {
        using Font primary = LoadFont("Latin.ttf");
        using Font fallback = LoadFont("Cjk.otf");
        var style = new TextStyleData(primary, 20, new[] { fallback });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var output = new ShapeBuffer();
            Shaper.Shape("AV中文", style, 0, output);
            Assert.Equal(new uint[] { 34, 55, 208, 214 }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.Index));
            Assert.Equal(63.98f, output.Shape.Size.Width, 3);
        })));
    }

    [Fact]
    public void WrappingKeepsLigaturesAndSupplementaryCharactersTogether()
    {
        using Font font = LoadFont("Latin.ttf");
        using Font cjk = LoadFont("Cjk.otf");
        using var output = new ShapeBuffer();
        Shaper.Shape("office", new TextStyleData(font, 20), 15, output);

        Assert.Equal(new[] { 1, 3, 1, 1 }, output.Shape.Lines.ToArray().Select(line => line.TextLength));
        Assert.Equal(18.92f, output.Shape.Lines[1].Width, 3);

        Shaper.Shape("𠀋中", new TextStyleData(cjk, 20), 20, output);

        Assert.Equal(new[] { 2, 1 }, output.Shape.Lines.ToArray().Select(line => line.TextLength));
        Assert.Equal(new[] { 0, 2 }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.TextIndex));
    }

    [Fact]
    public void WrapWidthUsesAdvancesAfterLineKerning()
    {
        using Font font = LoadFont("Latin.ttf");
        using var output = new ShapeBuffer();

        Shaper.Shape("AVA", new TextStyleData(font, 20), 23.5f, output);

        Assert.Equal(3, output.Shape.LineCount);
        Assert.Equal(new[] { 1, 1, 1 }, output.Shape.Lines.ToArray().Select(line => line.TextLength));
        Assert.Equal(12.78f, output.Shape.Size.Width, 3);
    }

    [Fact]
    public void WrappedLinesShapeTheirOwnKerning()
    {
        using Font font = LoadFont("Latin.ttf");
        using var output = new ShapeBuffer();

        Shaper.Shape("AV", new TextStyleData(font, 20), 13, output);

        Assert.Equal(2, output.Shape.LineCount);
        Assert.Equal(12.78f, output.Shape.Glyphs[0].Advance.X, 3);
        Assert.Equal(12.78f, output.Shape.Lines[0].Width, 3);
        Assert.Equal(12f, output.Shape.Lines[1].Width, 3);
    }

#if DEBUG
    [Fact]
    public void InvalidShapingArgumentsUseDevelopmentChecks()
    {
        using Font font = LoadFont("Latin.ttf");
        using var output = new ShapeBuffer();
        var style = new TextStyleData(font, 20);

        Assert.Throws<InvalidOperationException>(() => Shaper.Shape("A", style, float.NaN, output));
        Assert.Throws<InvalidOperationException>(() => Shaper.Shape("A", style, 0, null!));
        Assert.Throws<InvalidOperationException>(() => Shaper.Shape("A", new TextStyleData(font, 0), 0, output));
        output.Dispose();
        Assert.Throws<InvalidOperationException>(() => Shaper.Shape("A", style, 0, output));
        font.Dispose();
        using var another = new ShapeBuffer();
        Assert.Throws<InvalidOperationException>(() => Shaper.Shape("A", style, 0, another));
    }
#endif

    [Fact]
    public void WrappingUsesSpacesBeforeSplittingAWiderWord()
    {
        using Font font = LoadFont("Cjk.otf");
        using var output = new ShapeBuffer();

        Shaper.Shape("Hello world", new TextStyleData(font, 20), 80, output);

        Assert.Equal(2, output.Shape.LineCount);
        Assert.Equal(new[] { 0, 6 }, output.Shape.Lines.ToArray().Select(line => line.TextStart));
        Assert.Equal(new[] { 6, 5 }, output.Shape.Lines.ToArray().Select(line => line.TextLength));
        Assert.Equal(53.92f, output.Shape.Size.Width, 3);
        Assert.Equal(0, output.Shape.Glyphs[6].Position.X);
    }

    [Theory]
    [InlineData("Cjk.otf", "Hello世界", 89.12f, 28.96f,
        new uint[] { 41, 70, 77, 77, 80, 207, 216 }, new int[] { 0, 1, 2, 3, 4, 5, 6 })]
    [InlineData("Latin.ttf", "office", 51.90f, 27.24f,
        new uint[] { 80, 115, 68, 70 }, new int[] { 0, 1, 4, 5 })]
    [InlineData("Latin.ttf", "AV", 23.98f, 27.24f,
        new uint[] { 34, 55 }, new int[] { 0, 1 })]
    [InlineData("Latin.ttf", "e\u0301", 11.28f, 27.24f,
        new uint[] { 102 }, new int[] { 0 })]
    [InlineData("Cjk.otf", "𠀋", 20f, 28.96f,
        new uint[] { 327 }, new int[] { 0 })]
    public void ShapingMatchesHarfBuzzReference(
        string fontName, string text, float width, float height, uint[] glyphs, int[] textIndices)
    {
        using Font font = LoadFont(fontName);
        using var output = new ShapeBuffer();

        Shaper.Shape(text, new TextStyleData(font, 20), 0, output);

        Assert.Equal(glyphs, output.Shape.Glyphs.ToArray().Select(glyph => glyph.Index));
        Assert.Equal(textIndices, output.Shape.Glyphs.ToArray().Select(glyph => glyph.TextIndex));
        Assert.Equal(width, output.Shape.Size.Width, 3);
        Assert.Equal(height, output.Shape.Size.Height, 3);
        Assert.Equal(1, output.Shape.LineCount);
    }

    [Theory]
    [InlineData("中文\n你好", 2, new int[] { 0, 1, 3, 4 }, new int[] { 0, 3 })]
    [InlineData("中\r\n文\n", 3, new int[] { 0, 3 }, new int[] { 0, 3, 5 })]
    [InlineData("中\r文", 2, new int[] { 0, 2 }, new int[] { 0, 2 })]
    [InlineData("\n", 2, new int[] { }, new int[] { 0, 1 })]
    public void LineBreaksKeepTextIndicesAndEmptyLines(string text, int lineCount, int[] textIndices, int[] lineStarts)
    {
        using Font font = LoadFont("Cjk.otf");
        using var output = new ShapeBuffer();

        Shaper.Shape(text, new TextStyleData(font, 20), 0, output);

        Assert.Equal(lineCount, output.Shape.LineCount);
        Assert.Equal(textIndices, output.Shape.Glyphs.ToArray().Select(glyph => glyph.TextIndex));
        Assert.Equal(lineStarts, output.Shape.Lines.ToArray().Select(line => line.TextStart));
        Assert.Equal(28.96f * lineCount, output.Shape.Size.Height, 3);
    }

    [Fact]
    public void FallbackFontsSupplyMissingCharactersInListOrder()
    {
        using Font primary = LoadFont("Latin.ttf");
        using Font fallback = LoadFont("Cjk.otf");
        using Font later = LoadFont("Cjk.otf");
        using var output = new ShapeBuffer();
        var style = new TextStyleData(primary, 20, new[] { primary, fallback, later });

        Shaper.Shape("AV中文", style, 0, output);

        Assert.Equal(new uint[] { 34, 55, 208, 214 }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.Index));
        Assert.Equal(new[] { 0, 1, 2, 3 }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.TextIndex));
        Assert.Same(primary, output.Shape.Glyphs[0].Font);
        Assert.Same(fallback, output.Shape.Glyphs[2].Font);
        Assert.Equal(63.98f, output.Shape.Size.Width, 3);
        Assert.Equal(29.06f, output.Shape.Size.Height, 3);
        Assert.Equal(23.2f, output.Shape.Lines[0].Baseline, 3);
    }

    [Fact]
    public void WrapWidthProducesLinesAndGlyphPositions()
    {
        using Font font = LoadFont("Cjk.otf");
        using var output = new ShapeBuffer();

        Shaper.Shape("中文你好", new TextStyleData(font, 20), 45, output);

        Assert.Equal(2, output.Shape.LineCount);
        Assert.Equal(40, output.Shape.Size.Width, 3);
        Assert.Equal(57.92f, output.Shape.Size.Height, 3);
        Assert.Equal(new[] { 0, 0, 1, 1 }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.LineIndex));
        Assert.Equal(new[] { 0f, 20f, 0f, 20f }, output.Shape.Glyphs.ToArray().Select(glyph => glyph.Position.X));
        Assert.Equal(52.16f, output.Shape.Glyphs[2].Position.Y, 3);
        Assert.Equal(new[] { 0, 2 }, output.Shape.Lines.ToArray().Select(line => line.TextStart));
        Assert.Equal(new[] { 2, 2 }, output.Shape.Lines.ToArray().Select(line => line.TextLength));
        Assert.Equal(new[] { 2, 2 }, output.Shape.Lines.ToArray().Select(line => line.GlyphCount));
    }

    [Fact]
    public void EmptyTextHasZeroSizeAndLines()
    {
        using Font font = LoadFont("Cjk.otf");
        using var output = new ShapeBuffer();

        Shaper.Shape("", new TextStyleData(font, 20), 0, output);

        Assert.Empty(output.Shape.Glyphs.ToArray());
        Assert.Equal(new Size(0, 0), output.Shape.Size);
        Assert.Equal(0, output.Shape.LineCount);
    }

    private static Font LoadFont(string name) =>
        Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name)));
}
