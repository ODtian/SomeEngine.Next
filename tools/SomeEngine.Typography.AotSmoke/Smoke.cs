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

        Console.WriteLine("Typography NativeAOT passed: mixed text 7 glyphs / 89.12 px; wrapped Chinese 2 lines; font fallback 63.98 px.");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
