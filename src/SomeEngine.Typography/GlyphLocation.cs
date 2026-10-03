using System.Numerics;

namespace SomeEngine.Typography;

/// <summary>A pixel rectangle within a CPU atlas page. Coordinates start at the top left.</summary>
public readonly record struct AtlasDirtyRegion(int PageIndex, int X, int Y, int Width, int Height);

/// <summary>UV bounds and the bitmap's top-left offset from the glyph baseline, at the atlas font size.
/// Empty glyphs use page index -1 and a zero-size region.</summary>
public readonly record struct GlyphLocation(AtlasDirtyRegion Region, Vector2 UV0, Vector2 UV1, Vector2 Offset);
