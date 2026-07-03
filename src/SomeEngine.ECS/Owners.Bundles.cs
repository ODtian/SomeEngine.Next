using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Bundles
{
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Components _components = null!;
    private Buffers _buffers = null!;
    private Shared _shared = null!;
    private Sparse _sparse = null!;
    private Indices _indices = null!;
    private Hooks _hooks = null!;
    private Journal _journal = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;
    private Hierarchy _hierarchy = null!;
    private readonly Dictionary<SortedValueKey, BundleSpawnMap> _plans =
        new(SortedValueComparer.Instance);
    private int[]? _key;
    private BundleSpawnMap? _plan;

    internal void Bind(
        Entities entities,
        Tables tables,
        Components components,
        Buffers buffers,
        Shared shared,
        Sparse sparse,
        Indices indices,
        Hooks hooks,
        Journal journal,
        Clock clock,
        Iteration iteration,
        Hierarchy hierarchy)
    {
        _entities = entities;
        _tables = tables;
        _components = components;
        _buffers = buffers;
        _shared = shared;
        _sparse = sparse;
        _indices = indices;
        _hooks = hooks;
        _journal = journal;
        _clock = clock;
        _iteration = iteration;
        _hierarchy = hierarchy;
    }

    internal BundleWriter CreateSpawnWriter(Span<int> componentIds)
    {
        return SpawnWriter(componentIds, ReadOnlySpan<SharedValueSlot>.Empty);
    }

    internal BundleWriter CreateSpawnWriter(
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        return SpawnWriter(componentIds, sharedValues);
    }

    internal BundleWriter CreateAddWriter(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues,
        ReadOnlySpan<int> sparseComponentIds)
    {
        ValidateAdd(entity, componentIds, sparseComponentIds);
        return Prepare(entity, componentIds, sharedValues, BundleWriteMode.Add);
    }

    internal BundleWriter CreateReplaceWriter(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues,
        ReadOnlySpan<int> sparseComponentIds)
    {
        ValidateReplace(entity, componentIds, sparseComponentIds);
        return Prepare(entity, componentIds, sharedValues, BundleWriteMode.Replace);
    }

    internal SharedValueSlot SharedValue<T>(in SharedComponentValue<T> value)
        where T : struct, ISharedComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        int sharedIndex = _shared.AddIndex(componentId, value.Value);
        return new SharedValueSlot(componentId, sharedIndex);
    }

    internal void Reserve(ReadOnlySpan<int> componentIds, int entityCapacity)
    {
        _iteration.Throw();

        var plan = ResolveMap(componentIds);
        _entities.Store.EnsureAdditionalCapacity(entityCapacity);
        _tables.EnsureCapacity(plan.Archetype, entityCapacity);
    }

    internal BundleWriter CreateLoadWriter(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        _iteration.Throw();

        var plan = ResolveMap(componentIds);
        var archetype = plan.Archetype;
        if (sharedValues.Length > 0 || plan.HasSharedComponents)
            SharedValues.Validate(plan.ComponentIds, sharedValues);

        ref var record = ref _entities.Store.AllocatePreserved(entity);
        var (chunk, row) = AllocateRow(archetype, entity, sharedValues);
        record.Archetype = archetype;
        record.Chunk = chunk;
        record.RowInChunk = row;

        return new BundleWriter(
            this,
            entity,
            null,
            archetype,
            plan,
            chunk,
            row,
            BundleWriteMode.Spawn
        );
    }

    internal BundleBatch SpawnBatch<T>(int count)
        where T : struct, IComponent
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        _iteration.Throw();

        Span<int> componentIds = [ComponentMetadata<T>.Id];
        var plan = ResolveSortedMap(componentIds);
        return SpawnRows(plan, count);
    }

    internal BundleBatch SpawnBatch(ReadOnlySpan<int> componentIds, int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        _iteration.Throw();

        var plan = ResolveMap(componentIds);
        return SpawnRows(plan, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteEntity<T>(
        Entity entity,
        Archetype? sourceArchetype,
        Archetype archetype,
        Chunk chunk,
        int row,
        in T value,
        BundleWriteMode mode)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        bool isAdded = sourceArchetype is null || !sourceArchetype.HasComponent(componentId);
        if (mode == BundleWriteMode.Replace)
        {
            if (isAdded)
                throw new InvalidOperationException(
                    $"Entity {entity} does not have component ID {componentId}.");

            _components.Replace(entity, value);
            return;
        }

        if (mode == BundleWriteMode.Add && !isAdded)
            throw new InvalidOperationException(
                $"Entity {entity} already has component ID {componentId}.");

        int columnIndex = archetype.Column(componentId);
        _components.WriteAdded(entity, archetype, chunk, row, columnIndex, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteSpawn<T>(
        Entity entity,
        BundleSpawnMap plan,
        Chunk chunk,
        int row,
        in T value)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        int columnIndex = plan.Column(componentId);
        if (columnIndex < 0)
            ThrowMissing<T>();

        var archetype = plan.Archetype;
        _components.WriteAdded(entity, archetype, chunk, row, columnIndex, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteSparse<T>(Entity entity, in T value, BundleWriteMode mode)
        where T : struct, ISparseComponent
    {
        if (mode == BundleWriteMode.Replace)
        {
            _sparse.Replace(entity, value);
            return;
        }

        _sparse.Add(entity, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteBuffer<T>(
        Entity entity,
        ReadOnlySpan<T> values,
        BundleWriteMode mode)
        where T : struct, IBufferElement
    {
        var buffer = _buffers.Get<T>(entity);
        var kind = mode == BundleWriteMode.Replace
            ? SerializationChangeKind.BufferChanged
            : SerializationChangeKind.BufferAdded;
        buffer.ReplaceWith(values, kind);
    }

    internal void CompleteBatch(BundleSpawnMap plan, ReadOnlySpan<BundleBatchChunk> chunks)
    {
        bool recordJournal = !_journal.Suppressed;
        bool fixIndex = _indices.Any;
        bool runHooks = _hooks.Any;
        if ((chunks.Length == 0 || (!fixIndex && !runHooks)) && !recordJournal)
            return;

        var archetype = plan.Archetype;
        var columns = archetype.ColumnMetas;

        for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            var batchChunk = chunks[chunkIndex];
            var entities = batchChunk.Entities;

            for (int column = 0; column < columns.Length; column++)
            {
                int componentId = columns[column].ComponentId;
                var storage = batchChunk.GetColumnStorage(column);

                for (int row = 0; row < entities.Length; row++)
                    _components.CommitAdd(entities[row], componentId, storage, batchChunk.StartRow + row);
            }
        }
    }

    internal BundleSpawnMap ResolveSortedMap(ReadOnlySpan<int> sortedComponentIds)
    {
        if (_key is { } lastComponentIds &&
            sortedComponentIds.SequenceEqual(lastComponentIds) &&
            _plan is { } cached)
        {
            return cached;
        }

        var lookup = _plans.GetAlternateLookup<ReadOnlySpan<int>>();
        if (lookup.TryGetValue(sortedComponentIds, out var plan))
        {
            Cache(plan);
            return plan;
        }

        var archetype = _tables.Registry.GetOrCreate(sortedComponentIds);
        plan = new BundleSpawnMap(sortedComponentIds, archetype);
        _plans.Add(new SortedValueKey(plan.ComponentIds), plan);
        Cache(plan);
        return plan;
    }

    internal BundleSpawnMap ResolveMap(ReadOnlySpan<int> componentIds)
    {
        if (componentIds.Length <= 16)
        {
            Span<int> sortedComponentIds = stackalloc int[componentIds.Length];
            componentIds.CopyTo(sortedComponentIds);
            BundleComponents.SortAndValidate(sortedComponentIds);
            return ResolveSortedMap(sortedComponentIds);
        }

        var sortedArray = componentIds.ToArray();
        BundleComponents.SortAndValidate(sortedArray);
        return ResolveSortedMap(sortedArray);
    }
}

internal sealed partial class Bundles
{
    private void Cache(BundleSpawnMap plan)
    {
        _key = plan.ComponentIds;
        _plan = plan;
    }

    private void ValidateAdd(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds)
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);

        ref var record = ref _entities.Store.GetRecord(entity);
        ValidateAdd(record.Archetype!, entity, componentIds);
        ValidateSparseAdd(entity, sparseComponentIds);
    }

    private void ValidateReplace(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds)
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);

        ref var record = ref _entities.Store.GetRecord(entity);
        ValidateReplace(record.Archetype!, entity, componentIds);
        ValidateSparseReplace(entity, sparseComponentIds);
    }

    private BundleWriter Prepare(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues,
        BundleWriteMode mode)
    {
        ValidateWrite(entity, componentIds, sharedValues);

        ref var record = ref _entities.Store.GetRecord(entity);
        var sourceArchetype = record.Archetype!;
        var sourceChunk = record.Chunk!;
        var plan = _tables.Registry.IncludeTransition(sourceArchetype, componentIds);

        bool moved = MoveForWrite(
            entity,
            ref record,
            sourceArchetype,
            sourceChunk,
            plan,
            sharedValues);
        WriteSharedChanges(entity, sourceArchetype, sourceChunk, sharedValues, moved);

        var archetype = record.Archetype!;
        return CreatePreparedWriter(entity, sourceArchetype, archetype, record.Chunk!, record.RowInChunk, mode);
    }

    private BundleWriter SpawnWriter(
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        _iteration.Throw();

        var plan = ResolveSpawnMap(componentIds, sharedValues);
        var archetype = plan.Archetype;

        ref var record = ref _entities.Store.Allocate(out var entity);
        var (chunk, row) = AllocateRow(archetype, entity, sharedValues);
        record.Archetype = archetype;
        record.Chunk = chunk;
        record.RowInChunk = row;
        WriteSpawnJournal(entity, sharedValues);

        return CreateSpawnResult(entity, plan, archetype, chunk, row);
    }

    private void ValidateWrite(
        Entity entity,
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        _iteration.Throw();
        _entities.ThrowDead(entity);
        BundleComponents.SortAndValidate(componentIds);
        SharedValues.Validate(componentIds, sharedValues);
    }

    private BundleWriter CreatePreparedWriter(
        Entity entity,
        Archetype sourceArchetype,
        Archetype archetype,
        Chunk chunk,
        int row,
        BundleWriteMode mode)
    {
        return new BundleWriter(
            this,
            entity,
            sourceArchetype,
            archetype,
            spawnMap: null,
            chunk,
            row,
            mode
        );
    }

    private BundleSpawnMap ResolveSpawnMap(
        Span<int> componentIds,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        BundleComponents.SortAndValidate(componentIds);
        var plan = ResolveSortedMap(componentIds);
        ValidateSpawnShared(plan, sharedValues);
        return plan;
    }

    private BundleWriter CreateSpawnResult(
        Entity entity,
        BundleSpawnMap plan,
        Archetype archetype,
        Chunk chunk,
        int row)
    {
        return new BundleWriter(
            this,
            entity,
            null,
            archetype,
            plan,
            chunk,
            row,
            BundleWriteMode.Spawn
        );
    }

    private bool MoveForWrite(
        Entity entity,
        ref EntityRecord record,
        Archetype sourceArchetype,
        Chunk sourceChunk,
        StructuralTransition plan,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        if (plan.Target.SharedComponentIds.Length > 0)
            return MoveForSharedWrite(entity, ref record, sourceArchetype, sourceChunk, plan, sharedValues);

        if (plan.IsIdentityFor(sourceArchetype))
            return false;

        _tables.MoveEntity(entity, ref record, plan);
        return true;
    }

    private bool MoveForSharedWrite(
        Entity entity,
        ref EntityRecord record,
        Archetype sourceArchetype,
        Chunk sourceChunk,
        StructuralTransition plan,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        Span<int> destinationSharedValues = stackalloc int[plan.Target.SharedComponentIds.Length];
        bool sharedChanged = SharedValues.FillTarget(
            sourceArchetype,
            sourceChunk,
            plan.Target,
            sharedValues,
            destinationSharedValues);

        if (!plan.IsIdentityFor(sourceArchetype))
        {
            _tables.MoveRow(entity, ref record, plan.Target, plan.SharedColumns, destinationSharedValues);
            return true;
        }

        if (!sharedChanged)
            return false;

        _shared.MoveTo(entity, ref record, destinationSharedValues);
        return true;
    }

    private void WriteSharedChanges(
        Entity entity,
        Archetype sourceArchetype,
        Chunk sourceChunk,
        ReadOnlySpan<SharedValueSlot> sharedValues,
        bool moved)
    {
        if (_journal.Suppressed || (!moved && sharedValues.Length == 0))
            return;

        for (int i = 0; i < sharedValues.Length; i++)
        {
            if (SharedValues.Changed(sourceArchetype, sourceChunk, sharedValues[i]))
                WriteSharedChange(entity, sourceArchetype, sharedValues[i]);
        }
    }

    private void WriteSharedChange(
        Entity entity,
        Archetype sourceArchetype,
        SharedValueSlot sharedValue)
    {
        Write(
            sourceArchetype.HasComponent(sharedValue.ComponentId)
                ? SerializationChangeKind.SharedChanged
                : SerializationChangeKind.SharedAdded,
            entity,
            sharedValue.ComponentId);
    }

    private static void ValidateSpawnShared(
        BundleSpawnMap plan,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        if (sharedValues.Length > 0 || plan.HasSharedComponents)
            SharedValues.Validate(plan.ComponentIds, sharedValues);
    }

    private void WriteSpawnJournal(Entity entity, ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        if (_journal.Suppressed)
            return;

        Write(SerializationChangeKind.EntityCreated, entity);
        for (int i = 0; i < sharedValues.Length; i++)
            Write(
                SerializationChangeKind.SharedAdded,
                entity,
                sharedValues[i].ComponentId);
    }
}

