using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's right-aligned footer accepting caller content and sharing the dialog theme.</summary>
public record struct DialogFooter() : IView, ISparseComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        DialogTheme theme = Scope.ReadUp<DialogTheme>();
        Scope.Write<Flex>(Scope.Entity) = new Flex(Axis.Horizontal, theme.ActionsGap,
            MainAlign: Align.End, CrossAlign: Align.Center);
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        Flex row = pass.Read<Flex>(pass.Node);
        return row.Measure(ref pass, in constraints);
    }
}
