using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS;

public partial class World
{
    internal void ResetStores()
    {
        _sparse.Reset();
        _indices.Reset();
        _relations.Reset();
        _shared.Reset();
        _hierarchy.Reset();
        _bundles.Reset();
        _queries.Reset();
    }

    internal Entity[] LiveEntities()
    {
        return _tables.LiveEntities(EntityCount);
    }

    internal EntitySlotSnapshot[] EntitySlots()
    {
        return _entities.Slots();
    }

    internal void PrepareStore(int maxIndex, IReadOnlyList<EntitySlotSnapshot> slots)
    {
        ResetStores();
        _entities.Prepare(maxIndex, slots);
    }

    internal Entity LoadEntity(int index, int generation)
    {
        var entity = new Entity(index, generation);
        ref var record = ref _entities.Store.AllocatePreserved(entity);
        var (chunk, row) = _tables.AllocateInChunk(_tables.Empty, entity);
        record.Archetype = _tables.Empty;
        record.Chunk = chunk;
        record.RowInChunk = row;
        return entity;
    }
}

