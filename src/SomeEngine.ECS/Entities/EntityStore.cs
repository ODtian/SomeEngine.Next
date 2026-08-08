using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Entities;

/// <summary>
/// Entity allocator and location records. Records live in fixed-size persistent pages: a detached
/// structural candidate copies only the page-reference table and detaches a page on its first
/// mutation. Persistent pages contain only stable table identities and scalar facts; every object
/// location is resolved through this store's root-local table image. There is no mutation delta
/// stream, ancestor-shell reference, or overlay-chain lookup on the hot path.
/// </summary>
internal sealed partial class EntityStore
{
    private const int RecordsPerPage = 256;

    private static long s_nextOwnerIdentity;
    private static long s_nextPageIdentity;

    private readonly long _ownerIdentity;
    private EntityRecordPage[] _pages;
    private readonly Dictionary<long, Archetype> _currentArchetypes;
    private readonly Dictionary<long, Chunk> _currentChunks;
    private ArchetypeRegistry? _installedTableImage;
    private int _freeListHead = -1;
    private int _aliveCount;
    private int _count;

    internal EntityStore(int initialCapacity = 64)
    {
        _ownerIdentity = Interlocked.Increment(ref s_nextOwnerIdentity);
        _currentArchetypes = new Dictionary<long, Archetype>();
        _currentChunks = new Dictionary<long, Chunk>();
        _pages = CreatePages(Math.Max(1, initialCapacity + 1));
        MutableRecord(0).Generation = -1;
    }

