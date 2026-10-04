using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's list using public scope reads, component writes, and layout measurement.</summary>
public record struct VirtualList(float RowHeight) : IView, ISparseComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        DevelopmentCheck.Require(float.IsFinite(RowHeight) && RowHeight > 0,
            "VirtualList.RowHeight requires a finite value greater than zero.");
        ScrollableFunctions.Scrollable(Scope.Entity);
        Scope.Ensure<ListRows>(Scope.Entity);
        return default;
    }

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        ListRows rows = pass.Read<ListRows>(pass.Node);
        var available = new Constraints(new Size(0, RowHeight), new Size(float.PositiveInfinity, RowHeight));
        float width = 0;
        int index = rows.First;
        foreach (Entity child in pass.Children)
        {
            Size size = pass.Measure(child, in available);
            pass.Place(child, new Vector2(0, index++ * RowHeight));
            width = MathF.Max(width, size.Width);
        }
        return constraints.Constrain(new Size(width, rows.Count * RowHeight));
    }
}

internal readonly record struct ListRows(int Count, int First) : ISparseComponent;

public static partial class VirtualListFunctions
{
    public static VisibleRange Visible(Entity list, int count)
    {
        float rowHeight = Scope.Read<VirtualList>(list).RowHeight;
        float offset = Scope.Read<ScrollOffset>(list).Value.Y;
        float height = Scope.Has<LayoutRect>(list) ? Scope.Read<LayoutRect>(list).Value.Height : 0;
        if (Scope.Has<Box>(list))
        {
            Box box = Scope.Read<Box>(list);
            height = MathF.Max(0, height - box.Padding.Top - box.Padding.Bottom);
        }
        VisibleRange range = default;
        if (count > 0 && height > 0)
        {
            offset = Math.Clamp(offset, 0, MathF.Max(0, count * rowHeight - height));
            int start = (int)Math.Clamp(Math.Floor((double)offset / rowHeight) - 1, 0, count);
            int end = (int)Math.Clamp(Math.Ceiling(((double)offset + height) / rowHeight) + 1, start, count);
            range = new(start, end);
        }
        Scope.Write<ListRows>(list) = new ListRows(count, range.Start);
        return range;
    }
}
