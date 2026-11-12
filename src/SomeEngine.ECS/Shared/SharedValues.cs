using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Shared;

internal struct ValueLink
{
    internal uint Hash;
    internal int NextPlusOne;
}

internal abstract class SharedValues(int capacity)
{
    internal readonly SharedGroups Groups = new(capacity);
    internal abstract void Release(World world);
    internal abstract SharedValues Copy(World values, int entityCapacity, SharedValues? target = null);
}

internal sealed class SharedValues<T> : SharedValues where T : struct
{
    internal T[] Values = new T[16];
    internal int Count;
    private ValueLink[] _links = new ValueLink[16];
    private int[] _buckets = new int[16];

    internal SharedValues(int capacity) : base(capacity) => Intern(default);

    internal void ReserveLoadedValues(int capacity)
    {
        if (capacity <= Values.Length) return;
        Array.Resize(ref Values, capacity);
        Array.Resize(ref _links, capacity);
        Groups.ReserveValues(capacity);
    }

    internal void RebuildLoadedValues()
    {
        int capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, Count * 2));
        _buckets = new int[capacity];
        _links = new ValueLink[Values.Length];
        for (int i = 0; i < Count; i++)
        {
            uint hash = (uint)EqualityComparer<T>.Default.GetHashCode(Values[i]);
            int bucket = (int)(hash & (uint)(capacity - 1));
            _links[i] = new() { Hash = hash, NextPlusOne = _buckets[bucket] };
            _buckets[bucket] = i + 1;
        }
        Groups.ReserveValues(Count);
    }

    internal override SharedValues Copy(World values, int entityCapacity, SharedValues? target = null)
    {
        var copy = target as SharedValues<T> ?? new SharedValues<T>(entityCapacity);
        if (copy.Values.Length < Count) Array.Resize(ref copy.Values, Count);
        if (copy._links.Length < Count) Array.Resize(ref copy._links, Count);
        if (copy._buckets.Length < _buckets.Length) Array.Resize(ref copy._buckets, _buckets.Length);
        copy.Count = Count;
        _links.AsSpan(0, Count).CopyTo(copy._links);
        Array.Clear(copy._buckets);
        for (int i = 0; i < Count; i++)
        {
            int bucket = (int)(copy._links[i].Hash & (uint)(copy._buckets.Length - 1));
            copy._links[i].NextPlusOne = copy._buckets[bucket];
            copy._buckets[bucket] = i + 1;
        }
        if (ComponentType<T>.HasValueOperationsType)
            for (int i = 0; i < Count; i++) copy.Values[i] = ComponentType<T>.CloneValue(values, Values[i]);
        else Values.AsSpan(0, Count).CopyTo(copy.Values);
        if (entityCapacity != 0) copy.Groups.ReserveValues(Count);
        return copy;
    }

    internal int Intern(in T value)
    {
        uint hash = (uint)EqualityComparer<T>.Default.GetHashCode(value);
        int bucket = (int)(hash & (uint)(_buckets.Length - 1));
        for (int next = _buckets[bucket]; next != 0; next = _links[next - 1].NextPlusOne)
        {
            int index = next - 1;
            if (_links[index].Hash == hash && EqualityComparer<T>.Default.Equals(Values[index], value)) return index;
        }
        if (Count == Values.Length)
        {
            Array.Resize(ref Values, Values.Length * 2);
            Array.Resize(ref _links, Values.Length);
            Groups.ReserveValues(Values.Length);
        }
        if ((Count + 1) * 4 > _buckets.Length * 3)
        {
            var buckets = new int[_buckets.Length * 2];
            for (int i = 0; i < Count; i++)
            {
                ref ValueLink link = ref _links[i];
                int bucketIndex = (int)(link.Hash & (uint)(buckets.Length - 1));
                link.NextPlusOne = buckets[bucketIndex];
                buckets[bucketIndex] = i + 1;
            }
            _buckets = buckets;
            bucket = (int)(hash & (uint)(buckets.Length - 1));
        }
        int added = Count++;
        Values[added] = value;
        _links[added] = new() { Hash = hash, NextPlusOne = _buckets[bucket] };
        _buckets[bucket] = added + 1;
        return added;
    }

    internal override void Release(World world)
    {
        Groups.Clear();
        if (ComponentType<T>.HasValueOperationsType)
            for (int i = 0; i < Count; i++) ComponentType<T>.ReleaseValue(world, ref Values[i]);
        if (System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(Values, 0, Count);
    }
}