    private EntityStore(
        EntityStore source,
        Dictionary<long, Archetype> currentArchetypes,
        Dictionary<long, Chunk> currentChunks,
        ArchetypeRegistry installedTableImage)
    {
        _ownerIdentity = Interlocked.Increment(ref s_nextOwnerIdentity);
        _pages = (EntityRecordPage[])source._pages.Clone();
        _currentArchetypes = currentArchetypes;
        _currentChunks = currentChunks;
        _installedTableImage = installedTableImage;
        _freeListHead = source._freeListHead;
        _aliveCount = source._aliveCount;
        _count = source._count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity Allocate()
    {
        _ = Allocate(out Entity id);
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntityRecordWriter Allocate(out Entity id)
    {
        int index;
        if (_freeListHead != -1)
        {
            index = _freeListHead;
            ref PersistentEntityRecord recycled = ref MutableRecord(index);
            int nextFree = recycled.FreeListNext;
            int generation = recycled.Generation;
            recycled = default;
            recycled.Generation = generation;
            _freeListHead = nextFree;
        }
        else
        {
            index = ++_count;
            EnsureCapacity(index + 1);
        }

        _aliveCount++;
        ref PersistentEntityRecord record = ref MutableRecord(index);
        id = new Entity(index, record.Generation);
        return new EntityRecordWriter(this, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EntityRecordWriter AllocatePrepared(out Entity id)
    {
        int index;
        if (_freeListHead != -1)
        {
            index = _freeListHead;
            ref PersistentEntityRecord recycled = ref MutableRecord(index);
            int nextFree = recycled.FreeListNext;
            int generation = recycled.Generation;
            recycled = default;
            recycled.Generation = generation;
            _freeListHead = nextFree;
        }
        else
        {
            index = ++_count;
        }

        _aliveCount++;
        ref PersistentEntityRecord record = ref MutableRecord(index);
        id = new Entity(index, record.Generation);
        return new EntityRecordWriter(this, index);
    }

    internal void Free(Entity id)
    {
        if (!IsAlive(id))
        {
            throw new InvalidOperationException(
                $"Cannot free {id}: entity is not alive (possibly already freed or stale generation).");
        }

        int index = id.Index;
        ref PersistentEntityRecord record = ref MutableRecord(index);
        int nextGeneration = record.Generation + 1;
        _aliveCount--;
        record.ArchetypeIdentity = 0;
        record.ChunkIdentity = 0;
        record.FreeListNext = _freeListHead;
        record.RowInChunk = 0;
        record.PendingDestroy = false;
        record.Generation = nextGeneration;
        _freeListHead = index;
    }

    internal bool IsAlive(Entity id)
    {
        return id.Index > 0 &&
               id.Index <= _count &&
               ReadRecord(id.Index).Generation == id.Generation;
    }

    /// <summary>
    /// Returns a root-local writable record view. On a detached candidate this operation performs
    /// at most one bounded identity-page copy before exposing the view.
    /// </summary>
    internal EntityRecordWriter GetRecord(Entity id)
    {
        if (!IsAlive(id))
            throw new InvalidOperationException($"Cannot access record for {id}: entity is not alive.");

        _ = MutableRecord(id.Index);
        return new EntityRecordWriter(this, id.Index);
    }

    /// <summary>
    /// Resolves a live location through the current root without detaching its persistent record
    /// page. A shared page never contains an ancestor table object reference.
    /// </summary>
    internal EntityRecord GetRecordReadOnly(Entity id)
    {
        if (!IsAlive(id))
            throw new InvalidOperationException($"Cannot access record for {id}: entity is not alive.");

        return Materialize(ReadRecord(id.Index));
    }

    internal int Count => _count;

    internal int AliveCount => _aliveCount;

    internal int RecordPageCount => _pages.Length;

    internal long RecordPageIdentity(int entityIndex) =>
        Page(entityIndex).Identity;

    internal long RecordPageVersion(int entityIndex) =>
        Page(entityIndex).Version;

    internal EntityRecord RecordSnapshot(int entityIndex)
    {
        if (entityIndex < 0 || entityIndex >= _pages.Length * RecordsPerPage)
            throw new ArgumentOutOfRangeException(nameof(entityIndex));
        return Materialize(ReadRecord(entityIndex));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PersistentEntityRecord StoredRecordSnapshot(int entityIndex) =>
        ReadRecord(entityIndex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref PersistentEntityRecord WritableRecord(int entityIndex) =>
        ref MutableRecord(entityIndex);

    internal bool OwnsRecordPage(int entityIndex) =>
        Page(entityIndex).OwnerIdentity == _ownerIdentity;

    internal bool SharesRecordPageWith(EntityStore other, int entityIndex)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(Page(entityIndex), other.Page(entityIndex));
    }

    /// <summary>
    /// Forks the entity page table without copying record pages. Stable table identities resolve
    /// directly through this candidate's current table image, so publication chains never become
    /// lookup overlays and resolver size is bounded by the current table image.
    /// </summary>
    internal EntityStore CloneExact(ArchetypeRegistry tableImage)
    {
        ArgumentNullException.ThrowIfNull(tableImage);
        ReadOnlySpan<Archetype> archetypes = tableImage.AllArchetypes;

        var currentArchetypes = new Dictionary<long, Archetype>(archetypes.Length);
        var currentChunks = new Dictionary<long, Chunk>();

        for (int archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            Archetype archetype = archetypes[archetypeIndex];
            currentArchetypes.Add(archetype.PersistentIdentity, archetype);
            ReadOnlySpan<Chunk> chunks = archetype.Chunks;
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                Chunk chunk = chunks[chunkIndex];
                currentChunks.Add(chunk.PersistentIdentity, chunk);
            }
        }

        return new EntityStore(this, currentArchetypes, currentChunks, tableImage);
    }

    /// <summary>
    /// Forks the record-page table and takes the root-local identity resolvers already constructed
    /// by the table clone. This is the root/snapshot path; it avoids a second full table traversal.
    /// </summary>
    internal EntityStore CloneExact(
        DetachedTableMap tableMap,
        ArchetypeRegistry tableImage)
    {
        ArgumentNullException.ThrowIfNull(tableMap);
        ArgumentNullException.ThrowIfNull(tableImage);
        if (tableMap.CandidateIdentityCount != tableImage.AllArchetypes.Length)
        {
            throw new InvalidOperationException(
                "Detached table identity map does not match its archetype image.");
        }

        (
            Dictionary<long, Archetype> candidateArchetypes,
            Dictionary<long, Chunk> candidateChunks) =
            tableMap.TakeCandidateIdentityResolvers();
        return new EntityStore(
            this,
            candidateArchetypes,
            candidateChunks,
            tableImage);
    }

    /// <summary>
    /// Installs the complete table image which owns this store. Tables calls this when its registry
    /// is attached; an exact clone already carries that same image and returns here in O(1) instead
    /// of scanning it again. Subsequent table-object lifetime changes use the incremental
    /// registration methods below. Keeping this resolver root-local prevents a shared record page
    /// from retaining or resolving through an ancestor table shell.
    /// </summary>
    internal void InstallTableImage(ArchetypeRegistry tableImage)
    {
        ArgumentNullException.ThrowIfNull(tableImage);
        if (UsesTableImage(tableImage))
            return;

        _installedTableImage = null;
        _currentArchetypes.Clear();
        _currentChunks.Clear();
        ReadOnlySpan<Archetype> archetypes = tableImage.AllArchetypes;
        for (int archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            Archetype archetype = archetypes[archetypeIndex];
            RegisterArchetype(archetype);
            ReadOnlySpan<Chunk> chunks = archetype.Chunks;
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
                RegisterChunk(chunks[chunkIndex]);
        }

        _installedTableImage = tableImage;
    }

    private bool UsesTableImage(ArchetypeRegistry tableImage)
    {
        ArgumentNullException.ThrowIfNull(tableImage);
        return ReferenceEquals(_installedTableImage, tableImage) &&
               _currentArchetypes.Count == tableImage.AllArchetypes.Length;
    }

    internal void RegisterArchetype(Archetype archetype)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        if (_currentArchetypes.TryAdd(archetype.PersistentIdentity, archetype))
            return;

        if (!ReferenceEquals(_currentArchetypes[archetype.PersistentIdentity], archetype))
        {
            throw new InvalidOperationException(
                "A different archetype already owns the same persistent table identity.");
        }
    }

    internal void RegisterChunk(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_currentChunks.TryAdd(chunk.PersistentIdentity, chunk))
            return;

        if (!ReferenceEquals(_currentChunks[chunk.PersistentIdentity], chunk))
        {
            throw new InvalidOperationException(
                "A different chunk already owns the same persistent table identity.");
        }
    }

    internal void ReplaceChunk(Chunk retired, Chunk replacement)
    {
        ArgumentNullException.ThrowIfNull(retired);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!_currentChunks.TryGetValue(retired.PersistentIdentity, out Chunk? current) ||
            !ReferenceEquals(current, retired))
        {
            throw new InvalidOperationException(
                "Cannot promote a chunk which is not the current root-local table object.");
        }
        if (_currentChunks.TryGetValue(replacement.PersistentIdentity, out Chunk? collision) &&
            !ReferenceEquals(collision, replacement))
        {
            throw new InvalidOperationException(
                "The promoted chunk persistent identity is already owned by another table object.");
        }

        // Validate both mutations before changing either entry. The two identities are distinct
        // for a promoted chunk today; retaining the branch keeps this correct if promotion later
        // preserves logical chunk identity.
        if (retired.PersistentIdentity == replacement.PersistentIdentity)
        {
            _currentChunks[retired.PersistentIdentity] = replacement;
            return;
        }

        _currentChunks.Add(replacement.PersistentIdentity, replacement);
        if (!_currentChunks.Remove(retired.PersistentIdentity))
        {
            _currentChunks.Remove(replacement.PersistentIdentity);
            throw new InvalidOperationException(
                "The retired chunk disappeared while installing its promoted replacement.");
        }
    }

    internal void UnregisterChunk(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (!_currentChunks.TryGetValue(chunk.PersistentIdentity, out Chunk? current) ||
            !ReferenceEquals(current, chunk))
        {
            throw new InvalidOperationException(
                "Cannot recycle a chunk which is not the current root-local table object.");
        }
        if (!_currentChunks.Remove(chunk.PersistentIdentity))
            throw new InvalidOperationException("Failed to retire the current chunk table object.");
    }

    /// <summary>Full O(archetype + chunk) resolver audit for tests and diagnostics.</summary>
    internal void ValidateTableResolver(ArchetypeRegistry tableImage)
    {
        ArgumentNullException.ThrowIfNull(tableImage);
        ReadOnlySpan<Archetype> archetypes = tableImage.AllArchetypes;
        if (_currentArchetypes.Count != archetypes.Length)
        {
            throw new InvalidOperationException(
                $"Table resolver contains {_currentArchetypes.Count} archetypes; expected {archetypes.Length}.");
        }

        int chunkCount = 0;
        for (int archetypeIndex = 0; archetypeIndex < archetypes.Length; archetypeIndex++)
        {
            Archetype archetype = archetypes[archetypeIndex];
            if (!_currentArchetypes.TryGetValue(archetype.PersistentIdentity, out Archetype? current) ||
                !ReferenceEquals(current, archetype))
            {
                throw new InvalidOperationException(
                    "Table resolver does not contain the exact current archetype shell.");
            }

            ReadOnlySpan<Chunk> chunks = archetype.Chunks;
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                Chunk chunk = chunks[chunkIndex];
                if (chunk.IndexInArchetype != chunkIndex)
                {
                    throw new InvalidOperationException(
                        "Chunk IndexInArchetype does not match its current table position.");
                }
                if (!_currentChunks.TryGetValue(chunk.PersistentIdentity, out Chunk? currentChunk) ||
                    !ReferenceEquals(currentChunk, chunk))
                {
                    throw new InvalidOperationException(
                        "Table resolver does not contain the exact current chunk shell.");
                }
                chunkCount++;
            }
        }

        if (_currentChunks.Count != chunkCount)
        {
            throw new InvalidOperationException(
                $"Table resolver contains {_currentChunks.Count} chunks; expected {chunkCount}.");
        }
    }

