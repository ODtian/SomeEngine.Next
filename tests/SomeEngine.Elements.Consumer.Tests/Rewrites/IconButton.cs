using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's button with a text glyph beside its label, sharing button theme and pointer data.</summary>
public record struct IconButton(string Glyph, string Label) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        PressableFunctions.Pressable(Scope.Entity);
        ButtonTheme theme = Scope.ReadUp<ButtonTheme>();
        Hover hover = Scope.Read<Hover>(Scope.Entity);
        Color fill = hover.Pressed ? theme.PressedFill : hover.Hovered ? theme.HoverFill : theme.Fill;
        Entity row = Row(4).Background(fill).Corner(theme.CornerRadius);
        Scope.Write<Box>(row).Padding = theme.Padding;
        using (row.Content())
        {
            Text(Glyph).TextColor(theme.TextColor);
            Text(Label).TextColor(theme.TextColor);
        }
        return default;
    }
}
