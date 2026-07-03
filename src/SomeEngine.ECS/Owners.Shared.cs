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

internal sealed class Shared
{
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Journal _journal = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;

    internal SharedStores Stores { get; } = new();

    internal void Bind(
        Entities entities,
        Tables tables,
        Journal journal,
        Clock clock,
        Iteration iteration)
    {
        _entities = entities;
        _tables = tables;
        _journal = journal;
        _clock = clock;
        _iteration = iteration;
    }

    internal void Add<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        _iteration.Throw();

        int componentId = ComponentMetadata<T>.Id;
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;
        if (archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} already has shared component {typeof(T).Name}.");

        int sharedIndex = AddIndex(componentId, in value);
        Attach(entity, ref record, archetype, componentId, sharedIndex);
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        _iteration.Throw();

        int componentId = ComponentMetadata<T>.Id;
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have shared component {typeof(T).Name}.");

        int sharedIndex = AddIndex(componentId, in value);
        Move(entity, ref record, archetype, componentId, sharedIndex);
    }

    internal void Merge<T>(Entity entity, in T value)
        where T : struct, ISharedComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;
        int sharedIndex = AddIndex(componentId, in value);

        if (archetype.HasComponent(componentId))
            Move(entity, ref record, archetype, componentId, sharedIndex);
        else
            Attach(entity, ref record, archetype, componentId, sharedIndex);
    }

    internal T Get<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        ref var record = ref _entities.Row(entity);

        if (!record.Archetype!.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have shared component {typeof(T).Name}.");

        int sharedIndex = EntityIndex(record.Archetype, record.Chunk!, componentId);
        return Stores.Store<T>(componentId).GetValue(sharedIndex);
    }

    internal void Remove<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        Remove(entity, ComponentMetadata<T>.Id, typeof(T).Name);
    }

    private void Remove(Entity entity, int componentId, string name)
    {
        _iteration.Throw();

        ref var record = ref _entities.Row(entity);
        var archetype = record.Archetype!;

        if (!archetype.HasComponent(componentId))
            throw new InvalidOperationException(
                $"Entity {entity} does not have shared component {name}.");

        var edge = _tables.Registry.RemoveEdge(archetype, componentId);
        _tables.MoveEntity(entity, ref record, edge);
        Write(SerializationChangeKind.SharedRemoved, entity, componentId);
    }

    internal bool Has<T>(Entity entity)
        where T : struct, ISharedComponent
    {
        if (!_entities.Alive(entity))
            return false;

        ref var record = ref _entities.Store.GetRecord(entity);
        return record.Archetype is not null && record.Archetype.HasComponent(ComponentMetadata<T>.Id);
    }

    internal int AddIndex<T>(int componentId, in T value)
        where T : struct
    {
        return Stores.Store<T>(componentId).GetOrAdd(value);
    }

    internal bool TryIndex<T>(int componentId, in T value, out int sharedIndex)
        where T : struct
    {
        sharedIndex = default;
        return Stores.TryGetStore<T>(componentId, out var store) &&
            store.TryGetIndex(value, out sharedIndex);
    }

    internal void MoveTo(
        Entity entity,
        ref EntityRecord record,
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

        for (int columnIndex = 0; columnIndex < archetype.ColumnMetas.Length; columnIndex++)
        {
            unsafe
            {
                archetype.ColumnMetas[columnIndex].Operations.CopyElement(
                    sourceChunk.Columns[columnIndex],
                    sourceRow,
                    destinationChunk.Columns[columnIndex],
                    destinationRow);
            }

            sourceChunk.CopyVersions(columnIndex, sourceRow, columnIndex, destinationRow, destinationChunk);

            int componentId = archetype.ColumnMetas[columnIndex].ComponentId;
            if (archetype.TryMask(componentId, out int maskIndex))
            {
                bool enabled = sourceChunk.IsEnabled(maskIndex, sourceRow);
                destinationChunk.WriteEnabled(maskIndex, destinationRow, enabled);
            }
        }

        var movedEntity = sourceChunk.RemoveRow(sourceRow, archetype.ColumnMetas);
        if (movedEntity != Entity.Null)
        {
            ref var movedRecord = ref _entities.Store.GetRecord(movedEntity);
            movedRecord.RowInChunk = sourceRow;
        }

        _tables.TryRecycleChunk(archetype, sourceChunk);

        record.Chunk = destinationChunk;
        record.RowInChunk = destinationRow;
    }

    internal void Reset()
    {
        Stores.Clear();
    }

    internal static int Slot(Archetype archetype, int componentId)
    {
        int slot = Array.BinarySearch(archetype.SharedComponentIds, componentId);
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

    internal static bool ValuesMatch(int[]? chunkValues, ReadOnlySpan<int> entityValues)
    {
        return chunkValues is not null && chunkValues.AsSpan().SequenceEqual(entityValues);
    }

    private void Attach(
        Entity entity,
        ref EntityRecord record,
        Archetype archetype,
        int componentId,
        int sharedIndex)
    {
        var edge = _tables.Registry.AddEdge(archetype, componentId);
        _tables.MoveWithShared(entity, ref record, edge.AsTransition(), componentId, sharedIndex);
        Write(SerializationChangeKind.SharedAdded, entity, componentId);
    }

    private void Move(
        Entity entity,
        ref EntityRecord record,
        Archetype archetype,
        int componentId,
        int sharedIndex)
    {
        int oldSharedIndex = EntityIndex(archetype, record.Chunk!, componentId);
        if (oldSharedIndex == sharedIndex)
            return;

        Span<int> sharedValues = stackalloc int[archetype.SharedComponentIds.Length];
        FillChunk(archetype, record.Chunk!, sharedValues);
        sharedValues[Slot(archetype, componentId)] = sharedIndex;
        MoveTo(entity, ref record, sharedValues);
        Write(SerializationChangeKind.SharedChanged, entity, componentId);
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

    private void Write(SerializationChangeKind kind, Entity entity, int componentId)
    {
        _journal.Write(kind, entity, componentId, default, _clock.Tick);
    }
}


