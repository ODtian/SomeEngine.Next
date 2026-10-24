using SomeEngine.ECS.Components;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>A title styled by the nearest dialog theme and inherited text theme.</summary>
public record struct DialogTitle(string Text) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Heading, Text);
        DialogTheme theme = Scope.ReadUp<DialogTheme>();
        TextFunctions.Text(Text).FontSize(theme.TitleSize).TextColor(theme.TitleColor);
        return default;
    }
}
