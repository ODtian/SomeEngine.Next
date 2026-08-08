using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Collections;

namespace SomeEngine.ECS;

internal interface ISharedComponentStore
{
    ISharedComponentStore CloneExact();
    object BackingIdentity { get; }
    int DetachCount { get; }
}

internal sealed class SharedComponentStore<T> : ISharedComponentStore
    where T : struct
{
    private Generation _generation;
    private int _detachCount;

    private SharedComponentStore(Generation generation)
    {
        _generation = generation;
    }

    internal SharedComponentStore()
    {
        _generation = new Generation(
            new List<T> { default },
            new Dictionary<T, int> { { default, 0 } });
    }

    object ISharedComponentStore.BackingIdentity => BackingIdentity;

    int ISharedComponentStore.DetachCount => DetachCount;

    internal object BackingIdentity => _generation;

    internal int DetachCount => _detachCount;

    internal int ValueCount => _generation.Values.Count;

    internal int ValueCapacity => _generation.Values.Capacity;

    internal int IndexCapacity => _generation.ValueToIndex.EnsureCapacity(0);

    public int GetOrAdd(in T value)
    {
        Generation generation = _generation;
        if (generation.ValueToIndex.TryGetValue(value, out int existing))
            return existing;

        generation = WritableGeneration();
        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(
            generation.ValueToIndex,
            value,
            out bool exists);
        if (exists)
            return index;

        index = generation.Values.Count;
        generation.Values.Add(value);
        return index;
    }

    public bool TryGetIndex(in T value, out int index) =>
        _generation.ValueToIndex.TryGetValue(value, out index);

    public T GetValue(int index) => _generation.Values[index];

    public ref readonly T GetValueRef(int index) =>
        ref CollectionsMarshal.AsSpan(_generation.Values)[index];

    ISharedComponentStore ISharedComponentStore.CloneExact()
    {
        Generation generation = _generation;
        generation.MarkShared();
        return new SharedComponentStore<T>(generation);
    }

    private Generation WritableGeneration()
    {
        Generation generation = _generation;
        if (!generation.IsShared)
            return generation;

        var values = new List<T>(generation.Values.Capacity);
        values.AddRange(generation.Values);

        var valueToIndex = new Dictionary<T, int>(
            generation.ValueToIndex.EnsureCapacity(0),
            generation.ValueToIndex.Comparer);
        foreach (var pair in generation.ValueToIndex)
            valueToIndex.Add(pair.Key, pair.Value);

        generation = new Generation(values, valueToIndex);
        _generation = generation;
        _detachCount++;
        return generation;
    }

    private sealed class Generation
    {
        private int _shared;

        internal Generation(List<T> values, Dictionary<T, int> valueToIndex)
        {
            Values = values;
            ValueToIndex = valueToIndex;
        }

        internal List<T> Values { get; }

        internal Dictionary<T, int> ValueToIndex { get; }

        internal bool IsShared => Volatile.Read(ref _shared) != 0;

        internal void MarkShared() => Volatile.Write(ref _shared, 1);
    }
}

internal sealed class SharedStores
{
    private ISharedComponentStore?[] _stores;

    internal SharedStores()
        : this(new ISharedComponentStore?[8])
    {
    }

    private SharedStores(ISharedComponentStore?[] stores)
    {
        _stores = stores;
    }

    internal int Capacity => _stores.Length;

    internal int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _stores.Length; i++)
            {
                if (_stores[i] is not null)
                    count++;
            }

            return count;
        }
    }

    public SharedComponentStore<T> Store<T>(int componentId) where T : struct
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _stores, componentId + 1, 8);

        var store = _stores[componentId];
        if (store is null)
        {
            store = new SharedComponentStore<T>();
            _stores[componentId] = store;
        }

        return (SharedComponentStore<T>)store;
    }

    public bool TryGetStore<T>(
        int componentId,
        [NotNullWhen(true)] out SharedComponentStore<T>? store)
        where T : struct
    {
        if ((uint)componentId < (uint)_stores.Length && _stores[componentId] is not null)
        {
            store = (SharedComponentStore<T>)_stores[componentId]!;
            return true;
        }

        store = null;
        return false;
    }

    public void Clear() => Array.Clear(_stores);

    internal SharedStores CloneExact()
    {
        var stores = new ISharedComponentStore?[_stores.Length];
        for (int i = 0; i < _stores.Length; i++)
            stores[i] = _stores[i]?.CloneExact();

        return new SharedStores(stores);
    }
}

