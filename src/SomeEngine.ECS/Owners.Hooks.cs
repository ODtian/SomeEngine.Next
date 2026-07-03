using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Hooks
{
    private object?[] _stores = new object?[8];
    private World _world = null!;

    internal bool Any { get; private set; }

    internal void Bind(World world)
    {
        _world = world;
    }

    internal ComponentHooks<T> View<T>()
        where T : struct, IComponent
    {
        return new ComponentHooks<T>(Store<T>(), Mark);
    }

    internal bool Try<T>(out HookStore<T> store)
        where T : struct, IComponent
    {
        store = null!;
        int componentId = ComponentMetadata<T>.Id;
        if ((uint)componentId >= (uint)_stores.Length ||
            _stores[componentId] is not HookStore<T> existing)
        {
            return false;
        }

        store = existing;
        return true;
    }

    internal bool Try(int componentId, out IHookStore store)
    {
        store = null!;
        if ((uint)componentId >= (uint)_stores.Length ||
            _stores[componentId] is not IHookStore existing)
        {
            return false;
        }

        store = existing;
        return true;
    }

    internal void Add(int componentId, Entity entity, Array column, int row)
    {
        if (Try(componentId, out var store))
            store.RunAdd(new DeferredWorld(_world), entity, column, row);
    }

    internal void Insert(int componentId, Entity entity, Array column, int row)
    {
        if (Try(componentId, out var store))
            store.RunInsert(new DeferredWorld(_world), entity, column, row);
    }

    internal void Replace(int componentId, Entity entity, Array column, int row)
    {
        if (Try(componentId, out var store))
            store.RunReplace(new DeferredWorld(_world), entity, column, row);
    }

    internal void Remove(int componentId, Entity entity, Array column, int row)
    {
        if (Try(componentId, out var store))
            store.RunRemove(new DeferredWorld(_world), entity, column, row);
    }

    internal void Despawn(int componentId, Entity entity, Array column, int row)
    {
        if (Try(componentId, out var store))
            store.RunDespawn(new DeferredWorld(_world), entity, column, row);
    }

    internal void Insert<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (Try<T>(out var store))
            store.RunInsert(new DeferredWorld(_world), entity, in value);
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (Try<T>(out var store))
            store.RunReplace(new DeferredWorld(_world), entity, in value);
    }

    internal void Remove<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (Try<T>(out var store))
            store.RunRemove(new DeferredWorld(_world), entity, in value);
    }

    private void Mark()
    {
        Any = true;
    }

    private HookStore<T> Store<T>()
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        ArrayGrowthExtensions.EnsureCapacity(ref _stores, componentId + 1, 8);
        if (_stores[componentId] is HookStore<T> existing)
            return existing;

        var store = new HookStore<T>();
        _stores[componentId] = store;
        return store;
    }
}


