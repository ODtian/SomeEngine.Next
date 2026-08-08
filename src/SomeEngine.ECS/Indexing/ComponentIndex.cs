using System.Runtime.CompilerServices;
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
    void AddAt(Entity entity, ref byte value);
    void RemoveAt(Entity entity, ref byte value);
    IIndexStore CloneDetached();
    object BackingIdentity { get; }
    int DetachCount { get; }
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
    private readonly object _gate = new();
    private Generation _generation;
    private int _detachCount;

    private ComponentIndex(Dictionary<TKey, Bucket> buckets)
    {
        _generation = new Generation(buckets);
    }

    private ComponentIndex(Generation generation)
    {
        _generation = generation;
    }

    object IIndexStore.BackingIdentity => BackingIdentity;

    int IIndexStore.DetachCount => DetachCount;

    internal object BackingIdentity
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    internal int DetachCount => Volatile.Read(ref _detachCount);

    public void Add(Entity entity, in TComponent component)
    {
        var key = component.GetKey();
        lock (_gate)
            AddBucket(WritableGeneration().Buckets, key, entity);
    }

    public void Replace(Entity entity, in TComponent oldValue, in TComponent newValue)
    {
        var oldKey = oldValue.GetKey();
        var newKey = newValue.GetKey();

        lock (_gate)
        {
            if (oldKey.Equals(newKey))
                return;

            Dictionary<TKey, Bucket> buckets = WritableGeneration().Buckets;
            RemoveBucket(buckets, oldKey, entity);
            AddBucket(buckets, newKey, entity);
        }
    }

    public void Remove(Entity entity, in TComponent component)
    {
        lock (_gate)
            RemoveBucket(WritableGeneration().Buckets, component.GetKey(), entity);
    }

    public void AddAt(Entity entity, ref byte value)
    {
        Add(entity, in Unsafe.As<byte, TComponent>(ref value));
    }

    public void RemoveAt(Entity entity, ref byte value)
    {
        Remove(entity, in Unsafe.As<byte, TComponent>(ref value));
    }

    public ReadOnlySpan<Entity> Get(TKey key)
    {
        lock (_gate)
        {
            return _generation.Buckets.TryGetValue(key, out Bucket? bucket)
                ? bucket.Publish()
                : ReadOnlySpan<Entity>.Empty;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Generation generation = _generation;
            if (generation.Buckets.Count == 0)
                return;

            _generation = new Generation(
                new Dictionary<TKey, Bucket>(generation.Buckets.Comparer));
            if (generation.IsShared)
                _detachCount++;
        }
    }

    IIndexStore IIndexStore.CloneDetached() => CloneDetached();

    /// <summary>
    /// Creates an exact logical image that retains the current index generation. Both wrappers
    /// treat that generation as immutable and copy its dictionary and buckets before mutation.
    /// </summary>
    internal ComponentIndex<TComponent, TKey> CloneDetached()
    {
        lock (_gate)
        {
            Generation generation = _generation;
            generation.MarkShared();
            return new ComponentIndex<TComponent, TKey>(generation);
        }
    }

    private static void AddBucket(
        Dictionary<TKey, Bucket> buckets,
        TKey key,
        Entity entity)
    {
        if (!buckets.TryGetValue(key, out Bucket? bucket))
        {
            bucket = new Bucket();
            buckets.Add(key, bucket);
        }

        // Index creation can overlap a component commit whose table write has
        // already happened. Treating a repeated fix-up as idempotent keeps the
        // published bucket a set even across that hand-off.
        bucket.Add(entity);
    }

    private static void RemoveBucket(
        Dictionary<TKey, Bucket> buckets,
        TKey key,
        Entity entity)
    {
        if (!buckets.TryGetValue(key, out Bucket? bucket) || !bucket.Remove(entity))
            return;

        if (bucket.Count == 0)
            buckets.Remove(key);
    }

    private Generation WritableGeneration()
    {
        Generation generation = _generation;
        if (!generation.IsShared)
            return generation;

        var buckets = new Dictionary<TKey, Bucket>(
            generation.Buckets.Count,
            generation.Buckets.Comparer);
        foreach (var pair in generation.Buckets)
            buckets.Add(pair.Key, pair.Value.CloneDetached());

        generation = new Generation(buckets);
        _generation = generation;
        _detachCount++;
        return generation;
    }

    /// <summary>
    /// Builds an index store privately. Mutable bucket storage never escapes;
    /// each bucket is frozen only when that generation is first read.
    /// </summary>
    internal sealed class Builder
    {
        private Dictionary<TKey, Bucket>? _buckets = new();

        internal void Add(Entity entity, in TComponent component)
        {
            var buckets = _buckets ?? throw new InvalidOperationException(
                "An index builder cannot be reused after publication.");
            TKey key = component.GetKey();
            if (!buckets.TryGetValue(key, out Bucket? bucket))
            {
                bucket = new Bucket();
                buckets.Add(key, bucket);
            }

            bucket.Add(entity);
        }

        internal ComponentIndex<TComponent, TKey> Build()
        {
            var buckets = _buckets ?? throw new InvalidOperationException(
                "An index builder cannot publish more than once.");
            _buckets = null;
            return new ComponentIndex<TComponent, TKey>(buckets);
        }
    }

    /// <summary>
    /// Keeps the mutation-friendly representation private and publishes a new
    /// immutable array at most once per changed generation. A later mutation
    /// never writes an array that may already back a caller's span.
    /// </summary>
    private sealed class Bucket
    {
        private readonly object _publicationGate = new();
        private SmallList<Entity> _entities;
        private Entity[] _published = Array.Empty<Entity>();
        private bool _publicationCurrent = true;

        internal int Count => _entities.Count;

        internal void Add(Entity entity)
        {
            if (_entities.AsSpan().IndexOf(entity) >= 0)
                return;

            _entities.Add(entity);
            _publicationCurrent = false;
        }

        internal bool Remove(Entity entity)
        {
            if (!_entities.RemoveSwapBack(entity))
                return false;

            _publicationCurrent = false;
            return true;
        }

        internal Entity[] Publish()
        {
            lock (_publicationGate)
            {
                if (_publicationCurrent)
                    return _published;

                _published = _entities.Count == 0
                    ? Array.Empty<Entity>()
                    : _entities.AsSpan().ToArray();
                _publicationCurrent = true;

                return _published;
            }
        }

        internal Bucket CloneDetached()
        {
            lock (_publicationGate)
            {
                var clone = new Bucket
                {
                    // A published entity array is immutable and may remain alive for callers that
                    // retained a ReadOnlySpan from the prior generation.
                    _published = _published,
                    _publicationCurrent = _publicationCurrent,
                };

                clone._entities.EnsureCapacity(_entities.Count);
                ReadOnlySpan<Entity> entities = _entities.ReadSpan();
                for (int i = 0; i < entities.Length; i++)
                    clone._entities.Add(entities[i]);

                return clone;
            }
        }
    }

    private sealed class Generation
    {
        private int _shared;

        internal Generation(Dictionary<TKey, Bucket> buckets)
        {
            Buckets = buckets;
        }

        internal Dictionary<TKey, Bucket> Buckets { get; }

        internal bool IsShared => Volatile.Read(ref _shared) != 0;

        internal void MarkShared() => Volatile.Write(ref _shared, 1);
    }
}

