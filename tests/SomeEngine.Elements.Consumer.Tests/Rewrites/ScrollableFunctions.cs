using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

public static class ScrollableFunctions
{
    public static Entity Scrollable(Entity node)
    {
        DraggableFunctions.Draggable(node);
        Scope.Ensure<ScrollInput>(node);
        Scope.Ensure<ScrollOffset>(node);
        return node;
    }
}
