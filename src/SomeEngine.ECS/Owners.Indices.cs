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

internal sealed class Indices
{
    private readonly object _gate = new();
    private object?[] _stores = new object?[8];
    private bool[]? _dirty;
    private int _count;
    private int _building;

    internal bool Any =>
        Volatile.Read(ref _count) != 0 ||
        Volatile.Read(ref _building) != 0;

    internal bool HasStore(int componentId)
    {
        lock (_gate)
        {
            return (uint)componentId < (uint)_stores.Length &&
                _stores[componentId] is IIndexStore;
        }
    }

    internal bool RequiresBackfill(int componentId)
    {
        lock (_gate)
        {
            return (uint)componentId >= (uint)_stores.Length ||
                _stores[componentId] is not IIndexStore ||
                IsDirty(componentId);
        }
    }

    internal object? StoreBackingIdentity(int componentId)
    {
        lock (_gate)
        {
            return (uint)componentId < (uint)_stores.Length &&
                _stores[componentId] is IIndexStore store
                ? store.BackingIdentity
                : null;
        }
    }

    internal int StoreDetachCount(int componentId)
    {
        lock (_gate)
        {
            return (uint)componentId < (uint)_stores.Length &&
                _stores[componentId] is IIndexStore store
                ? store.DetachCount
                : 0;
        }
    }

