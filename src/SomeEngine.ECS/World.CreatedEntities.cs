using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    internal (int FreeHead, int Used) PreviewEntityIds(Span<Entity> entities)
    {
        int free = _free, used = Used;
        for (int i = 0; i < entities.Length; i++)
        {
            if (free != 0)
            {
                ref EntityRecord record = ref Records[free];
                entities[i] = new(free, record.Generation);
                free = record.Row;
            }
            else entities[i] = new(++used, 1);
        }
        return (free, used);
    }

    internal void CommitCreatedEntities(Archetype archetype, ReadOnlySpan<Entity> entities,
        int freeHead, int used, ulong version)
    {
        int offset = 0;
        while (offset < entities.Length)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int start = chunk.Count;
            int count = Math.Min(entities.Length - offset, chunk.Capacity - start);
            for (int row = 0; row < count; row++)
            {
                Entity entity = entities[offset + row];
                Records[entity.Id] = new(chunk, start + row, entity.Generation);
                chunk.Entities[start + row] = entity;
                if ((archetype.TransferKinds & 4) != 0) chunk.InitializePresence(start + row);
            }
            chunk.Count += count;
            StampCreated(chunk, start, count, version);
            if (archetype.Cached is { } cached)
                foreach (CachedComponent values in cached) values.BindRange(chunk, start, count);
            offset += count;
        }
        _free = freeHead;
        Used = used;
        _count += entities.Length;
        PublishCreatedMembership(entities);
    }
}
