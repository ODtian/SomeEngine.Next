using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public ref partial struct QueryScope
{
    public bool Contains(Entity entity)
    {
        if (!_world.IsAliveCore(entity)) return false;
        ref EntityRecord record = ref _world.Records[entity.Id];
        int archetype = record.Chunk!.Archetype.Id;
        int first = 0, last = _count - 1;
        while (first <= last)
        {
            int middle = first + ((last - first) >> 1);
            var match = _matches[middle];
            if (match.Archetype.Id < archetype) first = middle + 1;
            else if (match.Archetype.Id > archetype) last = middle - 1;
            else return match.Filter is null || match.Filter.Contains(record.Chunk, record.Row, _since);
        }
        return false;
    }
}
