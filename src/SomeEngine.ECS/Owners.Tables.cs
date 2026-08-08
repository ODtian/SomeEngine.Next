using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Owners;

internal sealed class Tables
{
    private const int MaximumStackSharedValues = 256;
    private readonly Entities _entities;

    internal Tables(Entities entities, Action<Archetype> onArchetype)
        : this(entities, new ArchetypeRegistry(), onArchetype)
    {
    }

    internal Tables(
        Entities entities,
        ArchetypeRegistry registry,
        Action<Archetype> onArchetype)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(onArchetype);
        _entities.Store.InstallTableImage(Registry);
        Registry.OnArchetypeCreated = archetype =>
        {
            // The registry owns archetype lifetime; EntityStore owns the root-local persistent-id
            // resolver used by shared record pages. Register before invoking secondary observers
            // so a throwing query/cache callback cannot leave the resolver stale.
            _entities.Store.RegisterArchetype(archetype);
            onArchetype(archetype);
        };
        Empty = Registry.GetOrCreate(ReadOnlySpan<int>.Empty);
    }

    internal ArchetypeRegistry Registry { get; }

    internal Archetype Empty { get; }

    internal ReadOnlySpan<Archetype> All => Registry.AllArchetypes;

    internal int Count => Registry.AllArchetypes.Length;

    internal void MoveEntity(
        Entity entity,
        EntityRecordWriter record,
        StructuralTransition plan)
    {
        MoveRow(entity, record, plan.Target, plan.SharedColumns);
    }

    internal void MoveWithShared(
        Entity entity,
        EntityRecordWriter record,
        StructuralTransition plan,
        int sharedComponentId,
        int sharedIndex)
    {
        var destination = plan.Target;
        int count = destination.SharedComponentIds.Length;
        int[]? rented = null;
        Span<int> sharedValues = count <= MaximumStackSharedValues
            ? stackalloc int[count]
            : (rented = ArrayPool<int>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            FillEntityShared(entity, destination, sharedValues);
            sharedValues[Shared.Slot(destination, sharedComponentId)] = sharedIndex;
            MoveRow(entity, record, destination, plan.SharedColumns, sharedValues);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<int>.Shared.Return(rented);
        }
    }

    internal void MoveRow(
        Entity entity,
        EntityRecordWriter record,
        Archetype destination,
        ReadOnlySpan<SharedColumnMapping> mappings)
    {
        MoveRow(entity, record, destination, mappings, ReadOnlySpan<int>.Empty);
    }

    internal void MoveRow(
        Entity entity,
        EntityRecordWriter record,
        Archetype destination,
        ReadOnlySpan<SharedColumnMapping> mappings,
        ReadOnlySpan<int> destinationSharedValues)
    {
        var sourceArchetype = record.Archetype!;
        var sourceChunk = record.Chunk!;
        int sourceRow = record.RowInChunk;

        if (ReferenceEquals(sourceArchetype, destination))
        {
            if (mappings.Length != 0 || destinationSharedValues.Length != 0)
            {
                throw new InvalidOperationException(
                    "A same-archetype structural transition cannot contain row mappings.");
            }

            return;
        }

        var (destinationChunk, destinationRow) = destinationSharedValues.Length > 0
            ? AllocateShared(destination, entity, destinationSharedValues)
            : AllocateInChunk(destination, entity);
        // Distinct archetypes own disjoint chunk shells. Keep the source backing read-only during
        // the copy and let RemoveRow perform its single required detach afterward.
        if (ReferenceEquals(sourceChunk, destinationChunk))
        {
            throw new InvalidOperationException(
                "A structural row move cannot allocate into its source chunk.");
        }

        foreach (var mapping in mappings)
        {
            sourceChunk.CopyComponentTo(
                mapping.SourceColumnIndex,
                sourceRow,
                destinationChunk,
                mapping.DestinationColumnIndex,
                destinationRow,
                in mapping.Operations);

            sourceChunk.CopyVersions(
                mapping.SourceColumnIndex,
                sourceRow,
                mapping.DestinationColumnIndex,
                destinationRow,
                destinationChunk);

            int componentId = sourceArchetype.TableComponentIds[mapping.SourceColumnIndex];
            if (sourceArchetype.TryMask(componentId, out int sourceMaskIndex))
            {
                int destinationMaskIndex = destination.EnableMask(componentId);
                bool enabled = sourceChunk.IsEnabled(sourceMaskIndex, sourceRow);
                destinationChunk.WriteEnabled(destinationMaskIndex, destinationRow, enabled);
            }
        }

        var movedEntity = sourceChunk.RemoveRow(sourceRow, sourceArchetype.ColumnOperations);
        if (movedEntity != Entity.Null)
        {
            EntityRecordWriter movedRecord = _entities.Store.GetRecord(movedEntity);
            movedRecord.RowInChunk = sourceRow;
        }

        TryRecycleChunk(sourceArchetype, sourceChunk);

        record.Archetype = destination;
        record.Chunk = destinationChunk;
        record.RowInChunk = destinationRow;
    }

    internal (Chunk chunk, int row) AllocateInChunk(
        Archetype archetype,
        Entity entityId)
    {
        if (archetype.SharedComponentIds.Length > 0)
        {
            int count = archetype.SharedComponentIds.Length;
            int[]? rented = null;
            Span<int> sharedValues = count <= MaximumStackSharedValues
                ? stackalloc int[count]
                : (rented = ArrayPool<int>.Shared.Rent(count)).AsSpan(0, count);
            try
            {
                FillEntityShared(entityId, archetype, sharedValues);
                return AllocateShared(archetype, entityId, sharedValues);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<int>.Shared.Return(rented);
            }
        }

        return AllocateFast(archetype, entityId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal (Chunk chunk, int row) AllocatePrepared(
        Archetype archetype,
        Entity entityId,
        Chunk? preferred)
    {
        if (preferred is not null && !preferred.IsFull)
        {
            // PreferredChunk is retained only after AllocateFast/AllocateRow has detached it.
            int row = preferred.AllocateOwnedRow(entityId);
            if (preferred.IsFull)
                archetype.FirstOpenChunk = preferred.IndexInArchetype + 1;
            return (preferred, row);
        }

        return AllocateFast(archetype, entityId);
    }

    internal (Chunk chunk, int row) AllocateShared(
        Archetype archetype,
        Entity entityId,
        ReadOnlySpan<int> sharedValues)
    {
        SharedComponentTuple? canonicalTuple = null;
        if (archetype.TryGetSharedChunkBucket(sharedValues, out var bucket))
        {
            if (bucket.ChunkCount <= 0)
            {
                throw new InvalidOperationException(
                    "A registered shared-component bucket must own at least one physical chunk.");
            }

            canonicalTuple = bucket.Values;
            if (bucket.OpenChunkCount > 0)
            {
                Chunk chunk = bucket.NextOpenChunk;
                int row = chunk.AllocateRow(entityId);
                if (chunk.IsFull)
                    bucket.MarkFull(chunk);
                return (chunk, row);
            }
        }

        int capacity = ChunkCapacity.Shared(archetype, bucket);
        canonicalTuple ??= new SharedComponentTuple(sharedValues);
        return AllocateNewChunk(archetype, entityId, canonicalTuple, capacity);
    }

    internal void EnsureCapacity(Archetype archetype, int additionalEntityCapacity)
    {
        if (additionalEntityCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalEntityCapacity));

        if (archetype.SharedComponentIds.Length != 0)
            return;

        int freeCapacity = 0;
        for (int i = 0; i < archetype.ChunkCount; i++)
        {
            Chunk chunk = archetype.ChunkAt(i);
            freeCapacity += chunk.Capacity - chunk.Count;
        }

        if (freeCapacity >= additionalEntityCapacity)
            return;

        int additionalRows = additionalEntityCapacity - freeCapacity;
        archetype.EnsureChunkListCapacity(
            archetype.ChunkCount +
            (additionalRows + archetype.MaxChunkRows - 1) / archetype.MaxChunkRows);

        while (additionalRows > 0)
        {
            int capacity = ChunkCapacity.Reserved(archetype, additionalRows);
            var chunk = new Chunk(
                capacity,
                archetype.ColumnOperations,
                archetype.EnableableComponentIds.Length);
            chunk.IndexInArchetype = archetype.ChunkCount;
            archetype.AddChunk(chunk);
            _entities.Store.RegisterChunk(chunk);
            additionalRows -= capacity;
        }

        archetype.NextChunkRows = archetype.MaxChunkRows;
    }

    internal void TryRecycleChunk(Archetype archetype, Chunk chunk)
    {
        int chunkIndex = chunk.IndexInArchetype;
        if ((uint)chunkIndex >= (uint)archetype.ChunkCount ||
            !ReferenceEquals(archetype.ChunkAt(chunkIndex), chunk))
        {
            throw new InvalidOperationException(
                "Cannot recycle a chunk which is not at its current archetype index.");
        }

        if (chunkIndex < archetype.FirstOpenChunk)
            archetype.FirstOpenChunk = chunkIndex;

        MarkSharedChunkOpen(archetype, chunk);

        if (
            chunkIndex < archetype.ChunkCount
            && chunk.Count == 0
            && archetype.ChunkCount > 1
        )
        {
            int lastIndex = archetype.ChunkCount - 1;
            UnregisterSharedChunk(archetype, chunk);
            if (chunkIndex != lastIndex)
            {
                var movedChunk = archetype.ChunkAt(lastIndex);
                archetype.ReplaceChunk(chunkIndex, movedChunk);
                movedChunk.IndexInArchetype = chunkIndex;
            }
            _ = archetype.RemoveLastChunk();
            _entities.Store.UnregisterChunk(chunk);

            if (archetype.FirstOpenChunk >= archetype.ChunkCount)
                archetype.FirstOpenChunk = Math.Max(0, archetype.ChunkCount - 1);
        }
    }

    private (Chunk chunk, int row) AllocateFast(
        Archetype archetype,
        Entity entityId)
    {
        int hint = archetype.FirstOpenChunk;
        if (hint < archetype.ChunkCount && !archetype.ChunkAt(hint).IsFull)
        {
            var chunk = archetype.ChunkAt(hint);
            int row = chunk.AllocateRow(entityId);
            if (chunk.IsFull)
                archetype.FirstOpenChunk = hint + 1;
            return (chunk, row);
        }

        if (TryPromoteChunk(archetype, out var promotedChunk))
        {
            int row = promotedChunk.AllocateRow(entityId);
            if (promotedChunk.IsFull)
                archetype.FirstOpenChunk = promotedChunk.IndexInArchetype + 1;
            return (promotedChunk, row);
        }

        return AllocateNewChunk(
            archetype,
            entityId,
            sharedValues: null,
            ChunkCapacity.NextUnshared(archetype));
    }

    private (Chunk chunk, int row) AllocateNewChunk(
        Archetype archetype,
        Entity entityId,
        SharedComponentTuple? sharedValues,
        int capacity)
    {
        var newChunk = new Chunk(
            capacity,
            archetype.ColumnOperations,
            archetype.EnableableComponentIds.Length,
            sharedValues
        );
        int newIndex = archetype.ChunkCount;
        newChunk.IndexInArchetype = newIndex;
        archetype.AddChunk(newChunk);
        _entities.Store.RegisterChunk(newChunk);
        RegisterSharedChunk(archetype, newChunk);
        archetype.FirstOpenChunk = newIndex;
        int newRow = newChunk.AllocateRow(entityId);
        if (newChunk.IsFull)
        {
            MarkSharedChunkFull(archetype, newChunk);
            archetype.FirstOpenChunk = newIndex + 1;
        }
        return (newChunk, newRow);
    }

    private bool TryPromoteChunk(Archetype archetype, out Chunk promotedChunk)
    {
        promotedChunk = null!;
        if (archetype.SharedComponentIds.Length != 0 ||
            archetype.ChunkCount != 1)
        {
            return false;
        }

        var source = archetype.ChunkAt(0);
        if (!source.IsFull || source.Capacity >= archetype.MaxChunkRows)
            return false;

        int promotedCapacity = ChunkCapacity.Grow(archetype, source.Capacity);
        if (promotedCapacity <= source.Capacity)
            return false;

        promotedChunk = new Chunk(
            promotedCapacity,
            archetype.ColumnOperations,
            archetype.EnableableComponentIds.Length)
        {
            Count = source.Count,
            OrderVersion = source.OrderVersion,
            IndexInArchetype = 0,
        };

        source.Entities[..source.Count].CopyTo(promotedChunk.Entities);
        for (int column = 0; column < source.ColumnCount; column++)
            source.CopyColumnPrefixTo(column, promotedChunk, source.Count);

        source.ChangeVersions.CopyTo(promotedChunk.ChangeVersions);
        for (int column = 0; column < source.ChangeVersions.Length; column++)
        {
            source.AddVersionRows(column)[..source.Count]
                .CopyTo(promotedChunk.AddVersionRows(column));
            source.WriteVersionRows(column)[..source.Count]
                .CopyTo(promotedChunk.WriteVersionRows(column));
        }

        if (!source.EnableMasks.IsEmpty)
            source.EnableMasks.CopyTo(promotedChunk.EnableMasks);

        for (int row = 0; row < source.Count; row++)
        {
            EntityRecordWriter record = _entities.Store.GetRecord(source.Entities[row]);
            record.Archetype = archetype;
            record.Chunk = promotedChunk;
            record.RowInChunk = row;
        }

        archetype.ReplaceChunk(0, promotedChunk);
        _entities.Store.ReplaceChunk(source, promotedChunk);
        archetype.FirstOpenChunk = 0;
        archetype.NextChunkRows = ChunkCapacity.Grow(archetype, promotedCapacity);
        return true;
    }

    private void FillEntityShared(
        Entity entity,
        Archetype archetype,
        Span<int> values)
    {
        var sharedIds = archetype.SharedComponentIds;
        if (values.Length != sharedIds.Length)
            throw new ArgumentException("Shared value span length must match archetype shared component count.", nameof(values));

        if (sharedIds.Length == 0)
            return;

        EntityRecord record = _entities.Store.GetRecordReadOnly(entity);
        var sourceArchetype = record.Archetype!;
        var sourceSharedValues = record.Chunk!.SharedValues;

        for (int i = 0; i < sharedIds.Length; i++)
        {
            int sourceSlot = sourceArchetype.SharedComponentIds.BinarySearch(sharedIds[i]);
            if (sourceSlot < 0)
            {
                values[i] = -1;
                continue;
            }

            if (sourceSharedValues is null || sourceSlot >= sourceSharedValues.Length)
                throw new InvalidOperationException(
                    $"Entity {entity} is in archetype {sourceArchetype.ArchetypeId} with shared component ID {sharedIds[i]}, but its chunk has no shared value tuple.");

            values[i] = sourceSharedValues[sourceSlot];
        }
    }

    private static void RegisterSharedChunk(Archetype archetype, Chunk chunk)
    {
        if (chunk.SharedValues is null)
            return;

        SharedComponentTuple key = chunk.SharedValues;
        SharedChunkBucket bucket = archetype.GetOrAddSharedChunkBucket(key);
        if (!ReferenceEquals(bucket.Values, key))
        {
            throw new InvalidOperationException(
                "A shared-component bucket cannot register a non-canonical tuple.");
        }

        bucket.Register(chunk);
    }

    private static void MarkSharedChunkFull(Archetype archetype, Chunk chunk)
    {
        if (chunk.SharedValues is null)
            return;

        if (!archetype.TryGetSharedChunkBucket(chunk.SharedValues, out var bucket))
            throw new InvalidOperationException("A shared chunk must be registered before it becomes full.");

        bucket.MarkFull(chunk);
    }

    private static void MarkSharedChunkOpen(Archetype archetype, Chunk chunk)
    {
        if (chunk.SharedValues is null || chunk.IsFull)
            return;

        if (!archetype.TryGetSharedChunkBucket(chunk.SharedValues, out var bucket))
            throw new InvalidOperationException("A shared chunk must be registered before it becomes open.");

        bucket.MarkOpen(chunk);
    }

    private static void UnregisterSharedChunk(Archetype archetype, Chunk chunk)
    {
        if (chunk.SharedValues is null)
            return;

        SharedComponentTuple key = chunk.SharedValues;
        if (!archetype.TryGetSharedChunkBucket(key, out var bucket))
            return;

        bucket.Unregister(chunk);
        if (bucket.ChunkCount == 0)
            archetype.RemoveSharedChunkBucket(key);
    }

    internal static class ChunkCapacity
    {
        internal static int NextUnshared(Archetype archetype)
        {
            int capacity = archetype.NextChunkRows;
            archetype.NextChunkRows = Grow(archetype, capacity);
            return capacity;
        }

        internal static int Shared(Archetype archetype, SharedChunkBucket? bucket)
        {
            if (bucket is null)
                return archetype.InitialChunkRows;

            return Grow(archetype, bucket.LastCapacity);
        }

        internal static int Reserved(Archetype archetype, int remainingRows)
        {
            if (remainingRows <= archetype.InitialChunkRows)
                return archetype.InitialChunkRows;

            return Math.Min(archetype.MaxChunkRows, remainingRows);
        }

        internal static int Grow(Archetype archetype, int currentCapacity)
        {
            if (currentCapacity >= archetype.MaxChunkRows)
                return archetype.MaxChunkRows;

            int doubled = currentCapacity <= int.MaxValue / 2
                ? currentCapacity * 2
                : archetype.MaxChunkRows;
            return Math.Min(archetype.MaxChunkRows, Math.Max(currentCapacity + 1, doubled));
        }
    }
}

