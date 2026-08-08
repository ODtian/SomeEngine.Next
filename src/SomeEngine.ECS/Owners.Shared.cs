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
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Shared
{
    private const int MaximumStackSharedValues = 256;
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Iteration _iteration = null!;

    internal Shared()
        : this(new SharedStores())
    {
    }

    internal Shared(SharedStores stores)
    {
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
    }

    private readonly SharedStores _stores;

    internal Shared CloneDetached() => new(_stores.CloneExact());

    internal int StoreDetachCount<T>(int componentId)
        where T : struct =>
        _stores.Store<T>(componentId).DetachCount;

    internal bool SharesStoreObjectWith<T>(Shared other, int componentId)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(
            _stores.Store<T>(componentId),
            other._stores.Store<T>(componentId));
    }

    internal bool SharesStoreBackingWith<T>(Shared other, int componentId)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(
            _stores.Store<T>(componentId).BackingIdentity,
            other._stores.Store<T>(componentId).BackingIdentity);
    }

    internal void Bind(
        Entities entities,
        Tables tables,
        Iteration iteration)
    {
        _entities = entities;
        _tables = tables;
        _iteration = iteration;
    }

    internal void Add<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        _iteration.Throw();

        int componentId = ComponentMetadata<T>.Id;
        EntityRecordWriter record = _entities.Row(entity);
        var archetype = record.Archetype!;
        if (archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} already has shared component {typeof(T).Name}.");

        int sharedIndex = AddIndex(componentId, in value);
        Attach(entity, record, archetype, componentId, sharedIndex);
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        _iteration.Throw();

        int componentId = ComponentMetadata<T>.Id;
        EntityRecordWriter record = _entities.Row(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have shared component {typeof(T).Name}.");

        int sharedIndex = AddIndex(componentId, in value);
        Move(entity, record, archetype, componentId, sharedIndex);
    }

    internal void Merge<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        EntityRecordWriter record = _entities.Row(entity);
        var archetype = record.Archetype!;
        int sharedIndex = AddIndex(componentId, in value);

        if (archetype.HasComponent(componentId))
            Move(entity, record, archetype, componentId, sharedIndex);
        else
            Attach(entity, record, archetype, componentId, sharedIndex);
    }

    internal T Get<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        EntityRecord record = _entities.ReadRow(entity);

        if (!record.Archetype!.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have shared component {typeof(T).Name}.");

        int sharedIndex = EntityIndex(record.Archetype, record.Chunk!, componentId);
        return _stores.Store<T>(componentId).GetValue(sharedIndex);
    }

    internal ref readonly T GetRef<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        EntityRecord record = _entities.ReadRow(entity);

        if (!record.Archetype!.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have shared component {typeof(T).Name}.");

        int sharedIndex = EntityIndex(record.Archetype, record.Chunk!, componentId);
        return ref _stores.Store<T>(componentId).GetValueRef(sharedIndex);
    }

    internal void Remove<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        Remove(entity, ComponentMetadata<T>.Id, typeof(T).Name);
    }

    private void Remove(Entity entity, int componentId, string name)
    {
        _iteration.Throw();

        EntityRecordWriter record = _entities.Row(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have shared component {name}.");

        var edge = _tables.Registry.RemoveEdge(archetype, componentId);
        _tables.MoveEntity(entity, record, edge);
    }

    internal bool Has<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        if (!_entities.Alive(entity))
            return false;

        EntityRecord record = _entities.Store.GetRecordReadOnly(entity);
        return record.Archetype is not null && record.Archetype.HasComponent(ComponentMetadata<T>.Id);
    }

    internal int AddIndex<T>(int componentId, in T value)
        where T : struct
    {
        return _stores.Store<T>(componentId).GetOrAdd(value);
    }

    internal bool TryIndex<T>(int componentId, in T value, out int sharedIndex)
        where T : struct
    {
        sharedIndex = default;
        return _stores.TryGetStore<T>(componentId, out var store) &&
            store.TryGetIndex(value, out sharedIndex);
    }

    internal void MoveTo(
        Entity entity,
        EntityRecordWriter record,
        ReadOnlySpan<int> expectedValues)
    {
        var archetype = record.Archetype!;
        if (archetype.SharedComponentIds.Length == 0)
            return;

        if (ValuesMatch(record.Chunk!.SharedValues, expectedValues))
            return;

        var sourceChunk = record.Chunk!;
        int sourceRow = record.RowInChunk;

        var (destinationChunk, destinationRow) = _tables.AllocateShared(archetype, entity, expectedValues);
        // ValuesMatch above ruled out the source bucket. Keep the source backing read-only during
        // the copy and let RemoveRow perform its single required detach afterward.
        if (ReferenceEquals(sourceChunk, destinationChunk))
        {
            throw new InvalidOperationException(
                "A shared-value row move cannot allocate into its source chunk.");
        }

        for (int columnIndex = 0; columnIndex < archetype.TableComponentIds.Length; columnIndex++)
        {
            sourceChunk.CopyComponentTo(
                columnIndex,
                sourceRow,
                destinationChunk,
                columnIndex,
                destinationRow,
                in archetype.ColumnOperations[columnIndex]);

            sourceChunk.CopyVersions(columnIndex, sourceRow, columnIndex, destinationRow, destinationChunk);

            int componentId = archetype.TableComponentIds[columnIndex];
            if (archetype.TryMask(componentId, out int maskIndex))
            {
                bool enabled = sourceChunk.IsEnabled(maskIndex, sourceRow);
                destinationChunk.WriteEnabled(maskIndex, destinationRow, enabled);
            }
        }

        var movedEntity = sourceChunk.RemoveRow(sourceRow, archetype.ColumnOperations);
        if (movedEntity != Entity.Null)
        {
            EntityRecordWriter movedRecord = _entities.Store.GetRecord(movedEntity);
            movedRecord.RowInChunk = sourceRow;
        }

        _tables.TryRecycleChunk(archetype, sourceChunk);

        record.Chunk = destinationChunk;
        record.RowInChunk = destinationRow;
    }

    internal void Reset()
    {
        _stores.Clear();
    }

    internal static int Slot(Archetype archetype, int componentId)
    {
        int slot = archetype.SharedComponentIds.BinarySearch(componentId);
        if (slot < 0)
            throw new InvalidOperationException(
                $"Component ID {componentId} is not a shared component of Archetype {archetype.ArchetypeId}.");

        return slot;
    }

    internal static int EntityIndex(Archetype archetype, Chunk chunk, int componentId)
    {
        int slot = Slot(archetype, componentId);
        if (chunk.SharedValues is null || slot >= chunk.SharedValues.Length)
            throw new InvalidOperationException(
                $"Archetype {archetype.ArchetypeId} has shared component ID {componentId}, but its chunk has no shared value tuple.");

        return chunk.SharedValues[slot];
    }

    internal static bool ValuesMatch(
        SharedComponentTuple? chunkValues,
        ReadOnlySpan<int> entityValues)
    {
        return chunkValues is not null && chunkValues.AsSpan().SequenceEqual(entityValues);
    }

    private void Attach(
        Entity entity,
        EntityRecordWriter record,
        Archetype archetype,
        int componentId,
        int sharedIndex)
    {
        var edge = _tables.Registry.AddEdge(archetype, componentId);
        _tables.MoveWithShared(entity, record, edge, componentId, sharedIndex);
    }

    private void Move(
        Entity entity,
        EntityRecordWriter record,
        Archetype archetype,
        int componentId,
        int sharedIndex)
    {
        int oldSharedIndex = EntityIndex(archetype, record.Chunk!, componentId);
        if (oldSharedIndex == sharedIndex)
            return;

        int count = archetype.SharedComponentIds.Length;
        int[]? rented = null;
        Span<int> sharedValues = count <= MaximumStackSharedValues
            ? stackalloc int[count]
            : (rented = ArrayPool<int>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            FillChunk(archetype, record.Chunk!, sharedValues);
            sharedValues[Slot(archetype, componentId)] = sharedIndex;
            MoveTo(entity, record, sharedValues);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static void FillChunk(Archetype archetype, Chunk chunk, Span<int> values)
    {
        if (values.Length != archetype.SharedComponentIds.Length)
            throw new ArgumentException(
                "Shared value span length must match archetype shared component count.",
                nameof(values));

        if (values.Length == 0)
            return;

        if (chunk.SharedValues is null || chunk.SharedValues.Length != values.Length)
            throw new InvalidOperationException(
                $"Archetype {archetype.ArchetypeId} has shared components, but its chunk has no matching shared value tuple.");

        chunk.SharedValues.AsSpan().CopyTo(values);
    }

}


