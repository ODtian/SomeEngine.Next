using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    internal void ResetStores()
    {
        _sparse.Reset();
        _indices.Reset();
        _relationGraph.Reset();
        _shared.Reset();
        _hierarchy.Reset();
        _bundles.Reset();
    }

    internal void BeginSerializationStore(int slotCount)
    {
        // Restore is a construction path, never an in-place replacement path. Check the
        // published image before ResetStores detaches the fresh checkpoint candidate so an
        // internal caller cannot silently revive destructive in-place restore semantics.
        WorldStructureRoot published = PublishedStructureRoot;
        if (PublishedStructureEpoch != 0 ||
            published.Entities.Store.Count != 0 ||
            published.Entities.Store.AliveCount != 0 ||
            EntityCount != 0)
        {
            throw new InvalidOperationException(
                "Serialization restore requires a new, empty World and cannot replace an existing World image.");
        }

        ResetStores();
        _entities.Store.BeginSerializationRestore(slotCount);
    }

    internal void AppendSerializationSlot(int index, int generation, bool isAlive)
    {
        _entities.Store.AppendSerializationSlot(index, generation, isAlive);
        if (isAlive)
            LoadEntity(index, generation);
    }

    internal void CompleteSerializationStore() =>
        _entities.Store.CompleteSerializationRestore();

    internal bool SerializationSlotMatches(Entity entity) =>
        entity.Index > 0 &&
        entity.Index <= _entities.Store.Count &&
        _entities.Store.IsAliveIndex(entity.Index) &&
        _entities.Store.GetGeneration(entity.Index) == entity.Generation;

    internal Entity LoadEntity(int index, int generation)
    {
        var entity = new Entity(index, generation);
        EntityRecordWriter record = _entities.Store.AllocatePreserved(entity);
        var (chunk, row) = _tables.AllocateInChunk(_tables.Empty, entity);
        record.Archetype = _tables.Empty;
        record.Chunk = chunk;
        record.RowInChunk = row;
        return entity;
    }

}

