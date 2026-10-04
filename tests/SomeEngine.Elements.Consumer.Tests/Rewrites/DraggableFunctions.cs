using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

public static class DraggableFunctions
{
    public static Entity Draggable(Entity node)
    {
        PressableFunctions.Pressable(node);
        Scope.Ensure<Drag>(node);
        return node;
    }
}
