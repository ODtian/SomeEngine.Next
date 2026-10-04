using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>A scrollable list whose caller declares rows from Visible.</summary>
public record struct VirtualList(float RowHeight) : IView, ISparseComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        DevelopmentCheck.Require(float.IsFinite(RowHeight) && RowHeight > 0,
            "VirtualList.RowHeight requires a finite value greater than zero.");
        ScrollableFunctions.Scrollable(Scope.Entity);
        Scope.Ensure<ListContent>(Scope.Entity);
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        ListContent content = pass.Read<ListContent>(pass.Node);
        var available = new Constraints(new Size(0, RowHeight), new Size(float.PositiveInfinity, RowHeight));
        float width = 0;
        for (int index = 0; index < pass.Children.Count; index++)
        {
            var child = pass.Children[index];
            Size size = pass.Measure(child, in available);
            pass.Place(child, new Vector2(0, (content.First + index) * RowHeight));
            width = MathF.Max(width, size.Width);
        }
        return constraints.Constrain(new Size(width, content.Count * RowHeight));
    }
}

internal readonly record struct ListContent(int Count, int First) : ISparseComponent;
