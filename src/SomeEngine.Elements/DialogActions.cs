using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>A right-aligned horizontal container for caller-declared dialog actions.</summary>
public record struct DialogActions() : IView, ISparseComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        DialogTheme theme = Scope.ReadUp<DialogTheme>();
        Scope.Write<Flex>(Scope.Entity) = new Flex(Axis.Horizontal, theme.ActionsGap, Align.End, Align.Center);
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        Flex layout = pass.Read<Flex>(pass.Node);
        return layout.Measure(ref pass, in constraints);
    }
}
