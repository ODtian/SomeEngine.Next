using SomeEngine.Animation;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

public static class AnimateModifiers
{
    public static Entity Animate<T, TOps, TParameters, TState, TEvaluator>(this Entity node, Channel<T> channel,
        Spec<T, TOps, TParameters, TState, TEvaluator> spec)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        Scope.Animate(node, channel, spec);
        return node;
    }

    /// <summary>Animates subsequent caller writes to this field on the interface clock.</summary>
    public static Entity Animate<T>(this Entity node, Channel<T> channel, Spec spec) where T : unmanaged
    {
        Scope.Animate(node, channel, spec);
        return node;
    }
}
