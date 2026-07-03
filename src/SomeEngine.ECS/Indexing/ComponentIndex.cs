using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Indexing;

internal interface IResettableIndex
{
    void Clear();
}

internal interface IIndexStore : IResettableIndex
{
    void AddAt(Entity entity, Array column, int row);
    void RemoveAt(Entity entity, Array column, int row);
}

internal interface IIndex<T>
    where T : struct, IComponent
{
    void Add(Entity entity, in T value);
    void Replace(Entity entity, in T oldValue, in T newValue);
    void Remove(Entity entity, in T oldValue);
}

internal sealed class ComponentIndex<TComponent, TKey>
    : IIndexStore, IIndex<TComponent>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly Dictionary<TKey, SmallList<Entity>> _buckets = new();

    public void Add(Entity entity, in TComponent component)
    {
        var key = component.GetKey();
        AddBucket(key, entity);
    }

    public void Replace(Entity entity, in TComponent oldValue, in TComponent newValue)
    {
        var oldKey = oldValue.GetKey();
        var newKey = newValue.GetKey();

        if (oldKey.Equals(newKey))
            return;

        RemoveBucket(oldKey, entity);
        AddBucket(newKey, entity);
    }

    public void Remove(Entity entity, in TComponent component)
    {
        RemoveBucket(component.GetKey(), entity);
    }

    public void AddAt(Entity entity, Array column, int row)
    {
        Add(entity, Unsafe.As<TComponent[]>(column)[row]);
    }

    public void RemoveAt(Entity entity, Array column, int row)
    {
        Remove(entity, Unsafe.As<TComponent[]>(column)[row]);
    }

    public ReadOnlySpan<Entity> Get(TKey key)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(_buckets, key);
        return Unsafe.IsNullRef(ref bucket) ? ReadOnlySpan<Entity>.Empty : bucket.AsSpan();
    }

    public void Clear()
    {
        _buckets.Clear();
    }

    private void AddBucket(TKey key, Entity entity)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(_buckets, key, out bool exists);
        if (!exists)
            bucket = default;

        bucket.Add(entity);
    }

    private void RemoveBucket(TKey key, Entity entity)
    {
        ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(_buckets, key);
        if (Unsafe.IsNullRef(ref bucket))
            return;

        if (!bucket.RemoveSwapBack(entity))
            return;

        if (bucket.Count == 0)
            _buckets.Remove(key);
    }
}

