using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class PressableFunctions
{
    /// <summary>Declares pointer data while preserving existing hover and click values.</summary>
    public static Entity Pressable(Entity node)
    {
        Scope.Ensure<Hover>(node);
        Scope.Ensure<Clicks>(node);
        Scope.Ensure<Pressable>(node);
        return node;
    }

    /// <summary>Consumes clicks since this node's last read and depends only on Clicks.</summary>
    public static bool Clicked(this Entity node)
    {
        uint count = Scope.Read<Clicks>(node).Count;
        ref ClickSeen seen = ref Scope.Write<ClickSeen>(node);
        bool clicked = count != seen.Count;
        seen.Count = count;
        return clicked;
    }
}
