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

internal sealed class Sparse
{
    internal object?[] Stores = new object?[8];
    private Entities _entities = null!;
    private Journal _journal = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;

    internal void Bind(
        Entities entities,
        Journal journal,
        Clock clock,
        Iteration iteration)
    {
        _entities = entities;
        _journal = journal;
        _clock = clock;
        _iteration = iteration;
    }

    internal void Add<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);
        Set<T>().Add(entity, value);
        Write(SerializationChangeKind.SparseAdded, entity, ComponentMetadata<T>.Id);
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);
        Set<T>().Replace(entity, value);
        Write(SerializationChangeKind.SparseChanged, entity, ComponentMetadata<T>.Id);
    }

    internal void Remove<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);
        Set<T>().Remove(entity);
        Write(SerializationChangeKind.SparseRemoved, entity, ComponentMetadata<T>.Id);
    }

    internal ref T Get<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        _entities.ThrowDead(entity);
        Write(SerializationChangeKind.SparseChanged, entity, ComponentMetadata<T>.Id);
        return ref Set<T>().Get(entity);
    }

    internal SparseSet<T> Set<T>()
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        ArrayGrowthExtensions.EnsureCapacity(ref Stores, componentId + 1, 8);
        if (Stores[componentId] is SparseSet<T> existing)
            return existing;

        var sparseSet = new SparseSet<T>();
        Stores[componentId] = sparseSet;
        return sparseSet;
    }

    internal bool Has<T>(Entity entity)
        where T : struct
    {
        if (!_entities.Store.IsAlive(entity))
            return false;

        return HasValue<T>(entity);
    }

    internal bool HasValue<T>(Entity entity)
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        return componentId < Stores.Length &&
            Stores[componentId] is SparseSet<T> sparseSet &&
            sparseSet.Has(entity);
    }

    internal void Copy(Entity source, Entity target)
    {
        for (int componentId = 0; componentId < Stores.Length; componentId++)
        {
            if (Stores[componentId] is not ISparseSet sparseSet)
                continue;

            if (sparseSet.Has(source))
            {
                bool targetHadValue = sparseSet.Has(target);
                if (targetHadValue)
                    sparseSet.ReplaceCopy(source, target);
                else
                    sparseSet.AddCopy(source, target);

                Write(
                    targetHadValue
                        ? SerializationChangeKind.SparseChanged
                        : SerializationChangeKind.SparseAdded,
                    target,
                    componentId);
                continue;
            }

            if (sparseSet.RemoveOptional(target))
                Write(SerializationChangeKind.SparseRemoved, target, componentId);
        }
    }

    internal void Reset()
    {
        Array.Clear(Stores);
    }

    private void Write(
        SerializationChangeKind kind,
        Entity entity,
        int componentId)
    {
        _journal.Write(kind, entity, componentId, default, _clock.Tick);
    }
}

