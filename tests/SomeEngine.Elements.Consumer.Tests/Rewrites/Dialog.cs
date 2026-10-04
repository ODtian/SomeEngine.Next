using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's vertical dialog composed through public theme, surface, and layout data.</summary>
public record struct Dialog() : IView, ISparseComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Dialog, "");
        DialogTheme theme = Scope.ReadUp<DialogTheme>();
        ref Surface surface = ref Scope.Write<Surface>(Scope.Entity);
        surface.Fill = theme.Fill;
        surface.CornerRadius = theme.CornerRadius;
        surface.Shadow = theme.Shadow;
        Scope.Write<Flex>(Scope.Entity) = new Flex(Axis.Vertical, theme.Gap);
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        Flex column = pass.Read<Flex>(pass.Node);
        return column.Measure(ref pass, in constraints);
    }
}
