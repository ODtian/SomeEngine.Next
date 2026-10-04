using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's focus behavior composed from public components and scope functions.</summary>
public static class FocusFunctions
{
    public static Entity Focusable(Entity node)
    {
        Scope.Ensure<Clicks>(node);
        Scope.Ensure<Focusable>(node);
        return node;
    }

    public static bool Focused(Entity node) => Scope.Has<Focused>(node);
}