internal sealed partial class Bundles
{
    private BundleBatch SpawnRows(BundleSpawnMap plan, int count)
    {
        if (plan.HasSharedComponents)
            throw new NotSupportedException("Bundle batch creation with shared components is not supported yet.");

        if (count == 0)
            return new BundleBatch(this, plan, chunks: null, chunkCount: 0, count: 0);

        bool hasContiguousEntities = _entities.Store.TryAllocateContiguous(count, out int nextEntityIndex);
        if (!hasContiguousEntities)
            _entities.Store.EnsureAdditionalCapacity(count);

        var chunks = ArrayPool<BundleBatchChunk>.Shared.Rent(EstimateChunks(plan.Archetype, count));
        int chunkCount = 0;
        AllocateRows(plan, count, hasContiguousEntities, ref nextEntityIndex, ref chunks, ref chunkCount);
        return new BundleBatch(this, plan, chunks, chunkCount, count);
    }

    private void AllocateRows(
        BundleSpawnMap plan,
        int count,
        bool hasContiguousEntities,
        ref int nextEntityIndex,
        ref BundleBatchChunk[] chunks,
        ref int chunkCount)
    {
        var archetype = plan.Archetype;
        int remaining = count;

        while (remaining > 0)
        {
            var chunk = GetChunk(archetype, remaining);
            int startRow = chunk.Count;
            int take = Math.Min(remaining, chunk.Capacity - startRow);

            if (hasContiguousEntities)
                AllocateContiguous(archetype, chunk, startRow, take, ref nextEntityIndex);
            else
                AllocateEntities(archetype, chunk, startRow, take);

            chunk.Count = startRow + take;
            if (chunk.IsFull)
                archetype.FirstOpenChunk = chunk.IndexInArchetype + 1;

            MarkColumns(archetype, chunk, startRow, take);
            AddChunk(plan, chunk, startRow, take, ref chunks, ref chunkCount);
            remaining -= take;
        }
    }

