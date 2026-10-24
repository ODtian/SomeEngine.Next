using System.Runtime.CompilerServices;
using SomeEngine.Animation;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>A leaf target retained independently of the component's referenced objects.</summary>
internal sealed class FieldCopy<T>(Entity entity, Channel<T> channel, T previous, T value) : ComponentCopy where T : unmanaged
{
    internal T Value = value;
    private T _previous = previous;
    private bool _committed;
    private bool _restored;
    private int _builds;

    internal override ref byte Bytes => ref Unsafe.As<T, byte>(ref Value);
    internal override bool CanReuse => _builds == 0;
    internal override void Commit(World world, ChildChanges changes, bool reuseData) => changes.WriteAnimation(this);

    internal void Set(T previous, T value)
    {
        _previous = previous;
        Value = value;
        Written = true;
    }

    internal override void Clear()
    {
        Written = false;
        if (CanReuse) Reset();
    }

    internal override void CommitAnimations(World world)
    {
        if (_committed) return;
        AnimatedFields.Submit(world, entity, channel, in Value);
        _committed = true;
    }

    internal override void RetainAnimations() => _builds++;

    internal override void RestoreAnimations(World world)
    {
        if (_committed && !_restored && world.IsAlive(entity))
            AnimatedFields.Submit(world, entity, channel, in _previous);
        _restored = true;
        CompleteAnimations();
    }

    internal override void CompleteAnimations()
    {
        if (--_builds == 0)
        {
            _previous = default;
            _committed = false;
            _restored = false;
            if (!Written) Reset();
        }
    }

    private void Reset()
    {
        Value = default;
        _previous = default;
        _committed = false;
        _restored = false;
    }
}
