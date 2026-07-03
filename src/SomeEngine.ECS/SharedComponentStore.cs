using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Collections;

namespace SomeEngine.ECS;

internal class SharedComponentStore<T> where T : struct
{
    private readonly List<T> _values = new() { default };
    private readonly Dictionary<T, int> _valueToIndex = new() { { default, 0 } };

    public int GetOrAdd(in T value)
    {
        ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _valueToIndex,
            value,
            out bool exists);
        if (exists)
            return index;

        index = _values.Count;
        _values.Add(value);
        return index;
    }

    public bool TryGetIndex(in T value, out int index) =>
        _valueToIndex.TryGetValue(value, out index);

    public T GetValue(int index) => _values[index];
}

internal class SharedStores
{
    private object?[] _stores = new object?[8];

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
}

