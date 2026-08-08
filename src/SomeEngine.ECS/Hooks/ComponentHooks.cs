using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hooks;

public delegate void HookAction<T>(DeferredWorld world, Entity entity, in T value)
    where T : struct, IComponent;

public sealed class ComponentHooks<T>
    where T : struct, IComponent
{
    private readonly HookStore<T> _store;
    private readonly Action<Action> _mutate;
    private readonly Action _markHook;

    internal ComponentHooks(
        HookStore<T> store,
        Action<Action> mutate,
        Action markHook)
    {
        _store = store;
        _mutate = mutate;
        _markHook = markHook;
    }

    public ComponentHooks<T> OnAdd(HookAction<T> hook)
    {
        _mutate(() =>
        {
            _store.BindAdd(hook);
            _markHook();
        });
        return this;
    }

    public ComponentHooks<T> OnInsert(HookAction<T> hook)
    {
        _mutate(() =>
        {
            _store.BindInsert(hook);
            _markHook();
        });
        return this;
    }

    public ComponentHooks<T> OnReplace(HookAction<T> hook)
    {
        _mutate(() =>
        {
            _store.BindReplace(hook);
            _markHook();
        });
        return this;
    }

    public ComponentHooks<T> OnRemove(HookAction<T> hook)
    {
        _mutate(() =>
        {
            _store.BindRemove(hook);
            _markHook();
        });
        return this;
    }

    public ComponentHooks<T> OnDespawn(HookAction<T> hook)
    {
        _mutate(() =>
        {
            _store.BindDespawn(hook);
            _markHook();
        });
        return this;
    }
}

