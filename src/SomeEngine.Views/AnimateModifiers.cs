using SomeEngine.Animation;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

public static class AnimateModifiers
{
    /// <summary>Animates subsequent caller writes to this field on the interface clock.</summary>
    public static Entity Animate<T>(this Entity node, Channel<T> channel, Spec spec) where T : unmanaged
    {
        Scope.Animate(node, channel, spec);
        return node;
    }
}
