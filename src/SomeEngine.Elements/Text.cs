using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>Caller-written text whose measured content comes from the text shaping system.</summary>
public record struct Text(string Value) : IView, IComponent, ILayout
{
    public BuildTask Build()
    {
        TextStyle style = Scope.Has<TextStyle>(Scope.Entity) ? Scope.Read<TextStyle>(Scope.Entity) : default;
        TextStyle defaults = style.NeedsDefaults ? Scope.ReadUp<TextTheme>().Style : default;
        Scope.Write<TextAppearance>(Scope.Entity) = new TextAppearance(style.WithDefaults(in defaults));
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        DevelopmentCheck.Require(pass.Has<ShapedText>(pass.Node),
            "Text measurement requires TextShapingSystem registered with LayoutSystem.");
        return constraints.Constrain(pass.Read<ShapedText>(pass.Node).Size);
    }
}

internal readonly record struct TextAppearance(TextStyle Style) : IComponent;
