using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Owners;

internal sealed class Tables
{
    private readonly Entities _entities;

    internal Tables(Entities entities, Action<Archetype> onArchetype)
    {
        _entities = entities;
        Registry = new ArchetypeRegistry();
        Registry.OnArchetypeCreated = onArchetype;
        Empty = Registry.GetOrCreate(ReadOnlySpan<int>.Empty);
    }

    internal ArchetypeRegistry Registry { get; }

    internal Archetype Empty { get; }

    internal IReadOnlyList<Archetype> All => Registry.AllArchetypes;

    internal int Count => Registry.AllArchetypes.Count;

    internal void MoveEntity(
        Entity entity,
        ref EntityRecord record,
        ArchetypeEdge edge)
    {
        MoveEntity(entity, ref record, edge.AsTransition());
    }

    internal void MoveEntity(
        Entity entity,
        ref EntityRecord record,
        StructuralTransition plan)
    {
        MoveRow(entity, ref record, plan.Target, plan.SharedColumns);
    }

    internal void MoveWithShared(
        Entity entity,
        ref EntityRecord record,
        StructuralTransition plan,
        int sharedComponentId,
        int sharedIndex)
    {
        var destination = plan.Target;
        Span<int> sharedValues = stackalloc int[destination.SharedComponentIds.Length];
        FillEntityShared(entity, destination, sharedValues);
        sharedValues[Shared.Slot(destination, sharedComponentId)] = sharedIndex;
        MoveRow(entity, ref record, destination, plan.SharedColumns, sharedValues);
    }

    internal void MoveRow(
        Entity entity,
        ref EntityRecord record,
        Archetype destination,
        ReadOnlySpan<SharedColumnMapping> mappings)
    {
        MoveRow(entity, ref record, destination, mappings, ReadOnlySpan<int>.Empty);
    }

    internal void MoveRow(
        Entity entity,
        ref EntityRecord record,
        Archetype destination,
        ReadOnlySpan<SharedColumnMapping> mappings,
        ReadOnlySpan<int> destinationSharedValues)
    {
        var sourceArchetype = record.Archetype!;
        var sourceChunk = record.Chunk!;
        int sourceRow = record.RowInChunk;

        var (destinationChunk, destinationRow) = destinationSharedValues.Length > 0
            ? AllocateShared(destination, entity, destinationSharedValues)
            : AllocateInChunk(destination, entity);

        foreach (var mapping in mappings)
        {
            unsafe
            {
                mapping.Operations.CopyElement(
                    sourceChunk.Columns[mapping.SourceColumnIndex],
                    sourceRow,
                    destinationChunk.Columns[mapping.DestinationColumnIndex],
                    destinationRow
                );
            }

            sourceChunk.CopyVersions(
                mapping.SourceColumnIndex,
                sourceRow,
                mapping.DestinationColumnIndex,
                destinationRow,
                destinationChunk);

            int componentId = sourceArchetype.ColumnMetas[mapping.SourceColumnIndex].ComponentId;
            if (sourceArchetype.TryMask(componentId, out int sourceMaskIndex))
            {
                int destinationMaskIndex = destination.EnableMask(componentId);
                bool enabled = sourceChunk.IsEnabled(sourceMaskIndex, sourceRow);
                destinationChunk.WriteEnabled(destinationMaskIndex, destinationRow, enabled);
            }
        }

        var movedEntity = sourceChunk.RemoveRow(sourceRow, sourceArchetype.ColumnMetas);
        if (movedEntity != Entity.Null)
        {
            ref var movedRecord = ref _entities.Store.GetRecord(movedEntity);
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
            Span<int> sharedValues = stackalloc int[archetype.SharedComponentIds.Length];
            FillEntityShared(entityId, archetype, sharedValues);
            return AllocateShared(archetype, entityId, sharedValues);
        }

        return AllocateFast(archetype, entityId);
    }

