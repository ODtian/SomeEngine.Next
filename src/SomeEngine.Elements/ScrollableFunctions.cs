using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class ScrollableFunctions
{
    /// <summary>Declares wheel and drag scrolling while preserving the content offset.</summary>
    public static Entity Scrollable(Entity node)
    {
        DraggableFunctions.Draggable(node);
        Scope.Ensure<ScrollInput>(node);
        Scope.Ensure<ScrollOffset>(node);
        return node;
    }
}
