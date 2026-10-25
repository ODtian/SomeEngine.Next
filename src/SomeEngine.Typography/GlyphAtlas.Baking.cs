using SomeEngine.Job;

namespace SomeEngine.Typography;

public sealed partial class GlyphAtlas
{
    private const int GlyphsPerRange = 8;
    private readonly List<(Font Font, uint Glyph)> newGlyphs = [];
    private readonly HashSet<(Font Font, uint Glyph)> newGlyphKeys = [];

    /// <summary>Prebakes the font's glyph numbers, preserving their first-use order.</summary>
    public void Bake(Font font, ReadOnlySpan<uint> glyphIndices)
    {
        try
        {
            foreach (uint glyph in glyphIndices) RequestGlyph(font, glyph);
            BakeNewGlyphs();
        }
        finally
        {
            newGlyphs.Clear();
            newGlyphKeys.Clear();
        }
    }

    /// <summary>Prebakes shaped glyphs from their fonts, preserving their first-use order.</summary>
    public void Bake(ReadOnlySpan<Glyph> shapedGlyphs)
    {
        try
        {
            foreach (ref readonly Glyph glyph in shapedGlyphs) RequestGlyph(glyph.Font, glyph.Index);
            BakeNewGlyphs();
        }
        finally
        {
            newGlyphs.Clear();
            newGlyphKeys.Clear();
        }
    }

    private void RequestGlyph(Font font, uint glyph)
    {
        RequireGlyph(font, glyph);
        var key = (font, glyph);
        if (!glyphs.ContainsKey(key) && newGlyphKeys.Add(key)) newGlyphs.Add(key);
    }

    private void BakeNewGlyphs()
    {
        if (newGlyphs.Count == 0) return;
        var bitmaps = new GlyphBitmap[newGlyphs.Count];
        int rangeCount = (newGlyphs.Count + GlyphsPerRange - 1) / GlyphsPerRange;
        JobSystem.ExecuteParallel(new GlyphRasterJob(newGlyphs, bitmaps, FontSize, DistanceSpread), rangeCount);
        for (int index = 0; index < newGlyphs.Count; index++)
        {
            var glyph = newGlyphs[index];
            AddGlyph(glyph.Font, glyph.Glyph, bitmaps[index]);
        }
    }

    private readonly struct GlyphRasterJob(List<(Font Font, uint Glyph)> glyphs, GlyphBitmap[] bitmaps,
        int fontSize, int spread) : IJobParallelFor
    {
        public void Execute(int index)
        {
            var rasterizers = new Dictionary<Font, FontData.GlyphRasterizer>();
            try
            {
                int start = index * GlyphsPerRange;
                int end = Math.Min(start + GlyphsPerRange, glyphs.Count);
                for (int glyphIndex = start; glyphIndex < end; glyphIndex++)
                {
                    var glyph = glyphs[glyphIndex];
                    if (!rasterizers.TryGetValue(glyph.Font, out FontData.GlyphRasterizer? rasterizer))
                    {
                        rasterizer = glyph.Font.Data.CreateGlyphRasterizer();
                        rasterizers.Add(glyph.Font, rasterizer);
                    }
                    bitmaps[glyphIndex] = rasterizer.RenderGlyph(glyph.Glyph, fontSize, spread);
                }
            }
            finally
            {
                foreach (FontData.GlyphRasterizer rasterizer in rasterizers.Values) rasterizer.Dispose();
            }
        }
    }
}