    private void AllocateContiguous(
        Archetype archetype,
        Chunk chunk,
        int startRow,
        int count,
        ref int nextEntityIndex)
    {
        int firstEntityIndex = nextEntityIndex;
        _entities.Store.InitializeContiguousRecords(archetype, chunk, startRow, count, firstEntityIndex);
        nextEntityIndex += count;

        if (_journal.Suppressed)
            return;

        for (int i = 0; i < count; i++)
        {
            var entity = new Entity(firstEntityIndex + i, generation: 0);
            Write(SerializationChangeKind.EntityCreated, entity);
        }
    }

    private void AllocateEntities(Archetype archetype, Chunk chunk, int startRow, int count)
    {
        bool recordJournal = !_journal.Suppressed;
        for (int i = 0; i < count; i++)
        {
            ref var record = ref _entities.Store.Allocate(out var entity);
            int row = startRow + i;
            chunk.Entities[row] = entity;
            record.Archetype = archetype;
            record.Chunk = chunk;
            record.RowInChunk = row;

            if (recordJournal)
                Write(SerializationChangeKind.EntityCreated, entity);
        }
    }

    private void MarkColumns(Archetype archetype, Chunk chunk, int startRow, int count)
    {
        uint version = _clock.Tick;
        for (int column = 0; column < chunk.ChangeVersions.Length; column++)
            chunk.MarkAddRange(column, startRow, count, version);

        _hierarchy.RequireScan(archetype);

        if (archetype.EnableableComponentIds.Length == 0)
            return;

        var masks = chunk.EnableMasks!;
        UInt128 rowMask = CreateRowMask(startRow, count);
        for (int i = 0; i < masks.Length; i++)
            masks[i] |= rowMask;
    }