    internal ReadOnlySpan<Entity> Get<TComponent, TKey>(
        TKey key,
        ReadOnlySpan<Archetype> archetypes)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        lock (_gate)
        {
            int componentId = ComponentMetadata<TComponent>.Id;
            var index = Index<TComponent, TKey>(componentId, archetypes);
            if (IsDirty(componentId))
            {
                index = Backfill<TComponent, TKey>(archetypes);
                _stores[componentId] = index;
                _dirty![componentId] = false;
            }

            // ComponentIndex returns a span over an immutable Entity[]. The
            // metadata lock can therefore be released without unpinning the
            // generation observed by this caller.
            return index.Get(key);
        }
    }

    internal void Dirty<T>()
        where T : struct
    {
        if (ComponentMetadata<T>.IsIndexed)
            Dirty(ComponentMetadata<T>.Id);
    }

    internal void Dirty(int componentId)
    {
        if (!ComponentRegistry.Get(componentId).IsIndexed || !Any)
            return;

        lock (_gate)
        {
            if ((uint)componentId >= (uint)_stores.Length ||
                _stores[componentId] is null)
            {
                return;
            }

            ArrayGrowthExtensions.EnsureCapacity(ref _dirty, componentId + 1, 8);
            _dirty[componentId] = true;
        }
    }

    internal void Fix<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (!ComponentMetadata<T>.IsIndexed || !Any)
            return;

        int componentId = ComponentMetadata<T>.Id;
        lock (_gate)
        {
            if (IsDirty(componentId))
                return;

            if (Try(componentId, out IIndex<T> index))
                index.Add(entity, in value);
        }
    }

    internal void Fix<T>(Entity entity, in T oldValue, in T newValue)
        where T : struct, IComponent
    {
        if (!ComponentMetadata<T>.IsIndexed || !Any)
            return;

        int componentId = ComponentMetadata<T>.Id;
        lock (_gate)
        {
            if (IsDirty(componentId))
                return;

            if (Try(componentId, out IIndex<T> index))
                index.Replace(entity, in oldValue, in newValue);
        }
    }

    internal void Drop<T>(Entity entity, in T oldValue)
        where T : struct, IComponent
    {
        if (!ComponentMetadata<T>.IsIndexed || !Any)
            return;

        int componentId = ComponentMetadata<T>.Id;
        lock (_gate)
        {
            if (IsDirty(componentId))
                return;

            if (Try(componentId, out IIndex<T> index))
                index.Remove(entity, in oldValue);
        }
    }

    internal void Fix(
        Entity entity,
        int componentId,
        Chunk chunk,
        int column,
        int row)
    {
        if (!ComponentRegistry.Get(componentId).IsIndexed || !Any)
            return;

        lock (_gate)
        {
            if (IsDirty(componentId))
                return;

            if (Try(componentId, out IIndexStore index))
            {
                ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
                ref byte value = ref chunk.ComponentRowReference(
                    column,
                    row,
                    in info.Operations);
                index.AddAt(entity, ref value);
            }
        }
    }

    internal void Drop(
        Entity entity,
        int componentId,
        Chunk chunk,
        int column,
        int row)
    {
        if (!ComponentRegistry.Get(componentId).IsIndexed || !Any)
            return;

        lock (_gate)
        {
            if (IsDirty(componentId))
                return;

            if (Try(componentId, out IIndexStore index))
            {
                ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
                ref byte value = ref chunk.ComponentRowReference(
                    column,
                    row,
                    in info.Operations);
                index.RemoveAt(entity, ref value);
            }
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            foreach (var index in _stores)
            {
                if (index is IResettableIndex resettable)
                    resettable.Clear();
            }

            _dirty?.AsSpan().Clear();
        }
    }

    /// <summary>
    /// Clones the index owner metadata and creates a cheap wrapper for each typed store. Typed
    /// stores retain immutable shared generations and copy their buckets only before first write.
    /// No index build may be in flight because its table scan would not have a single atomic
    /// source generation to retain.
    /// </summary>
    internal Indices CloneDetached()
    {
        lock (_gate)
        {
            if (_building != 0)
            {
                throw new InvalidOperationException(
                    "Cannot clone World indices while an index backfill is in progress.");
            }

            var clone = new Indices
            {
                _stores = new object?[_stores.Length],
                _dirty = _dirty is null ? null : (bool[])_dirty.Clone(),
                _count = _count,
            };

            for (int i = 0; i < _stores.Length; i++)
            {
                if (_stores[i] is IIndexStore index)
                    clone._stores[i] = index.CloneDetached();
            }

            return clone;
        }
    }

    private ComponentIndex<TComponent, TKey> Index<TComponent, TKey>(
        int componentId,
        ReadOnlySpan<Archetype> archetypes)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _stores, componentId + 1, 8);
        if (_stores[componentId] is ComponentIndex<TComponent, TKey> existing)
            return existing;

        Volatile.Write(ref _building, 1);
        try
        {
            var index = Backfill<TComponent, TKey>(archetypes);
            _stores[componentId] = index;
            Volatile.Write(ref _count, _count + 1);
            return index;
        }
        finally
        {
            Volatile.Write(ref _building, 0);
        }
    }

    private static ComponentIndex<TComponent, TKey> Backfill<TComponent, TKey>(
        ReadOnlySpan<Archetype> archetypes)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var builder = new ComponentIndex<TComponent, TKey>.Builder();
        int componentId = ComponentMetadata<TComponent>.Id;
        foreach (var archetype in archetypes)
        {
            if (!archetype.TryColumn(componentId, out int columnIndex))
                continue;

            foreach (var chunk in archetype.Chunks)
            {
                for (int row = 0; row < chunk.Count; row++)
                    builder.Add(chunk.Entities[row], chunk.ReadComponent<TComponent>(columnIndex, row));
            }
        }

        return builder.Build();
    }

    private bool IsDirty(int componentId)
    {
        return _dirty is not null &&
            (uint)componentId < (uint)_dirty.Length &&
            _dirty[componentId];
    }

    private bool Try<T>(int componentId, out IIndex<T> index)
        where T : struct, IComponent
    {
        index = null!;
        if ((uint)componentId >= (uint)_stores.Length ||
            _stores[componentId] is not IIndex<T> existing)
        {
            return false;
        }

        index = existing;
        return true;
    }

    private bool Try(int componentId, out IIndexStore index)
    {
        index = null!;
        if ((uint)componentId >= (uint)_stores.Length ||
            _stores[componentId] is not IIndexStore existing)
        {
            return false;
        }

        index = existing;
        return true;
    }
}