    internal (Chunk chunk, int row) AllocateShared(
        Archetype archetype,
        Entity entityId,
        ReadOnlySpan<int> sharedValues)
    {
        var lookup = archetype.SharedChunkBuckets.GetAlternateLookup<ReadOnlySpan<int>>();
        if (lookup.TryGetValue(sharedValues, out var bucket))
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                var chunk = bucket[i];
                if (!chunk.IsFull)
                {
                    int row = chunk.AllocateRow(entityId);
                    return (chunk, row);
                }
            }
        }

        int capacity = ChunkCapacity.Shared(archetype, bucket);
        return AllocateNewChunk(archetype, entityId, sharedValues.ToArray(), capacity);
    }

    internal void EnsureCapacity(Archetype archetype, int additionalEntityCapacity)
    {
        if (additionalEntityCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalEntityCapacity));

        if (archetype.SharedComponentIds.Length != 0)
            return;

        int freeCapacity = 0;
        for (int i = 0; i < archetype.Chunks.Count; i++)
            freeCapacity += archetype.Chunks[i].Capacity - archetype.Chunks[i].Count;

        if (freeCapacity >= additionalEntityCapacity)
            return;

        int additionalRows = additionalEntityCapacity - freeCapacity;
        archetype.Chunks.Capacity = Math.Max(
            archetype.Chunks.Capacity,
            archetype.Chunks.Count +
            (additionalRows + archetype.MaxChunkRows - 1) / archetype.MaxChunkRows);

        while (additionalRows > 0)
        {
            int capacity = ChunkCapacity.Reserved(archetype, additionalRows);
            var chunk = new Chunk(
                capacity,
                archetype.ColumnMetas,
                archetype.EnableableComponentIds.Length);
            chunk.IndexInArchetype = archetype.Chunks.Count;
            archetype.Chunks.Add(chunk);
            additionalRows -= capacity;
        }

        archetype.NextChunkRows = archetype.MaxChunkRows;
    }

    internal void TryRecycleChunk(Archetype archetype, Chunk chunk)
    {
        int chunkIndex = chunk.IndexInArchetype;

        if (chunkIndex < archetype.FirstOpenChunk)
            archetype.FirstOpenChunk = chunkIndex;

        if (
            chunkIndex < archetype.Chunks.Count
            && chunk.Count == 0
            && archetype.Chunks.Count > 1
        )
        {
            int lastIndex = archetype.Chunks.Count - 1;
            UnregisterSharedChunk(archetype, chunk);
            if (chunkIndex != lastIndex)
            {
                var movedChunk = archetype.Chunks[lastIndex];
                archetype.Chunks[chunkIndex] = movedChunk;
                movedChunk.IndexInArchetype = chunkIndex;
            }
            archetype.Chunks.RemoveAt(lastIndex);

            if (archetype.FirstOpenChunk >= archetype.Chunks.Count)
                archetype.FirstOpenChunk = Math.Max(0, archetype.Chunks.Count - 1);
        }
    }

    internal Entity[] LiveEntities(int capacity)
    {
        var entities = new List<Entity>(capacity);
        foreach (var archetype in All)
        {
            foreach (var chunk in archetype.Chunks)
            {
                for (int row = 0; row < chunk.Count; row++)
                    entities.Add(chunk.Entities[row]);
            }
        }

        return entities.ToArray();
    }

    private (Chunk chunk, int row) AllocateFast(
        Archetype archetype,
        Entity entityId)
    {
        int hint = archetype.FirstOpenChunk;
        if (hint < archetype.Chunks.Count && !archetype.Chunks[hint].IsFull)
        {
            var chunk = archetype.Chunks[hint];
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
        int[]? sharedValues,
        int capacity)
    {
        var newChunk = new Chunk(
            capacity,
            archetype.ColumnMetas,
            archetype.EnableableComponentIds.Length
        );
        newChunk.SharedValues = sharedValues;
        archetype.Chunks.Add(newChunk);
        RegisterSharedChunk(archetype, newChunk);
        int newIndex = archetype.Chunks.Count - 1;
        newChunk.IndexInArchetype = newIndex;
        archetype.FirstOpenChunk = newIndex;
        int newRow = newChunk.AllocateRow(entityId);
        if (newChunk.IsFull)
            archetype.FirstOpenChunk = newIndex + 1;
        return (newChunk, newRow);
    }

    private bool TryPromoteChunk(Archetype archetype, out Chunk promotedChunk)
    {
        promotedChunk = null!;
        if (archetype.SharedComponentIds.Length != 0 ||
            archetype.Chunks.Count != 1)
        {
            return false;
        }

        var source = archetype.Chunks[0];
        if (!source.IsFull || source.Capacity >= archetype.MaxChunkRows)
            return false;

        int promotedCapacity = ChunkCapacity.Grow(archetype, source.Capacity);
        if (promotedCapacity <= source.Capacity)
            return false;

        promotedChunk = new Chunk(
            promotedCapacity,
            archetype.ColumnMetas,
            archetype.EnableableComponentIds.Length)
        {
            Count = source.Count,
            OrderVersion = source.OrderVersion,
            IndexInArchetype = 0,
        };

        Array.Copy(source.Entities, promotedChunk.Entities, source.Count);
        for (int column = 0; column < source.Columns.Length; column++)
            Array.Copy((Array)source.Columns[column], (Array)promotedChunk.Columns[column], source.Count);

        Array.Copy(source.ChangeVersions, promotedChunk.ChangeVersions, source.ChangeVersions.Length);
        for (int column = 0; column < source.ChangeVersions.Length; column++)
        {
            Array.Copy(source.AddVersions[column], promotedChunk.AddVersions[column], source.Count);
            Array.Copy(source.WriteVersions[column], promotedChunk.WriteVersions[column], source.Count);
        }

        if (source.EnableMasks is not null)
            Array.Copy(source.EnableMasks, promotedChunk.EnableMasks!, source.EnableMasks.Length);

        for (int row = 0; row < source.Count; row++)
        {
            ref var record = ref _entities.Store.GetRecord(source.Entities[row]);
            record.Archetype = archetype;
            record.Chunk = promotedChunk;
            record.RowInChunk = row;
        }

        archetype.Chunks[0] = promotedChunk;
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

        ref var record = ref _entities.Store.GetRecord(entity);
        var sourceArchetype = record.Archetype!;
        var sourceSharedValues = record.Chunk!.SharedValues;

        for (int i = 0; i < sharedIds.Length; i++)
        {
            int sourceSlot = Array.BinarySearch(sourceArchetype.SharedComponentIds, sharedIds[i]);
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

        var key = new SortedValueKey(chunk.SharedValues);
        if (!archetype.SharedChunkBuckets.TryGetValue(key, out var bucket))
        {
            bucket = new List<Chunk>(1);
            archetype.SharedChunkBuckets.Add(key, bucket);
        }

        bucket.Add(chunk);
    }

    private static void UnregisterSharedChunk(Archetype archetype, Chunk chunk)
    {
        if (chunk.SharedValues is null)
            return;

        var key = new SortedValueKey(chunk.SharedValues);
        if (!archetype.SharedChunkBuckets.TryGetValue(key, out var bucket))
            return;

        bucket.Remove(chunk);
        if (bucket.Count == 0)
            archetype.SharedChunkBuckets.Remove(key);
    }

    internal static class ChunkCapacity
    {
        internal static int NextUnshared(Archetype archetype)
        {
            int capacity = archetype.NextChunkRows;
            archetype.NextChunkRows = Grow(archetype, capacity);
            return capacity;
        }

        internal static int Shared(Archetype archetype, List<Chunk>? bucket)
        {
            if (bucket is null || bucket.Count == 0)
                return archetype.InitialChunkRows;

            return Grow(archetype, bucket[^1].Capacity);
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

