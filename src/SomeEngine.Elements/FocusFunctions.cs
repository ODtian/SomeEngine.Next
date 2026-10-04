using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class FocusFunctions
{
    /// <summary>Declares navigation and click data while preserving existing values.</summary>
    public static Entity Focusable(Entity node)
    {
        Scope.Ensure<Focusable>(node);
        Scope.Ensure<Clicks>(node);
        return node;
    }

    /// <summary>Reads focus and records its presence as a dependency of the caller.</summary>
    public static bool Focused(this Entity node) => Scope.Has<Focused>(node);
}