    private Chunk GetChunk(Archetype archetype, int remainingRows)
    {
        int hint = archetype.FirstOpenChunk;
        while (hint < archetype.Chunks.Count)
        {
            var chunk = archetype.Chunks[hint];
            if (!chunk.IsFull)
            {
                archetype.FirstOpenChunk = hint;
                return chunk;
            }

            hint++;
        }

        int capacity = Tables.ChunkCapacity.Reserved(archetype, remainingRows);
        var newChunk = new Chunk(
            capacity,
            archetype.ColumnMetas,
            archetype.EnableableComponentIds.Length);
        newChunk.IndexInArchetype = archetype.Chunks.Count;
        archetype.Chunks.Add(newChunk);
        archetype.FirstOpenChunk = newChunk.IndexInArchetype;
        archetype.NextChunkRows = archetype.MaxChunkRows;
        return newChunk;
    }

    private (Chunk chunk, int row) AllocateRow(
        Archetype archetype,
        Entity entity,
        ReadOnlySpan<SharedValueSlot> sharedValues)
    {
        if (archetype.SharedComponentIds.Length == 0)
            return _tables.AllocateInChunk(archetype, entity);

        Span<int> destinationSharedValues = stackalloc int[archetype.SharedComponentIds.Length];
        SharedValues.FillSpawn(archetype, sharedValues, destinationSharedValues);
        return _tables.AllocateShared(archetype, entity, destinationSharedValues);
    }

