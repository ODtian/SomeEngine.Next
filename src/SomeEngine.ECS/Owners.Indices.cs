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
    internal object?[] Stores = new object?[8];
    private bool[]? _dirty;
    private int _count;

    internal bool Any => _count != 0;

    internal ReadOnlySpan<Entity> Get<TComponent, TKey>(
        TKey key,
        IReadOnlyList<Archetype> archetypes)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var index = Index<TComponent, TKey>(archetypes);
        Clean(ComponentMetadata<TComponent>.Id, index, archetypes);
        return index.Get(key);
    }

    internal void Dirty<T>()
        where T : struct
    {
        if (ComponentMetadata<T>.IsIndexed)
            Dirty(ComponentMetadata<T>.Id);
    }

    internal void Dirty(int componentId)
    {
        if (_count == 0 ||
            (uint)componentId >= (uint)Stores.Length ||
            Stores[componentId] is null)
        {
            return;
        }

        ArrayGrowthExtensions.EnsureCapacity(ref _dirty, componentId + 1, 8);
        _dirty[componentId] = true;
    }

    internal void Fix<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (IsDirty(componentId))
            return;

        if (Try(componentId, out IIndex<T> index))
            index.Add(entity, in value);
    }

    internal void Fix<T>(Entity entity, in T oldValue, in T newValue)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (IsDirty(componentId))
            return;

        if (Try(componentId, out IIndex<T> index))
            index.Replace(entity, in oldValue, in newValue);
    }

    internal void Drop<T>(Entity entity, in T oldValue)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (IsDirty(componentId))
            return;

        if (Try(componentId, out IIndex<T> index))
            index.Remove(entity, in oldValue);
    }

    internal void Fix(Entity entity, int componentId, Array column, int row)
    {
        if (IsDirty(componentId))
            return;

        if (Try(componentId, out IIndexStore index))
            index.AddAt(entity, column, row);
    }

    internal void Drop(Entity entity, int componentId, Array column, int row)
    {
        if (IsDirty(componentId))
            return;

        if (Try(componentId, out IIndexStore index))
            index.RemoveAt(entity, column, row);
    }

    internal void Reset()
    {
        foreach (var index in Stores)
        {
            if (index is IResettableIndex resettable)
                resettable.Clear();
        }

        _dirty?.AsSpan().Clear();
    }

    private ComponentIndex<TComponent, TKey> Index<TComponent, TKey>(
        IReadOnlyList<Archetype> archetypes)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        int componentId = ComponentMetadata<TComponent>.Id;
        ArrayGrowthExtensions.EnsureCapacity(ref Stores, componentId + 1, 8);
        if (Stores[componentId] is ComponentIndex<TComponent, TKey> existing)
            return existing;

        var index = new ComponentIndex<TComponent, TKey>();
        Backfill(index, archetypes);
        Stores[componentId] = index;
        _count++;
        return index;
    }

    private static void Backfill<TComponent, TKey>(
        ComponentIndex<TComponent, TKey> index,
        IReadOnlyList<Archetype> archetypes)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        int componentId = ComponentMetadata<TComponent>.Id;
        foreach (var archetype in archetypes)
        {
            if (!archetype.TryColumn(componentId, out int columnIndex))
                continue;

            foreach (var chunk in archetype.Chunks)
            {
                for (int row = 0; row < chunk.Count; row++)
                    index.Add(chunk.Entities[row], chunk.ReadComponent<TComponent>(columnIndex, row));
            }
        }
    }

    private void Clean<TComponent, TKey>(
        int componentId,
        ComponentIndex<TComponent, TKey> index,
        IReadOnlyList<Archetype> archetypes)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        if (!IsDirty(componentId))
            return;

        index.Clear();
        Backfill(index, archetypes);
        _dirty![componentId] = false;
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
        if ((uint)componentId >= (uint)Stores.Length ||
            Stores[componentId] is not IIndex<T> existing)
        {
            return false;
        }

        index = existing;
        return true;
    }

    private bool Try(int componentId, out IIndexStore index)
    {
        index = null!;
        if ((uint)componentId >= (uint)Stores.Length ||
            Stores[componentId] is not IIndexStore existing)
        {
            return false;
        }

        index = existing;
        return true;
    }
}


