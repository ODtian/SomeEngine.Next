using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

internal abstract class ComponentCopy
{
    internal abstract ref byte Bytes { get; }
    internal abstract void Commit(World world, ChildChanges changes);
    internal abstract void CommitAnimations(World world);
    internal abstract void RetainAnimations();
    internal abstract void RestoreAnimations(World world);
    internal abstract void CompleteAnimations();
}

internal sealed class ComponentCopy<T>(Entity entity, T value) : ComponentCopy where T : struct
{
    internal T Value = value;
    private bool _animationsCommitted;
    private bool _animationsRestored;
    private int _animationBuilds = 1;
    private T _previousTarget;
    internal override ref byte Bytes => ref Unsafe.As<T, byte>(ref Value);

    internal static bool HasComponent(World world, Entity entity) => world.HasComponentData<T>(entity);

    internal override void Commit(World world, ChildChanges changes)
    {
        bool exists = HasComponent(world, entity);
        T presented = Value;
        if (AnimatedFields.PreservePresentation(world, entity, ref presented))
            changes.WriteAnimation(this);
        if (exists && EqualityComparer<T>.Default.Equals(world.ReadComponentData<T>(entity), presented))
            return;
        changes.RememberComponent<T>(world, entity);
        if (exists)
            world.ReplaceComponentData(entity, in presented);
        else
            world.AddComponentData(entity, in presented);
    }

    internal override void CommitAnimations(World world)
    {
        if (_animationsCommitted) return;
        _previousTarget = world.ReadComponentData<T>(entity);
        AnimatedFields.RestoreBase(world, entity, ref _previousTarget);
        AnimatedFields.SubmitTargets(world, entity, ref Value);
        _animationsCommitted = true;
    }

    internal override void RetainAnimations() => _animationBuilds++;

    internal override void RestoreAnimations(World world)
    {
        if (_animationsCommitted && !_animationsRestored && world.IsAlive(entity) && HasComponent(world, entity))
            AnimatedFields.SubmitTargets(world, entity, ref _previousTarget);
        _animationsRestored = true;
        CompleteAnimations();
    }

    internal override void CompleteAnimations()
    {
        if (--_animationBuilds == 0) _previousTarget = default;
    }
}
