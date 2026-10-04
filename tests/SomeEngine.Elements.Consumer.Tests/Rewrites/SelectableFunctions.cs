using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

public static class SelectableFunctions
{
    public static Entity Selectable(Entity node)
    {
        PressableFunctions.Pressable(node);
        Scope.Ensure<Selected>(node);
        return node;
    }
}
