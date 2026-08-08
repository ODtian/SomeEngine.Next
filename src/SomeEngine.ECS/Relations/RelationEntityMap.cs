using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

/// <summary>
/// Entity-slot keyed page table for persistent relation-derived state. Lookup is O(1), iteration
/// visits only occupied slots, and a detached generation copies only its page-reference table plus
/// the first page it actually changes. No entity-scale hash table or whole-dense-set detach is
/// retained by a relation generation.
/// </summary>
internal sealed class RelationEntityMap<TValue>
{
    private const int PageShift = 8;
    private const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;
    private const int Missing = -1;

    private static int s_nextStorageIdentity;
    private Storage _storage;

    internal RelationEntityMap()
    {
        _storage = new Storage(NextStorageIdentity(), new Page?[4], count: 0);
    }

    private RelationEntityMap(Storage storage)
    {
        _storage = storage;
    }

    internal int Count => Volatile.Read(ref _storage).Count;

    /// <summary>
    /// Test-visible identity for proving that detached maps initially share their page table and
    /// split only after a write. It is deliberately not exposed outside friend assemblies.
    /// </summary>
    internal object BackingIdentity => Volatile.Read(ref _storage);

    /// <summary>Returns the immutable backing identity of the page containing <paramref name="entity"/>.</summary>
    internal object? PageBackingIdentity(Entity entity)
    {
        Storage storage = Volatile.Read(ref _storage);
        int pageIndex = entity.Index >> PageShift;
        return entity.Index > 0 && (uint)pageIndex < (uint)storage.Pages.Length
            ? storage.Pages[pageIndex]
            : null;
    }

    internal bool ContainsKey(Entity entity) =>
        TryLocate(Volatile.Read(ref _storage), entity, out _, out _, out _);

    internal bool TryGetValue(Entity entity, out TValue value)
    {
        Storage storage = Volatile.Read(ref _storage);
        if (TryLocate(storage, entity, out Page? page, out int slot, out _))
        {
            value = page!.Values[slot];
            return true;
        }

        value = default!;
        return false;
    }

    internal TValue this[Entity entity]
    {
        get
        {
            if (TryGetValue(entity, out TValue value))
                return value;
            throw new KeyNotFoundException($"Relation entity key {entity} was not found.");
        }
        set
        {
            Storage storage = Volatile.Read(ref _storage);
            if (!TryLocate(storage, entity, out _, out int slot, out _))
            {
                Add(entity, value);
                return;
            }

            Page page = WritablePage(entity.Index, create: false, out storage);
            if (page.Generations[slot] != entity.Generation)
                throw new InvalidOperationException($"Relation entity key {entity} changed during replacement.");
            page.Values[slot] = value;
        }
    }

    internal void Add(Entity entity, TValue value)
    {
        ThrowInvalidEntity(entity);
        Storage observed = Volatile.Read(ref _storage);
        if (TryLocate(observed, entity, out _, out _, out _))
            throw new InvalidOperationException($"Relation entity key {entity} already exists.");

        int slot = entity.Index & PageMask;
        Page page = WritablePage(entity.Index, create: true, out Storage storage);
        int position = page.DensePositions[slot];
        if (position != Missing)
        {
            throw new InvalidOperationException(
                $"Relation entity slot {entity.Index} is still occupied by generation {page.Generations[slot]}.");
        }

        int denseIndex = page.Count;
        page.DenseSlots[denseIndex] = (ushort)slot;
        page.DensePositions[slot] = denseIndex;
        page.Generations[slot] = entity.Generation;
        page.Values[slot] = value;
        page.Count++;
        storage.Count++;
    }

    internal bool TryAdd(Entity entity, TValue value)
    {
        if (ContainsKey(entity))
            return false;
        Add(entity, value);
        return true;
    }

    internal bool Remove(Entity entity) => Remove(entity, out _);

