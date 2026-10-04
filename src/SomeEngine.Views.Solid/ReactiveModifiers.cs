using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements;
using SomeEngine.Typography;

namespace SomeEngine.Views.Solid;

/// <summary>Calculations that update individual modifier fields on existing nodes.</summary>
public static class ReactiveModifiers
{
    public static Entity Background(this Entity node, Func<Color> fill)
    {
        DevelopmentCheck.Require(fill is not null, "Background requires a fill getter.");
        Scope.Compute(() => SurfaceModifiers.Background(node, fill()));
        // Keep this field in the caller's copy alongside its other modifier fields.
        return SurfaceModifiers.Background(node, Scope.World.Read<Surface>(node).Fill);
    }

    public static Entity Font(this Entity node, Func<Font> font)
    {
        DevelopmentCheck.Require(font is not null, "Font requires a font getter.");
        Scope.Compute(() => TextModifiers.Font(node, font()));
        return TextModifiers.Font(node, Scope.World.Read<TextStyle>(node).Font!);
    }

    public static Entity Corner(this Entity node, Func<float> radius)
    {
        DevelopmentCheck.Require(radius is not null, "Corner requires a radius getter.");
        Scope.Compute(() => SurfaceModifiers.Corner(node, radius()));
        return SurfaceModifiers.Corner(node, Scope.World.Read<Surface>(node).CornerRadius);
    }

    public static Entity Border(this Entity node, Func<Color> color, Func<float> width)
    {
        DevelopmentCheck.Require(color is not null && width is not null, "Border requires color and width getters.");
        Scope.Compute(() => SurfaceModifiers.Border(node, color(), width()));
        Surface surface = Scope.World.Read<Surface>(node);
        return SurfaceModifiers.Border(node, surface.BorderColor, surface.BorderWidth);
    }

    public static Entity FontSize(this Entity node, Func<float> size)
    {
        DevelopmentCheck.Require(size is not null, "FontSize requires a size getter.");
        Scope.Compute(() => TextModifiers.FontSize(node, size()));
        return TextModifiers.FontSize(node, Scope.World.Read<TextStyle>(node).Size);
    }

    public static Entity TextColor(this Entity node, Func<Color> color)
    {
        DevelopmentCheck.Require(color is not null, "TextColor requires a color getter.");
        Scope.Compute(() => TextModifiers.TextColor(node, color()));
        return TextModifiers.TextColor(node, Scope.World.Read<TextStyle>(node).Color);
    }

    public static Entity LineHeight(this Entity node, Func<float> height)
    {
        DevelopmentCheck.Require(height is not null, "LineHeight requires a height getter.");
        Scope.Compute(() => TextModifiers.LineHeight(node, height()));
        return TextModifiers.LineHeight(node, Scope.World.Read<TextStyle>(node).LineHeight);
    }
}
