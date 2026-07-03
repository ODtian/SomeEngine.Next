using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hooks;

public delegate void HookAction<T>(DeferredWorld world, Entity entity, in T value)
    where T : struct, IComponent;

public sealed class ComponentHooks<T>
    where T : struct, IComponent
{
    private readonly HookStore<T> _store;
    private readonly Action _markHook;

    internal ComponentHooks(HookStore<T> store, Action markHook)
    {
        _store = store;
        _markHook = markHook;
    }

    public ComponentHooks<T> OnAdd(HookAction<T> hook)
    {
        _store.BindAdd(hook);
        _markHook();
        return this;
    }

    public ComponentHooks<T> OnInsert(HookAction<T> hook)
    {
        _store.BindInsert(hook);
        _markHook();
        return this;
    }

    public ComponentHooks<T> OnReplace(HookAction<T> hook)
    {
        _store.BindReplace(hook);
        _markHook();
        return this;
    }

    public ComponentHooks<T> OnRemove(HookAction<T> hook)
    {
        _store.BindRemove(hook);
        _markHook();
        return this;
    }

    public ComponentHooks<T> OnDespawn(HookAction<T> hook)
    {
        _store.BindDespawn(hook);
        _markHook();
        return this;
    }
}

