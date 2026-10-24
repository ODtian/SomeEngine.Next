using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class DraggableFunctions
{
    /// <summary>Declares primary-button dragging while preserving existing drag data.</summary>
    public static Entity Draggable(Entity node)
    {
        PressableFunctions.Pressable(node);
        Scope.Ensure<Drag>(node);
        return node;
    }
}
