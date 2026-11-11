using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private static TypeSet DestroyColumnTypes(Archetype source)
    {
        if (source.DestroyColumns is { } found) return found;
        var types = new List<int> { ComponentType<Destroyed>.TypeId };
        foreach (ComponentType type in source.Components)
            if (type.KeepsRemoved && !type.IsCleanup) types.Add(type.RemovedType.Id);
        return source.DestroyColumns = new TypeSet(false, types.ToArray());
    }

    private void PrepareDestroyColumns(ReadOnlySpan<Entity> entities)
    {
        Dictionary<Archetype, int>? targets = null;
        foreach (Entity entity in entities)
        {
            ref EntityRecord record = ref Resolve(entity);
            Archetype source = record.Chunk!.Archetype;
            if (!RetainsValues(entity, in record)) continue;
            Archetype target = ColumnsTarget(entity, DestroyColumnTypes(source));
            if (target == source) continue;
            targets ??= new();
            targets[target] = targets.GetValueOrDefault(target) + 1;
        }
        if (targets is null) return;
        foreach (var pair in targets)
        {
            int count = 0;
            foreach (Chunk chunk in pair.Key.AllChunks) count += chunk.Count;
            pair.Key.Reserve(count + pair.Value);
        }
        foreach (Entity entity in entities)
        {
            ref EntityRecord record = ref Resolve(entity);
            if (RetainsValues(entity, in record))
                EnsureColumns(entity, DestroyColumnTypes(record.Chunk!.Archetype));
        }
    }
}
