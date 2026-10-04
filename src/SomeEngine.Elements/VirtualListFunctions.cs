using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static partial class VirtualListFunctions
{
    /// <summary>Reads the viewport and offset, records the list extent, and returns buffered row indices.</summary>
    public static VisibleRange Visible(Entity list, int count)
    {
        DevelopmentCheck.Require(Scope.World.HasSparse<VirtualList>(list),
            "Visible requires a VirtualList node in the active World.");
        DevelopmentCheck.Require(count >= 0, "Visible requires an item count of zero or greater.");
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
            int first = (int)Math.Clamp(Math.Floor((double)offset / rowHeight) - 1, 0, count);
            int end = (int)Math.Clamp(Math.Ceiling(((double)offset + height) / rowHeight) + 1, first, count);
            range = new(first, end);
        }
        Scope.Write<ListContent>(list) = new ListContent(count, range.Start);
        return range;
    }
}
