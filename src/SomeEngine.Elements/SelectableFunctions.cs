using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class SelectableFunctions
{
    /// <summary>Declares click selection while preserving the current selection.</summary>
    public static Entity Selectable(Entity node)
    {
        PressableFunctions.Pressable(node);
        Scope.Ensure<Selected>(node);
        return node;
    }
}
