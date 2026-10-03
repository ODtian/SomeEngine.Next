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
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Sparse
{
    private readonly WorldStructureRoot _structure;

    internal Sparse(WorldStructureRoot structure) => _structure = structure;

    private Entities _entities => _structure.Entities;
    private Iteration _iteration => _structure.Iteration;
    private object?[] _stores = new object?[8];
    private readonly Lock _storesGate = new();


    internal void Add<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);
        Set<T>().Add(entity, value, unchecked(_structure.Clock.Acquire() + 1));
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);
        if (!TrySet<T>(out SparseSet<T> sparseSet))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        sparseSet.Replace(entity, value, unchecked(_structure.Clock.Acquire() + 1));
    }

    internal uint WriteVersion<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        _entities.ThrowDead(entity);
        bool hasSet = TrySet<T>(out SparseSet<T> sparseSet);
        DevelopmentCheck.Require(hasSet,
            $"Entity {entity} requires sparse component {typeof(T).Name} to read its write version.");

        return sparseSet.WriteVersion(entity);
    }

    internal void Remove<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);
        if (!TrySet<T>(out SparseSet<T> sparseSet))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        sparseSet.Remove(entity);
    }

    internal T Read<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        _entities.ThrowDead(entity);
        if (!TrySet<T>(out SparseSet<T> sparseSet))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        return sparseSet.Read(entity);
    }

    internal ref readonly T ReadRef<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        _entities.ThrowDead(entity);
        if (!TrySet<T>(out SparseSet<T> sparseSet))
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        return ref sparseSet.ReadRef(entity);
    }

    internal SparseSet<T> Set<T>()
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        object?[] stores = Volatile.Read(ref _stores);
        if (componentId < stores.Length && stores[componentId] is SparseSet<T> existing)
            return existing;

        lock (_storesGate)
        {
            stores = _stores;
            ArrayGrowthExtensions.EnsureCapacity(ref stores, componentId + 1, 8);
            if (!ReferenceEquals(stores, _stores))
                Volatile.Write(ref _stores, stores);
            if (stores[componentId] is SparseSet<T> published)
                return published;

            var sparseSet = new SparseSet<T>();
            stores[componentId] = sparseSet;
            return sparseSet;
        }
    }

    internal bool TrySet<T>(out SparseSet<T> sparseSet)
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        object?[] stores = Volatile.Read(ref _stores);
        if (componentId < stores.Length && stores[componentId] is SparseSet<T> existing)
        {
            sparseSet = existing;
            return true;
        }

        sparseSet = null!;
        return false;
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
        return TrySet<T>(out SparseSet<T> sparseSet) && sparseSet.Has(entity);
    }

    internal bool HasValue(int componentId, Entity entity)
    {
        object?[] stores = Volatile.Read(ref _stores);
        return (uint)componentId < (uint)stores.Length &&
               stores[componentId] is ISparseSet sparseSet &&
               sparseSet.Has(entity);
    }

    internal void Copy(Entity source, Entity target)
    {
        object?[] stores = Volatile.Read(ref _stores);
        for (int componentId = 0; componentId < stores.Length; componentId++)
        {
            if (stores[componentId] is not ISparseSet sparseSet)
                continue;

            if (sparseSet.Has(source))
            {
                bool targetHadValue = sparseSet.Has(target);
                if (targetHadValue)
                    sparseSet.ReplaceCopy(source, target, _structure.Clock.Tick);
                else
                    sparseSet.AddCopy(source, target, _structure.Clock.Tick);

                continue;
            }

            sparseSet.RemoveOptional(target);
        }
    }

    /// <summary>
    /// Removes every sparse row owned by an entity before its generation is retired. This is
    /// required even though stale generations no longer pass Has(): leaving the dense row behind
    /// would leak storage and collide with a later entity that reuses the same index.
    /// </summary>
    internal void RemoveAll(Entity entity)
    {
        object?[] stores = Volatile.Read(ref _stores);
        for (int componentId = 0; componentId < stores.Length; componentId++)
        {
            if (stores[componentId] is not ISparseSet sparseSet ||
                !sparseSet.RemoveOptional(entity))
            {
                continue;
            }

        }
    }

    internal void Reset()
    {
        lock (_storesGate)
            Array.Clear(_stores);
    }

    /// <summary>
    /// Creates a detached exact image of every typed sparse store. Runtime bindings are
    /// intentionally absent; the result is a transaction candidate, not another live owner.
    /// </summary>
    internal Sparse CloneDetached(WorldStructureRoot structure)
    {
        object?[] stores = Volatile.Read(ref _stores);
        var clone = new Sparse(structure)
        {
            _stores = new object?[stores.Length],
        };
        for (int componentId = 0; componentId < stores.Length; componentId++)
        {
            if (stores[componentId] is ISparseSet sparseSet)
                clone._stores[componentId] = sparseSet.CloneDetached();
        }

        return clone;
    }

}

