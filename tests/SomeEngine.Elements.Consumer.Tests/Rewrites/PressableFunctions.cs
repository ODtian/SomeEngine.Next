using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user's pressable behavior composed from public components and scope functions.</summary>
public static class PressableFunctions
{
    public static Entity Pressable(Entity node)
    {
        Scope.Ensure<Pressable>(node);
        Scope.Ensure<Clicks>(node);
        Scope.Ensure<Hover>(node);
        return node;
    }

    public static bool Clicked(Entity node)
    {
        ref ClickSeen read = ref Scope.Write<ClickSeen>(node);
        uint clicks = Scope.Read<Clicks>(node).Count;
        if (read.Count == clicks)
            return false;
        read.Count = clicks;
        return true;
    }
}
