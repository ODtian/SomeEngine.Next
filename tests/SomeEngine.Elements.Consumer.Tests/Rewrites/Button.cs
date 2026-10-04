using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's button built entirely from the same public data and functions as library controls.</summary>
public record struct Button(string Label) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Button, Label);
        PressableFunctions.Pressable(Scope.Entity);
        ButtonTheme theme = Scope.ReadUp<ButtonTheme>();
        Hover hover = Scope.Read<Hover>(Scope.Entity);
        Color fill = theme.Fill;
        if (hover.Hovered)
            fill = theme.HoverFill;
        if (hover.Pressed)
            fill = theme.PressedFill;
        Entity row = Row();
        Scope.Write<Box>(row).Padding = theme.Padding;
        row.Background(fill).Corner(theme.CornerRadius);
        using (row.Content())
            Text(Label).TextColor(theme.TextColor);
        return default;
    }
}
