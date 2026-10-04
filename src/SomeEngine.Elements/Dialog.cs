using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>A themed vertical container whose parts are declared by its caller.</summary>
public record struct Dialog() : IView, ISparseComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        DialogTheme theme = Scope.ReadUp<DialogTheme>();
        Scope.Entity.Background(theme.Fill).Corner(theme.CornerRadius).Shadow(theme.Shadow);
        Scope.Write<Flex>(Scope.Entity) = new Flex(Axis.Vertical, theme.Gap);
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        Flex layout = pass.Read<Flex>(pass.Node);
        return layout.Measure(ref pass, in constraints);
    }
}
