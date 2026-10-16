using SomeEngine.Animation;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

public static partial class Scope
{
    /// <summary>Submits one typed target for each node in the current build segment.</summary>
    public static void Set<T>(ReadOnlySpan<Entity> entities, Channel<T> channel, ReadOnlySpan<T> values) where T : unmanaged
    {
        SomeEngine.Core.Diagnostics.DevelopmentCheck.Require(entities.Length == values.Length,
            "Field targets require one value per entity.");
        for (int index = 0; index < entities.Length; index++)
            Set(entities[index], channel, values[index]);
    }

    /// <summary>Submits a field target while retaining the current presentation until animation writes it.</summary>
    public static void Set<T>(Entity entity, Channel<T> channel, T value) where T : unmanaged
    {
        ScopeData current = Current;
        CheckBuild(current);
        using var untracked = Untrack();
        var copies = current.ViewData.ComponentCopies ??= new();
        if (!channel.ThroughReference && copies.TryGetValue((entity, channel.Component, 0UL), out ComponentCopy? component) && component.Written)
        {
            channel.WriteRoot(ref component.Bytes, in value);
            return;
        }
        var key = (entity, channel.Component, (ulong)channel.Field + 1);
        T previous = AnimatedFields.ReadTarget(current.World, entity, channel);
        if (!copies.TryGetValue(key, out ComponentCopy? copy) || !copy.Written && !copy.CanReuse)
            copies[key] = new FieldCopy<T>(entity, channel, previous, value);
        else
            ((FieldCopy<T>)copy).Set(previous, value);
    }

    internal static void Animate<T, TOps, TParameters, TState, TEvaluator>(Entity entity, Channel<T> channel,
        Spec<T, TOps, TParameters, TState, TEvaluator> spec)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        ScopeData current = Current;
        CheckBuild(current);
        using var untracked = Untrack();
        var copies = current.ViewData.ComponentCopies;
        bool hasCopy = copies is not null &&
            copies.TryGetValue((entity, channel.Component, 0UL), out var copy) && copy.Written;
        T value = hasCopy ? channel.ReadRoot(in copies![(entity, channel.Component, 0UL)].Bytes) : channel.Read(current.World, entity);
        AnimatedFields.Declare(current.World, entity, channel, spec, value, hasValue: channel.ThroughReference || !hasCopy);
    }

    internal static void Animate<T>(Entity entity, Channel<T> channel, Spec spec) where T : unmanaged
    {
        ScopeData current = Current;
        using var untracked = Untrack();
        var copies = current.ViewData.ComponentCopies;
        bool hasCopy = copies is not null && copies.TryGetValue((entity, channel.Component, 0UL), out var copy) && copy.Written;
        T value = hasCopy
            ? channel.ReadRoot(in copies![(entity, channel.Component, 0UL)].Bytes)
            : channel.Read(current.World, entity);
        AnimatedFields.Declare(current.World, entity, channel, spec, value, hasValue: channel.ThroughReference || !hasCopy);
    }
}
