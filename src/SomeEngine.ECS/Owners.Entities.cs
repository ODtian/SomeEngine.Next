using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Owners;

internal sealed class Entities
{
    private Tables _tables = null!;
    private Relations _relations = null!;
    private Components _components = null!;
    private Journal _journal = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;
    private Hierarchy _hierarchy = null!;

    internal Entities(int capacity)
    {
        Store = new EntityStore(capacity);
    }

    internal EntityStore Store { get; }

    internal void Bind(
        Tables tables,
        Relations relations,
        Components components,
        Journal journal,
        Clock clock,
        Iteration iteration,
        Hierarchy hierarchy)
    {
        _tables = tables;
        _relations = relations;
        _components = components;
        _journal = journal;
        _clock = clock;
        _iteration = iteration;
        _hierarchy = hierarchy;
    }

    internal Entity Create()
    {
        var entity = Store.Allocate();
        var (chunk, row) = _tables.AllocateInChunk(_tables.Empty, entity);
        ref var record = ref Store.GetRecord(entity);
        record.Archetype = _tables.Empty;
        record.Chunk = chunk;
        record.RowInChunk = row;
        Write(SerializationChangeKind.EntityCreated, entity);
        return entity;
    }

    internal Entity Reserve()
    {
        return Store.Allocate();
    }

    internal void Spawn(Entity entity)
    {
        if (!Store.IsAlive(entity))
            throw new InvalidOperationException($"Cannot spawn {entity}: reserved entity is not alive.");

        ref var record = ref Store.GetRecord(entity);
        if (record.Archetype is not null)
            throw new InvalidOperationException($"Cannot spawn {entity}: reserved entity was already spawned.");

        var (chunk, row) = _tables.AllocateInChunk(_tables.Empty, entity);
        record.Archetype = _tables.Empty;
        record.Chunk = chunk;
        record.RowInChunk = row;
        Write(SerializationChangeKind.EntityCreated, entity);
    }

    internal bool Release(Entity entity)
    {
        if (!Store.IsAlive(entity))
            return false;

        ref var record = ref Store.GetRecord(entity);
        if (record.Archetype is not null)
            return false;

        Store.Free(entity);
        return true;
    }

    internal void DestroyNow(Entity entity)
    {
        _iteration.Throw();
        ref var record = ref Row(entity);
        var archetype = record.Archetype!;
        FreeLive(entity, ref record, archetype, _relations.Any);
    }

    internal void Destroy(Entity entity)
    {
        _iteration.Throw();
        ref var record = ref Row(entity);
        var archetype = record.Archetype!;

        if (archetype.HasCleanupComponents)
        {
            SoftDestroy(entity, ref record, archetype);
            return;
        }

        FreeLive(entity, ref record, archetype, _relations.Any);
    }

    internal bool Alive(Entity entity)
    {
        return Store.IsAlive(entity);
    }

    internal int Count => Store.AliveCount;

    internal bool Pending(Entity entity)
    {
        if (!Store.IsAlive(entity))
            return false;

        ref var record = ref Store.GetRecord(entity);
        if (record.Archetype is null)
            return false;

        var archetype = record.Archetype!;
        return archetype.HasCleanupComponents && archetype.CleanupComponentIds.Length == archetype.ComponentIds.Length;
    }

    internal void FinishCleanup(
        Entity entity,
        ref EntityRecord record,
        Archetype sourceArchetype)
    {
        if (!sourceArchetype.HasCleanupComponents || record.Archetype != _tables.Empty)
            return;

        FreeLive(entity, ref record, _tables.Empty, false);
    }

    internal ref EntityRecord Row(Entity entity)
    {
        ref var record = ref Store.GetRecord(entity);
        if (record.Archetype is null)
            throw new InvalidOperationException($"Entity {entity} is not alive.");

        return ref record;
    }

    internal void ThrowDead(Entity entity)
    {
        _ = Row(entity);
    }

    internal EntitySlotSnapshot[] Slots()
    {
        var slots = new EntitySlotSnapshot[Store.Count];
        for (int index = 1; index <= Store.Count; index++)
        {
            int generation = Store.GetGeneration(index);
            slots[index - 1] = new EntitySlotSnapshot(
                index,
                generation,
                Store.IsAliveIndex(index));
        }

        return slots;
    }

    internal void Prepare(int maxIndex, IReadOnlyList<EntitySlotSnapshot> slots)
    {
        if (Store.AliveCount != 0)
            throw new InvalidOperationException("Serialization load requires an empty World target.");

        Store.ResetForSerialization(maxIndex, slots);
    }

    private void SoftDestroy(
        Entity entity,
        ref EntityRecord record,
        Archetype archetype)
    {
        if (_relations.Any)
            _relations.Cleanup(entity);

        if (archetype.CleanupComponentIds.Length == archetype.ComponentIds.Length)
        {
            FreeLive(entity, ref record, archetype, false);
            return;
        }

        NotifySoft(entity, archetype);
        _components.RemoveLive(entity, archetype, record.Chunk!, record.RowInChunk);
        var plan = _tables.Registry.CleanupTransition(archetype);
        _tables.MoveEntity(entity, ref record, plan);
    }

    private void FreeLive(
        Entity entity,
        ref EntityRecord record,
        Archetype archetype,
        bool hasRelations)
    {
        if (hasRelations)
            _relations.Cleanup(entity);

        var currentChunk = record.Chunk!;
        NotifyDestroy(entity, archetype);
        _components.RemoveAll(entity, archetype, currentChunk, record.RowInChunk);

        var movedEntity = currentChunk.RemoveRow(record.RowInChunk, archetype.ColumnMetas);
        if (movedEntity != Entity.Null)
        {
            ref var movedRecord = ref Store.GetRecord(movedEntity);
            movedRecord.RowInChunk = record.RowInChunk;
        }

        _tables.TryRecycleChunk(archetype, currentChunk);

        record.Archetype = null;
        record.Chunk = null;
        record.RowInChunk = 0;

        Store.Free(entity);
        Write(SerializationChangeKind.EntityDestroyed, entity);
    }

    private void NotifySoft(Entity entity, Archetype archetype)
    {
        for (int columnIndex = 0; columnIndex < archetype.ColumnMetas.Length; columnIndex++)
        {
            int componentId = archetype.ColumnMetas[columnIndex].ComponentId;
            if (Array.BinarySearch(archetype.CleanupComponentIds, componentId) >= 0)
                continue;

            _hierarchy.TrackParent(entity, componentId);
        }
    }

    private void NotifyDestroy(Entity entity, Archetype archetype)
    {
        for (int columnIndex = 0; columnIndex < archetype.ColumnMetas.Length; columnIndex++)
            _hierarchy.TrackParent(entity, archetype.ColumnMetas[columnIndex].ComponentId);
    }

    private void Write(SerializationChangeKind kind, Entity entity)
    {
        _journal.Write(kind, entity, 0, default, _clock.Tick);
    }
}

