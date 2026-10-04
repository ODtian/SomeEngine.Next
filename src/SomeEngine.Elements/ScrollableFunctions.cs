using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class ScrollableFunctions
{
    /// <summary>Requests the smallest scroll that displays a rectangle relative to the content origin.</summary>
    public static Entity ScrollIntoView(this Entity node, in Rect region)
    {
        DevelopmentCheck.Require(Scope.World.Has<ScrollOffset>(node), "ScrollIntoView requires a scroll viewport.");
        DevelopmentCheck.Require(float.IsFinite(region.X) && float.IsFinite(region.Y) &&
            float.IsFinite(region.Width) && region.Width >= 0 && float.IsFinite(region.Height) && region.Height >= 0 &&
            float.IsFinite(region.X + region.Width) && float.IsFinite(region.Y + region.Height),
            "ScrollIntoView requires a finite content rectangle with nonnegative dimensions.");
        ref ScrollTarget target = ref Scope.Write<ScrollTarget>(node);
        target.Region = region;
        target.Count = unchecked(target.Count + 1);
        return node;
    }

    /// <summary>Declares wheel and drag scrolling while preserving the content offset.</summary>
    public static Entity Scrollable(Entity node)
    {
        DraggableFunctions.Draggable(node);
        Scope.Ensure<ScrollInput>(node);
        Scope.Ensure<ScrollOffset>(node);
        return node;
    }
}
