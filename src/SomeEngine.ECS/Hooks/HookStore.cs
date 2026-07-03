using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Hooks;

internal interface IHookStore
{
    void RunAdd(DeferredWorld world, Entity entity, Array column, int row);
    void RunInsert(DeferredWorld world, Entity entity, Array column, int row);
    void RunReplace(DeferredWorld world, Entity entity, Array column, int row);
    void RunRemove(DeferredWorld world, Entity entity, Array column, int row);
    void RunDespawn(DeferredWorld world, Entity entity, Array column, int row);
}

internal sealed class HookStore<T> : IHookStore
    where T : struct, IComponent
{
    private HookAction<T>? _add;
    private HookAction<T>? _insert;
    private HookAction<T>? _replace;
    private HookAction<T>? _remove;
    private HookAction<T>? _despawn;

    public void BindAdd(HookAction<T> hook)
    {
        Bind(ref _add, hook, "OnAdd");
    }

    public void BindInsert(HookAction<T> hook)
    {
        Bind(ref _insert, hook, "OnInsert");
    }

    public void BindReplace(HookAction<T> hook)
    {
        Bind(ref _replace, hook, "OnReplace");
    }

    public void BindRemove(HookAction<T> hook)
    {
        Bind(ref _remove, hook, "OnRemove");
    }

    public void BindDespawn(HookAction<T> hook)
    {
        Bind(ref _despawn, hook, "OnDespawn");
    }

    public void RunAdd(DeferredWorld world, Entity entity, in T value)
    {
        _add?.Invoke(world, entity, in value);
    }

    public void RunAdd(DeferredWorld world, Entity entity, Array column, int row)
    {
        ref var value = ref Value(column, row);
        RunAdd(world, entity, in value);
    }

    public void RunInsert(DeferredWorld world, Entity entity, in T value)
    {
        _insert?.Invoke(world, entity, in value);
    }

    public void RunInsert(DeferredWorld world, Entity entity, Array column, int row)
    {
        ref var value = ref Value(column, row);
        RunInsert(world, entity, in value);
    }

    public void RunReplace(DeferredWorld world, Entity entity, in T value)
    {
        _replace?.Invoke(world, entity, in value);
    }

    public void RunReplace(DeferredWorld world, Entity entity, Array column, int row)
    {
        ref var value = ref Value(column, row);
        RunReplace(world, entity, in value);
    }

    public void RunRemove(DeferredWorld world, Entity entity, in T value)
    {
        _remove?.Invoke(world, entity, in value);
    }

    public void RunRemove(DeferredWorld world, Entity entity, Array column, int row)
    {
        ref var value = ref Value(column, row);
        RunRemove(world, entity, in value);
    }

    public void RunDespawn(DeferredWorld world, Entity entity, in T value)
    {
        _despawn?.Invoke(world, entity, in value);
    }

    public void RunDespawn(DeferredWorld world, Entity entity, Array column, int row)
    {
        ref var value = ref Value(column, row);
        RunDespawn(world, entity, in value);
    }

    private static void Bind(ref HookAction<T>? slot, HookAction<T> hook, string name)
    {
        if (slot is not null)
            throw new InvalidOperationException($"{name} is already bound for {typeof(T).Name}.");

        slot = hook ?? throw new ArgumentNullException(nameof(hook));
    }

    private static ref T Value(Array column, int row)
    {
        return ref Unsafe.As<T[]>(column)[row];
    }
}

