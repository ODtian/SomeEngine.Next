using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Hooks;

internal interface IHookStore
{
    bool HasAddCallbacks { get; }
    bool HasInsertCallbacks { get; }
    bool HasReplaceCallbacks { get; }
    bool HasRemoveCallbacks { get; }
    bool HasDespawnCallbacks { get; }

    void RunAdd(DeferredWorld world, Entity entity, ref byte value);
    void RunInsert(DeferredWorld world, Entity entity, ref byte value);
    void RunReplace(DeferredWorld world, Entity entity, ref byte value);
    void RunRemove(DeferredWorld world, Entity entity, ref byte value);
    void RunDespawn(DeferredWorld world, Entity entity, ref byte value);
}

internal sealed class HookStore<T> : IHookStore
    where T : struct, IComponent
{
    private HookAction<T>? _add;
    private HookAction<T>? _insert;
    private HookAction<T>? _replace;
    private HookAction<T>? _remove;
    private HookAction<T>? _despawn;

    public bool HasAddCallbacks => _add is not null;

    public bool HasInsertCallbacks => _insert is not null;

    public bool HasReplaceCallbacks => _replace is not null;

    public bool HasRemoveCallbacks => _remove is not null;

    public bool HasDespawnCallbacks => _despawn is not null;

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

    public void RunAdd(DeferredWorld world, Entity entity, ref byte value)
    {
        RunAdd(world, entity, in Value(ref value));
    }

    public void RunInsert(DeferredWorld world, Entity entity, in T value)
    {
        _insert?.Invoke(world, entity, in value);
    }

    public void RunInsert(DeferredWorld world, Entity entity, ref byte value)
    {
        RunInsert(world, entity, in Value(ref value));
    }

    public void RunReplace(DeferredWorld world, Entity entity, in T value)
    {
        _replace?.Invoke(world, entity, in value);
    }

    public void RunReplace(DeferredWorld world, Entity entity, ref byte value)
    {
        RunReplace(world, entity, in Value(ref value));
    }

    public void RunRemove(DeferredWorld world, Entity entity, in T value)
    {
        _remove?.Invoke(world, entity, in value);
    }

    public void RunRemove(DeferredWorld world, Entity entity, ref byte value)
    {
        RunRemove(world, entity, in Value(ref value));
    }

    public void RunDespawn(DeferredWorld world, Entity entity, in T value)
    {
        _despawn?.Invoke(world, entity, in value);
    }

    public void RunDespawn(DeferredWorld world, Entity entity, ref byte value)
    {
        RunDespawn(world, entity, in Value(ref value));
    }

    private static void Bind(ref HookAction<T>? slot, HookAction<T> hook, string name)
    {
        if (slot is not null)
            throw new InvalidOperationException($"{name} is already bound for {typeof(T).Name}.");

        slot = hook ?? throw new ArgumentNullException(nameof(hook));
    }

    private static ref T Value(ref byte value) =>
        ref Unsafe.As<byte, T>(ref value);
}

