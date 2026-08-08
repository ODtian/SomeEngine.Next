using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Entities;

/// <summary>
/// Root-local materialized entity record returned by read operations.
/// </summary>
/// <remarks>
/// Archetype and Chunk are resolved snapshots, never the payload of a shared persistent page.
/// Free-list and generation facts are copied from that page at the same time.
/// </remarks>
internal struct EntityRecord
{
    /// <summary>
    /// Root-local archetype shell. Null means unplaced or free.
    /// </summary>
    public Archetype? Archetype;

    /// <summary>
    /// Root-local chunk shell. Null means unplaced or free.
    /// </summary>
    public Chunk? Chunk;

    /// <summary>
    /// free-list 链接。死亡后复用为 nextFreeIndex。活着时无意义。
    /// </summary>
    public int FreeListNext;

    /// <summary>
    /// entity 在 Chunk 内的行号。
    /// </summary>
    public int RowInChunk;

    /// <summary>
    /// entity 代数。每次释放递增，使旧 Entity 失效。
    /// </summary>
    public int Generation;

    /// <summary>
    /// True only after World.DestroyEntity requested cleanup teardown. Merely retaining a
    /// Removed&lt;T&gt; fact must not make an otherwise live entity look pending-destroy.
    /// </summary>
    public bool PendingDestroy;
}

/// <summary>
/// Root-neutral facts stored in persistent record pages. Table object references are deliberately
/// excluded: a shared page may outlive the root which created it, so retaining an Archetype or
/// Chunk here would pin that ancestor's complete table graph and chunk backing.
/// </summary>
internal struct PersistentEntityRecord
{
    public long ArchetypeIdentity;
    public long ChunkIdentity;
    public int FreeListNext;
    public int RowInChunk;
    public int Generation;
    public bool PendingDestroy;
}

/// <summary>
/// Writable root-local view over one persistent record. Object-valued reads resolve through the
/// owning store's current table image; writes persist only stable identities and scalar facts.
/// </summary>
internal readonly struct EntityRecordWriter
{
    private readonly EntityStore _store;
    private readonly int _index;

    internal EntityRecordWriter(EntityStore store, int index)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _index = index;
    }

    public Archetype? Archetype
    {
        get => _store.ResolveArchetypeIdentity(_store.StoredRecordSnapshot(_index).ArchetypeIdentity);
        set => _store.WritableRecord(_index).ArchetypeIdentity = value?.PersistentIdentity ?? 0;
    }

    public Chunk? Chunk
    {
        get => _store.ResolveChunkIdentity(_store.StoredRecordSnapshot(_index).ChunkIdentity);
        set => _store.WritableRecord(_index).ChunkIdentity = value?.PersistentIdentity ?? 0;
    }

    public int FreeListNext
    {
        get => _store.StoredRecordSnapshot(_index).FreeListNext;
        set => _store.WritableRecord(_index).FreeListNext = value;
    }

    public int RowInChunk
    {
        get => _store.StoredRecordSnapshot(_index).RowInChunk;
        set => _store.WritableRecord(_index).RowInChunk = value;
    }

    public int Generation
    {
        get => _store.StoredRecordSnapshot(_index).Generation;
        set => _store.WritableRecord(_index).Generation = value;
    }

    public bool PendingDestroy
    {
        get => _store.StoredRecordSnapshot(_index).PendingDestroy;
        set => _store.WritableRecord(_index).PendingDestroy = value;
    }

    public static implicit operator EntityRecord(EntityRecordWriter writer) =>
        writer._store.RecordSnapshot(writer._index);
}

