using SomeEngine.Typography;

namespace SomeEngine.Tools.TypographyAotSmoke;

internal static class Smoke
{
    public static int Main()
    {
        using Font cjk = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Cjk.otf")));
        using Font latin = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        using var output = new ShapeBuffer();
        var style = new TextStyleData(cjk, 20);

        Shaper.Shape("Hello世界", style, 0, output);
        Require(output.Shape.Glyphs.Length == 7 && MathF.Abs(output.Shape.Size.Width - 89.12f) < 0.001f,
            "Mixed text must match the HarfBuzz reference.");

        Shaper.Shape("中文你好", style, 45, output);
        Require(output.Shape.Glyphs.Length == 4 && output.Shape.LineCount == 2,
            "Wrapped Chinese text must form two lines.");

        Shaper.Shape("AV中文", new TextStyleData(latin, 20, new[] { cjk }), 0, output);
        Require(output.Shape.Glyphs[2].Font == cjk && MathF.Abs(output.Shape.Size.Width - 63.98f) < 0.001f,
            "Fallback fonts must supply the Chinese glyphs.");

        var atlas = new GlyphAtlas(pageWidth: 60, pageHeight: 60, fontSize: 32, distanceSpread: 4);
        Glyph[] glyphs = output.Shape.Glyphs.ToArray();
        GlyphLocation[] locations = glyphs.Select(glyph => atlas.Get(glyph.Font, glyph.Index)).ToArray();
        Require(atlas.Pages.Length > 1, "Mixed glyphs must expand the SDF atlas to several pages.");
        byte[] distancePixels = atlas.Pages[0].Pixels.ToArray();
        Require(distancePixels.Any(pixel => pixel > 128) && distancePixels.Any(pixel => pixel is > 0 and < 128),
            "The atlas must contain glyph distance data.");
        using var content = new MemoryStream();
        atlas.Save(content);
        content.Position = 0;
        GlyphAtlas loaded = GlyphAtlas.Load(content, [cjk, latin]);
        loaded.ClearDirtyRegions();
        for (int index = 0; index < glyphs.Length; index++)
            Require(loaded.Get(glyphs[index].Font, glyphs[index].Index) == locations[index],
                "Baked glyph queries must preserve their saved positions.");
        Require(loaded.DirtyRegions.IsEmpty, "Baked glyphs must reuse the loaded page contents.");
        GlyphLocation added = loaded.Get(cjk, 216);
        Require(loaded.DirtyRegions.Length == 1 && loaded.DirtyRegions[0] == added.Region,
            "A new glyph must expose its pixel region to extraction.");

        Console.WriteLine("Typography NativeAOT passed: mixed text 7 glyphs / 89.12 px; wrapped Chinese 2 lines; font fallback 63.98 px; dynamic SDF pages; baked font loading and added regions.");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
