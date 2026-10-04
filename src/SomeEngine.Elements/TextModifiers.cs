using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Typography;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class TextModifiers
{
    public static Entity Font(this Entity node, Font font)
    {
        DevelopmentCheck.Require(font is not null, "Font requires a loaded font.");
        Scope.Write<TextStyle>(node).Font = font;
        return node;
    }

    public static Entity FontSize(this Entity node, float size)
    {
        DevelopmentCheck.Require(float.IsFinite(size) && size > 0, "Font size must be positive and finite.");
        Scope.Write<TextStyle>(node).Size = size;
        return node;
    }

    public static Entity TextColor(this Entity node, Color color)
    {
        Scope.Write<TextStyle>(node).Color = color;
        return node;
    }

    public static Entity LineHeight(this Entity node, float height)
    {
        DevelopmentCheck.Require(float.IsFinite(height) && height >= 0, "Line height must be finite and nonnegative.");
        Scope.Write<TextStyle>(node).LineHeight = height;
        return node;
    }
}