    /// <summary>
    /// Full diagnostic audit for tests/tools. Structural candidate creation deliberately does not
    /// call this O(entity-count) scan; ordinary invariants are maintained incrementally and each
    /// bounded page validates that its stable identities belong to the current table image when it
    /// first detaches.
    /// </summary>
    internal void ValidateExact(DetachedTableMap tableMap)
    {
        ArgumentNullException.ThrowIfNull(tableMap);
        ValidateAllocatorImage();
        ValidateTableRows(tableMap);
    }

    internal void EnsureAdditionalCapacity(int additionalCount)
    {
        if (additionalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalCount));

        EnsureCapacity(_count + additionalCount + 1);
    }

    internal int GetGeneration(int index)
    {
        if (!IsAllocatedIndex(index))
            throw new ArgumentOutOfRangeException(nameof(index));

        return ReadRecord(index).Generation;
    }

    internal bool IsAliveIndex(int index)
    {
        return IsAllocatedIndex(index) && ReadRecord(index).ArchetypeIdentity != 0;
    }

    private void EnsureCapacity(int required)
    {
        int requiredPages = PageCount(required);
        if (requiredPages <= _pages.Length)
            return;

        int oldLength = _pages.Length;
        int newLength = Math.Max(requiredPages, oldLength * 2);
        Array.Resize(ref _pages, newLength);
        for (int pageIndex = oldLength; pageIndex < newLength; pageIndex++)
        {
            _pages[pageIndex] = CreatePage();
        }
    }

    private bool IsAllocatedIndex(int index) => index > 0 && index <= _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly PersistentEntityRecord ReadRecord(int index)
    {
        EntityRecordPage page = Volatile.Read(ref _pages[index / RecordsPerPage]);
        return ref page.Records[index % RecordsPerPage];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref PersistentEntityRecord MutableRecord(int index)
    {
        int pageIndex = index / RecordsPerPage;
        EntityRecordPage page = Volatile.Read(ref _pages[pageIndex]);
        if (page.OwnerIdentity != _ownerIdentity)
        {
            // The backing page is immutable while shared, so it is also the zero-allocation
            // first-detach gate. Different pages remain independently detachable.
            EntityRecordPage lockedPage = page;
            lock (lockedPage)
            {
                page = Volatile.Read(ref _pages[pageIndex]);
                if (page.OwnerIdentity != _ownerIdentity)
                {
                    page = DetachPage(page);
                    Volatile.Write(ref _pages[pageIndex], page);
                }
            }
        }

        return ref page.Records[index % RecordsPerPage];
    }

    private EntityRecordPage DetachPage(EntityRecordPage source)
    {
        var records = (PersistentEntityRecord[])source.Records.Clone();
        for (int index = 0; index < records.Length; index++)
        {
            ref PersistentEntityRecord record = ref records[index];
            if ((record.ArchetypeIdentity == 0) != (record.ChunkIdentity == 0))
            {
                throw new InvalidOperationException(
                    "Entity record page contains a partial table location.");
            }
            if (record.ArchetypeIdentity != 0 &&
                !_currentArchetypes.ContainsKey(record.ArchetypeIdentity))
            {
                throw new InvalidOperationException(
                    "Entity record page references an archetype outside the candidate table image.");
            }
            if (record.ChunkIdentity != 0 &&
                !_currentChunks.ContainsKey(record.ChunkIdentity))
            {
                throw new InvalidOperationException(
                    "Entity record page references a chunk outside the candidate table image.");
            }
        }

        return new EntityRecordPage(
            Interlocked.Increment(ref s_nextPageIdentity),
            _ownerIdentity,
            checked(source.Version + 1),
            records);
    }

    private EntityRecordPage[] CreatePages(int capacity)
    {
        var pages = new EntityRecordPage[PageCount(capacity)];
        for (int index = 0; index < pages.Length; index++)
            pages[index] = CreatePage();
        return pages;
    }

    private EntityRecordPage CreatePage() =>
        new(
            Interlocked.Increment(ref s_nextPageIdentity),
            _ownerIdentity,
            version: 0,
            new PersistentEntityRecord[RecordsPerPage]);

    private static int PageCount(int capacity) =>
        Math.Max(1, checked((capacity + RecordsPerPage - 1) / RecordsPerPage));

    private EntityRecordPage Page(int entityIndex)
    {
        if (entityIndex < 0 || entityIndex >= _pages.Length * RecordsPerPage)
            throw new ArgumentOutOfRangeException(nameof(entityIndex));
        return Volatile.Read(ref _pages[entityIndex / RecordsPerPage]);
    }

    internal Archetype? ResolveArchetypeIdentity(long identity)
    {
        if (identity == 0)
            return null;
        if (_currentArchetypes.TryGetValue(identity, out Archetype? current))
            return current;

        throw new InvalidOperationException(
            "Entity record references an archetype outside the current root-local table image.");
    }

    internal Chunk? ResolveChunkIdentity(long identity)
    {
        if (identity == 0)
            return null;
        if (_currentChunks.TryGetValue(identity, out Chunk? current))
            return current;

        throw new InvalidOperationException(
            "Entity record references a chunk outside the current root-local table image.");
    }

    private EntityRecord Materialize(in PersistentEntityRecord stored)
    {
        bool hasArchetype = stored.ArchetypeIdentity != 0;
        if (hasArchetype != (stored.ChunkIdentity != 0))
            throw new InvalidOperationException("Entity record contains a partial table location.");

        return new EntityRecord
        {
            Archetype = ResolveArchetypeIdentity(stored.ArchetypeIdentity),
            Chunk = ResolveChunkIdentity(stored.ChunkIdentity),
            FreeListNext = stored.FreeListNext,
            RowInChunk = stored.RowInChunk,
            Generation = stored.Generation,
            PendingDestroy = stored.PendingDestroy,
        };
    }

    private static bool ContainsChunk(Archetype archetype, Chunk expected)
    {
        foreach (Chunk chunk in archetype.Chunks)
        {
            if (ReferenceEquals(chunk, expected))
                return true;
        }
        return false;
    }

    private void ValidateTableRows(DetachedTableMap tableMap)
    {
        var visited = new bool[_count + 1];
        int tableRowCount = tableMap.ValidateTableRows(this, visited);

        if (tableRowCount != _aliveCount)
        {
            throw new InvalidOperationException(
                $"Detached table image contains {tableRowCount} live rows; expected {_aliveCount}.");
        }

        for (int index = 1; index <= _count; index++)
        {
            if ((ReadRecord(index).ArchetypeIdentity != 0) != visited[index])
            {
                throw new InvalidOperationException(
                    $"Entity slot {index} and detached table row membership disagree.");
            }
        }
    }

    internal int ValidateMappedArchetypeRows(
        Archetype sourceArchetype,
        Archetype candidateArchetype,
        DetachedTableMap tableMap,
        Span<bool> visited)
    {
        int tableRowCount = 0;
        foreach (Chunk sourceChunk in sourceArchetype.Chunks)
        {
            Chunk candidateChunk = tableMap.Remap(sourceChunk);
            if (!ContainsChunk(candidateArchetype, candidateChunk))
            {
                throw new InvalidOperationException(
                    "Detached chunk is not owned by the candidate archetype mapped from its source owner.");
            }

            for (int row = 0; row < sourceChunk.Count; row++)
            {
                Entity entity = sourceChunk.Entities[row];
                if (entity.Index <= 0 || entity.Index > _count)
                {
                    throw new InvalidOperationException(
                        $"Table row {row} contains entity {entity} outside the allocator range.");
                }

                if (visited[entity.Index])
                {
                    throw new InvalidOperationException(
                        $"Entity slot {entity.Index} appears in more than one table row.");
                }

                ref readonly PersistentEntityRecord record = ref ReadRecord(entity.Index);
                if (record.Generation != entity.Generation ||
                    !ReferenceEquals(
                        ResolveArchetypeIdentity(record.ArchetypeIdentity),
                        sourceArchetype) ||
                    !ReferenceEquals(
                        ResolveChunkIdentity(record.ChunkIdentity),
                        sourceChunk) ||
                    record.RowInChunk != row)
                {
                    throw new InvalidOperationException(
                        $"Table row {row} for {entity} does not round-trip to the same entity record.");
                }

                visited[entity.Index] = true;
                tableRowCount++;
            }
        }

        return tableRowCount;
    }

    private void ValidateAllocatorImage()
    {
        int capacity = _pages.Length * RecordsPerPage;
        if (_pages.Length == 0 || _count < 0 || _count >= capacity)
            throw new InvalidOperationException("Entity allocator count is outside the record pages.");
        if (_aliveCount < 0 || _aliveCount > _count)
            throw new InvalidOperationException("Entity allocator live count is outside the allocated slot range.");
        if (ReadRecord(0).ArchetypeIdentity != 0 || ReadRecord(0).ChunkIdentity != 0)
            throw new InvalidOperationException("Reserved entity slot zero must not reference table storage.");

        for (int index = _count + 1; index < capacity; index++)
        {
            ref readonly PersistentEntityRecord record = ref ReadRecord(index);
            if (record.ArchetypeIdentity != 0 || record.ChunkIdentity != 0)
            {
                throw new InvalidOperationException(
                    $"Unused entity record capacity slot {index} references table storage.");
            }
        }

        var visited = new bool[_count + 1];
        int freeCount = 0;
        int free = _freeListHead;
        while (free != -1)
        {
            if (free <= 0 || free > _count)
                throw new InvalidOperationException("Entity allocator free-list points outside allocated slots.");
            if (visited[free])
                throw new InvalidOperationException("Entity allocator free-list contains a cycle.");

            ref readonly PersistentEntityRecord record = ref ReadRecord(free);
            if (record.ArchetypeIdentity != 0 || record.ChunkIdentity != 0)
                throw new InvalidOperationException("Entity allocator free-list contains a live table record.");

            visited[free] = true;
            freeCount++;
            free = record.FreeListNext;
        }

        if (freeCount != _count - _aliveCount)
        {
            throw new InvalidOperationException(
                $"Entity allocator free-list contains {freeCount} slots; expected {_count - _aliveCount}.");
        }

        for (int index = 1; index <= _count; index++)
        {
            bool live = ReadRecord(index).ArchetypeIdentity != 0;
            if (live == visited[index])
            {
                throw new InvalidOperationException(
                    $"Entity slot {index} is inconsistent with allocator live/free membership.");
            }
        }
    }

}
