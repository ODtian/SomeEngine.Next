using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private int AllocateCreatedRows(Span<Entity> entities, Chunk chunk)
    {
        int start = chunk.Count, count = entities.Length;
        if (count > chunk.Capacity) chunk.ReserveRows(start + count);
        else chunk.Reserve(start + count);
        int reuse = Math.Min(Used - _count, count);
        ReserveEntities(Used + count - reuse);
        EntityRecord[] records = Records;
        Entity[] rows = chunk.Entities;
        int free = _free, i = 0;
        for (; i < reuse; i++)
        {
            int id = free;
            ref EntityRecord record = ref records[id];
            free = record.Row;
            Entity entity = new(id, record.Generation);
            record = new(chunk, start + i, entity.Generation);
            rows[start + i] = entities[i] = entity;
        }
        int used = Used;
        for (; i < count; i++)
        {
            Entity entity = new(++used, 1);
            records[used] = new(chunk, start + i, 1);
            rows[start + i] = entities[i] = entity;
        }
        _free = free;
        Used = used;
        return start;
    }

    private void CompleteCreatedRows(Chunk chunk, int start, int count, bool publishMembership = true)
    {
        _count += count;
        chunk.Count += count;
        StampCreated(chunk, start, count);
        if ((chunk.Archetype.TransferKinds & 4) != 0)
            for (int row = start; row < start + count; row++) chunk.InitializePresence(row);
        if (chunk.Archetype.Cached is { } cached)
            foreach (CachedComponent data in cached) data.BindRange(chunk, start, count);
        if (publishMembership) PublishCreatedMembership(chunk.Entities.AsSpan(start, count));
    }


}