    internal bool Remove(Entity entity, out TValue value)
    {
        Storage observed = Volatile.Read(ref _storage);
        if (!TryLocate(observed, entity, out Page? found, out int slot, out int denseIndex))
        {
            value = default!;
            return false;
        }

        value = found!.Values[slot];
        Page page = WritablePage(entity.Index, create: false, out Storage storage);
        if (page.Generations[slot] != entity.Generation || page.DensePositions[slot] != denseIndex)
            throw new InvalidOperationException($"Relation entity key {entity} changed during removal.");

        int lastDenseIndex = page.Count - 1;
        int movedSlot = page.DenseSlots[lastDenseIndex];
        if (denseIndex != lastDenseIndex)
        {
            page.DenseSlots[denseIndex] = (ushort)movedSlot;
            page.DensePositions[movedSlot] = denseIndex;
        }

        page.DenseSlots[lastDenseIndex] = 0;
        page.DensePositions[slot] = Missing;
        page.Generations[slot] = 0;
        page.Values[slot] = default!;
        page.Count--;
        storage.Count--;
        if (page.Count == 0)
            storage.Pages[entity.Index >> PageShift] = null;
        return true;
    }

    internal RelationEntityMap<TValue> CloneDetached()
    {
        Storage storage = Volatile.Read(ref _storage);
        storage.MarkShared();
        return new RelationEntityMap<TValue>(storage);
    }

    public Enumerator GetEnumerator() => new(Volatile.Read(ref _storage));

    internal Entity[] ToEntityArray()
    {
        Storage storage = Volatile.Read(ref _storage);
        var entities = new Entity[storage.Count];
        int index = 0;
        var enumerator = new Enumerator(storage);
        while (enumerator.MoveNext())
            entities[index++] = enumerator.Current.Key;
        if (index != entities.Length)
            throw new InvalidOperationException("Relation entity map count changed during enumeration.");
        return entities;
    }

    private Page WritablePage(int entityIndex, bool create, out Storage storage)
    {
        storage = Volatile.Read(ref _storage);
        if (storage.IsShared)
        {
            storage = storage.CloneWritable(NextStorageIdentity());
            Volatile.Write(ref _storage, storage);
        }

        int pageIndex = entityIndex >> PageShift;
        EnsurePageCapacity(storage, pageIndex + 1);
        Page? page = storage.Pages[pageIndex];
        if (page is null)
        {
            if (!create)
                throw new InvalidOperationException($"Relation entity page {pageIndex} is missing.");
            page = new Page(storage.Identity);
            storage.Pages[pageIndex] = page;
            return page;
        }

        if (page.OwnerIdentity != storage.Identity)
        {
            page = page.CloneFor(storage.Identity);
            storage.Pages[pageIndex] = page;
        }
        return page;
    }

    private static bool TryLocate(
        Storage storage,
        Entity entity,
        out Page? page,
        out int slot,
        out int denseIndex)
    {
        page = null;
        slot = 0;
        denseIndex = Missing;
        if (entity.Index <= 0)
            return false;

        int pageIndex = entity.Index >> PageShift;
        if ((uint)pageIndex >= (uint)storage.Pages.Length)
            return false;
        page = storage.Pages[pageIndex];
        if (page is null)
            return false;

        slot = entity.Index & PageMask;
        denseIndex = page.DensePositions[slot];
        return denseIndex != Missing &&
               denseIndex < page.Count &&
               page.Generations[slot] == entity.Generation &&
               page.DenseSlots[denseIndex] == slot;
    }

