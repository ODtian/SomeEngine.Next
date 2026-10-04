using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's title using the public dialog theme and text style fields.</summary>
public record struct DialogTitle(string Text) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        DialogTheme theme = Scope.ReadUp<DialogTheme>();
        Entity label = TextFunctions.Text(Text);
        ref TextStyle style = ref Scope.Write<TextStyle>(label);
        style.Color = theme.TitleColor;
        style.Size = theme.TitleSize;
        return default;
    }
}
