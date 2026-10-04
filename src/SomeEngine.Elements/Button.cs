using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.PressableFunctions;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Elements;

/// <summary>A label and themed surface composed with public pointer, layout, and text APIs.</summary>
public record struct Button(string Label) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Button, Label);
        Pressable(Scope.Entity);
        ButtonTheme theme = Scope.ReadUp<ButtonTheme>();
        Hover hover = Scope.Read<Hover>(Scope.Entity);
        Color fill = hover.Pressed ? theme.PressedFill : hover.Hovered ? theme.HoverFill : theme.Fill;
        Entity row = Row().Background(fill).Corner(theme.CornerRadius);
        Scope.Write<Box>(row).Padding = theme.Padding;
        using (row.Content())
            Text(Label).TextColor(theme.TextColor);
        return default;
    }
}