    private static void EnsurePageCapacity(Storage storage, int required)
    {
        if (required <= storage.Pages.Length)
            return;
        int capacity = storage.Pages.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);
        storage.ResizePages(capacity);
    }

    private static void ThrowInvalidEntity(Entity entity)
    {
        // Generation zero is the first live generation produced by EntityStore. Occupancy is
        // represented by DensePositions, so zero is not overloaded as an empty-page sentinel.
        if (entity.Index <= 0)
            throw new InvalidOperationException($"Entity {entity} is not a valid relation storage key.");
    }

    private static int NextStorageIdentity()
    {
        int identity = Interlocked.Increment(ref s_nextStorageIdentity);
        return identity > 0
            ? identity
            : throw new InvalidOperationException("Relation entity storage identity overflow.");
    }

    public struct Enumerator
    {
        private readonly Storage _storage;
        private int _pageIndex;
        private int _denseIndex;
        private KeyValuePair<Entity, TValue> _current;

        internal Enumerator(Storage storage)
        {
            _storage = storage;
            _pageIndex = 0;
            _denseIndex = 0;
            _current = default;
        }

        public KeyValuePair<Entity, TValue> Current => _current;

        public bool MoveNext()
        {
            while (_pageIndex < _storage.Pages.Length)
            {
                Page? page = _storage.Pages[_pageIndex];
                if (page is not null && _denseIndex < page.Count)
                {
                    int slot = page.DenseSlots[_denseIndex++];
                    int entityIndex = checked((_pageIndex << PageShift) + slot);
                    _current = new KeyValuePair<Entity, TValue>(
                        new Entity(entityIndex, page.Generations[slot]),
                        page.Values[slot]);
                    return true;
                }

                _pageIndex++;
                _denseIndex = 0;
            }

            return false;
        }
    }

    internal sealed class Storage
    {
        private int _shared;

        internal Storage(int identity, Page?[] ownedPages, int count)
        {
            Identity = identity;
            _pages = ownedPages;
            Count = count;
        }

        private Page?[] _pages;

        internal int Identity { get; }

        internal Span<Page?> Pages => _pages;

        internal int Count;

        internal bool IsShared => Volatile.Read(ref _shared) != 0;

        internal void MarkShared() => Volatile.Write(ref _shared, 1);

        internal Storage CloneWritable(int identity) =>
            new(identity, (Page?[])_pages.Clone(), Count);

        internal void ResizePages(int capacity) =>
            Array.Resize(ref _pages, capacity);
    }

    internal sealed class Page
    {
        internal Page(int ownerIdentity)
        {
            OwnerIdentity = ownerIdentity;
            _generations = new int[PageSize];
            _values = new TValue[PageSize];
            _denseSlots = new ushort[PageSize];
            _densePositions = new int[PageSize];
            Array.Fill(_densePositions, Missing);
        }

        private Page(
            int ownerIdentity,
            int count,
            int[] generations,
            TValue[] values,
            ushort[] denseSlots,
            int[] densePositions)
        {
            OwnerIdentity = ownerIdentity;
            Count = count;
            _generations = generations;
            _values = values;
            _denseSlots = denseSlots;
            _densePositions = densePositions;
        }

        private readonly int[] _generations;
        private readonly TValue[] _values;
        private readonly ushort[] _denseSlots;
        private readonly int[] _densePositions;

        internal int OwnerIdentity { get; }

        internal int Count;

        internal Span<int> Generations => _generations;

        internal Span<TValue> Values => _values;

        internal Span<ushort> DenseSlots => _denseSlots;

        internal Span<int> DensePositions => _densePositions;

        internal Page CloneFor(int ownerIdentity) =>
            new(
                ownerIdentity,
                Count,
                (int[])_generations.Clone(),
                (TValue[])_values.Clone(),
                (ushort[])_denseSlots.Clone(),
                (int[])_densePositions.Clone());
    }
}

/// <summary>
/// Immutable endpoint-local dirty edge bucket. Replacement copies only the affected endpoint's
/// dirty list; detached generations safely share every untouched bucket backing.
/// </summary>
internal readonly struct RelationDirtyEdgeBucket
{
    private readonly Entity[]? _entities;

    private RelationDirtyEdgeBucket(Entity[] entities)
    {
        _entities = entities;
    }

    internal ReadOnlySpan<Entity> Entities => _entities ?? Array.Empty<Entity>();

    internal RelationDirtyEdgeBucket Add(Entity entity)
    {
        ReadOnlySpan<Entity> source = Entities;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == entity)
                return this;
        }

        var next = new Entity[source.Length + 1];
        source.CopyTo(next);
        next[^1] = entity;
        return new RelationDirtyEdgeBucket(next);
    }

    internal RelationDirtyEdgeBucket Remove(Entity entity, out bool removed)
    {
        ReadOnlySpan<Entity> source = Entities;
        int index = -1;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == entity)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            removed = false;
            return this;
        }

        removed = true;
        if (source.Length == 1)
            return default;

        var next = new Entity[source.Length - 1];
        source[..index].CopyTo(next);
        source[(index + 1)..].CopyTo(next.AsSpan(index));
        return new RelationDirtyEdgeBucket(next);
    }
}