    private static void ValidateAdd(Archetype source, Entity entity, ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            int componentId = componentIds[i];
            if (source.HasComponent(componentId))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} already has component ID {componentId}."
                );
            }
        }
    }

    private static void ValidateReplace(Archetype source, Entity entity, ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            int componentId = componentIds[i];
            if (!source.HasComponent(componentId))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} does not have component ID {componentId}."
                );
            }
        }
    }

    private void ValidateSparseAdd(Entity entity, ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            int componentId = componentIds[i];
            if (componentId < _sparse.Stores.Length &&
                _sparse.Stores[componentId] is ISparseSet sparseSet &&
                sparseSet.Has(entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} already has sparse component ID {componentId}."
                );
            }
        }
    }

    private void ValidateSparseReplace(Entity entity, ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            int componentId = componentIds[i];
            if (componentId >= _sparse.Stores.Length ||
                _sparse.Stores[componentId] is not ISparseSet sparseSet ||
                !sparseSet.Has(entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} does not have sparse component ID {componentId}."
                );
            }
        }
    }

    private static void AddChunk(
        BundleSpawnMap plan,
        Chunk chunk,
        int startRow,
        int count,
        ref BundleBatchChunk[] chunks,
        ref int chunkCount)
    {
        if (chunkCount == chunks.Length)
            GrowChunks(ref chunks);

        chunks[chunkCount++] = new BundleBatchChunk(plan, chunk, startRow, count);
    }

    private static void GrowChunks(ref BundleBatchChunk[] chunks)
    {
        var old = chunks;
        var grown = ArrayPool<BundleBatchChunk>.Shared.Rent(old.Length * 2);
        old.AsSpan().CopyTo(grown);
        old.AsSpan().Clear();
        ArrayPool<BundleBatchChunk>.Shared.Return(old);
        chunks = grown;
    }

    private void Write(
        SerializationChangeKind kind,
        Entity entity,
        int componentId = 0,
        Entity target = default)
    {
        _journal.Write(kind, entity, componentId, target, _clock.Tick);
    }

    private static int EstimateChunks(Archetype archetype, int count)
    {
        int estimate = (count + archetype.MaxChunkRows - 1) / archetype.MaxChunkRows + 2;
        return Math.Max(1, Math.Min(count, estimate));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 CreateRowMask(int startRow, int count)
    {
        if (count == 0)
            return 0;

        if (count == 128)
            return UInt128.MaxValue;

        return (((UInt128)1 << count) - 1) << startRow;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMissing<T>()
    {
        throw new InvalidOperationException(
            $"Component {typeof(T).Name} is not part of the prepared bundle archetype.");
    }

    private static class SharedValues
    {
        public static void Validate(
            ReadOnlySpan<int> componentIds,
            ReadOnlySpan<SharedValueSlot> sharedValues)
        {
            for (int i = 0; i < sharedValues.Length; i++)
            {
                int componentId = sharedValues[i].ComponentId;
                ref var info = ref ComponentRegistry.Get(componentId);
                if (info.Storage != StoragePath.Shared)
                    throw new InvalidOperationException(
                        $"Bundle shared assignment component ID {componentId} is not a SharedComponent.");

                if (componentIds.BinarySearch(componentId) < 0)
                    throw new InvalidOperationException(
                        $"Bundle shared assignment component ID {componentId} is missing from the bundle component ID collection.");

                for (int j = i + 1; j < sharedValues.Length; j++)
                {
                    if (sharedValues[j].ComponentId == componentId)
                        throw new InvalidOperationException(
                            $"Duplicate shared component ID {componentId} is not allowed in bundle operations.");
                }
            }

            for (int i = 0; i < componentIds.Length; i++)
            {
                ref var info = ref ComponentRegistry.Get(componentIds[i]);
                if (info.Storage != StoragePath.Shared)
                    continue;

                if (!TryFind(sharedValues, componentIds[i], out _))
                    throw new InvalidOperationException(
                        $"Shared component ID {componentIds[i]} requires a SharedComponentValue<T> value in bundle operations.");
            }
        }

        public static void FillSpawn(
            Archetype archetype,
            ReadOnlySpan<SharedValueSlot> sharedValues,
            Span<int> destinationSharedValues)
        {
            for (int i = 0; i < archetype.SharedComponentIds.Length; i++)
            {
                int componentId = archetype.SharedComponentIds[i];
                if (!TryFind(sharedValues, componentId, out int sharedIndex))
                    throw new InvalidOperationException(
                        $"Shared component ID {componentId} requires a SharedComponentValue<T> value in bundle operations.");

                destinationSharedValues[i] = sharedIndex;
            }
        }

        public static bool FillTarget(
            Archetype sourceArchetype,
            Chunk sourceChunk,
            Archetype destinationArchetype,
            ReadOnlySpan<SharedValueSlot> sharedValues,
            Span<int> destinationSharedValues)
        {
            bool changed = false;
            for (int i = 0; i < destinationArchetype.SharedComponentIds.Length; i++)
            {
                int componentId = destinationArchetype.SharedComponentIds[i];
                if (TryFind(sharedValues, componentId, out int sharedIndex))
                {
                    destinationSharedValues[i] = sharedIndex;
                    int oldSlot = Array.BinarySearch(sourceArchetype.SharedComponentIds, componentId);
                    changed |= oldSlot < 0 ||
                        sourceChunk.SharedValues is null ||
                        sourceChunk.SharedValues[oldSlot] != sharedIndex;
                    continue;
                }

                int sourceSlot = Array.BinarySearch(sourceArchetype.SharedComponentIds, componentId);
                if (sourceSlot < 0 || sourceChunk.SharedValues is null)
                    throw new InvalidOperationException(
                        $"Shared component ID {componentId} is missing a value for destination archetype {destinationArchetype.ArchetypeId}.");

                destinationSharedValues[i] = sourceChunk.SharedValues[sourceSlot];
            }

            return changed;
        }

        public static bool Changed(
            Archetype sourceArchetype,
            Chunk sourceChunk,
            SharedValueSlot sharedValue)
        {
            int oldSlot = Array.BinarySearch(sourceArchetype.SharedComponentIds, sharedValue.ComponentId);
            return oldSlot < 0 ||
                   sourceChunk.SharedValues is null ||
                   sourceChunk.SharedValues[oldSlot] != sharedValue.SharedIndex;
        }

        private static bool TryFind(
            ReadOnlySpan<SharedValueSlot> sharedValues,
            int componentId,
            out int sharedIndex)
        {
            for (int i = 0; i < sharedValues.Length; i++)
            {
                if (sharedValues[i].ComponentId == componentId)
                {
                    sharedIndex = sharedValues[i].SharedIndex;
                    return true;
                }
            }

            sharedIndex = default;
            return false;
        }
    }

    internal void Reset()
    {
        _plans.Clear();
        _key = null;
        _plan = null;
    }
}



