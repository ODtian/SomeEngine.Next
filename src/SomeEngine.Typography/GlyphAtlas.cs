using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Typography;

/// <summary>Caller-held glyph SDF pages and the regions added since the last extraction.</summary>
public sealed partial class GlyphAtlas
{
    private readonly Dictionary<(Font Font, uint Glyph), GlyphLocation> glyphs = new();
    private readonly List<AtlasPage> pages = [];
    private readonly List<AtlasDirtyRegion> dirtyRegions = [];

    public int PageWidth { get; }
    public int PageHeight { get; }
    public int FontSize { get; }
    public int DistanceSpread { get; }
    public ReadOnlySpan<AtlasPage> Pages => CollectionsMarshal.AsSpan(pages);
    public ReadOnlySpan<AtlasDirtyRegion> DirtyRegions => CollectionsMarshal.AsSpan(dirtyRegions);

    public GlyphAtlas(int pageWidth = 1024, int pageHeight = 1024, int fontSize = 48, int distanceSpread = 8)
    {
        DevelopmentCheck.Require(pageWidth > 2 && pageHeight > 2, "Atlas pages need space for glyphs and their border.");
        DevelopmentCheck.Require(fontSize > 0, "Atlas font size must be positive.");
        DevelopmentCheck.Require(distanceSpread is >= 2 and <= 32, "SDF distance spread must be between 2 and 32 pixels.");
        PageWidth = pageWidth;
        PageHeight = pageHeight;
        FontSize = fontSize;
        DistanceSpread = distanceSpread;
    }

    public GlyphLocation Get(Font font, uint glyph)
    {
        DevelopmentCheck.Require(font is not null, "A glyph needs a font.");
        DevelopmentCheck.Require(!font.Data.IsClosed, "The font must be alive while using its atlas.");
        DevelopmentCheck.Require(glyph < HarfBuzz.GetGlyphCount(font.Data.Face), "The glyph must belong to the font.");
        if (glyphs.TryGetValue((font, glyph), out GlyphLocation location)) return location;

        GlyphBitmap bitmap = font.Data.RenderGlyph(glyph, FontSize, DistanceSpread);
        if (bitmap.Width == 0 || bitmap.Height == 0)
        {
            location = new GlyphLocation(new AtlasDirtyRegion(-1, 0, 0, 0, 0), Vector2.Zero, Vector2.Zero, Vector2.Zero);
            glyphs.Add((font, glyph), location);
            return location;
        }
        AtlasDirtyRegion region = default;
        AtlasPage? page = pages.Count > 0 ? pages[^1] : null;
        if (page is null || !page.TryAdd(pages.Count - 1, bitmap, out region))
        {
            page = new AtlasPage(Math.Max(PageWidth, checked(bitmap.Width + 2)),
                Math.Max(PageHeight, checked(bitmap.Height + 2)));
            page.TryAdd(pages.Count, bitmap, out region);
            pages.Add(page);
        }
        location = new GlyphLocation(region,
            new Vector2((float)region.X / page.Width, (float)region.Y / page.Height),
            new Vector2((float)(region.X + region.Width) / page.Width, (float)(region.Y + region.Height) / page.Height),
            bitmap.Offset);
        glyphs.Add((font, glyph), location);
        dirtyRegions.Add(region);
        return location;
    }

    /// <summary>Call after copying the newly added pixels through render extraction.</summary>
    public void ClearDirtyRegions() => dirtyRegions.Clear();
}
