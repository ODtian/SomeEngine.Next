using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Bitmap;

/// <summary>Filters the same candidate list with direct component-field mask operations.</summary>
public static class HandwrittenBitmapCollision
{
    public static bool OneWay<TOutput>(World world, Query members,
        ReadOnlySpan<(Entity, Entity)> candidates, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope query = members.OpenRead();
        using ReadLookup<CollisionLayer> layers = world.OpenRead<CollisionLayer>();
        foreach (var pair in candidates)
        {
            if (!query.Contains(pair.Item1) || !query.Contains(pair.Item2) ||
                !layers.Has(pair.Item1) || !layers.Has(pair.Item2)) continue;
            ref readonly CollisionLayer first = ref layers.ReadKnown(pair.Item1);
            ref readonly CollisionLayer second = ref layers.ReadKnown(pair.Item2);
            if ((first.AllowedCategories & second.Categories) == 0) continue;
            if (!output.Add(in pair)) return false;
        }
        return true;
    }

    public static bool Mutual<TOutput>(World world, Query members,
        ReadOnlySpan<(Entity, Entity)> candidates, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope query = members.OpenRead();
        using ReadLookup<CollisionLayer> layers = world.OpenRead<CollisionLayer>();
        foreach (var pair in candidates)
        {
            if (!query.Contains(pair.Item1) || !query.Contains(pair.Item2) ||
                !layers.Has(pair.Item1) || !layers.Has(pair.Item2)) continue;
            ref readonly CollisionLayer first = ref layers.ReadKnown(pair.Item1);
            ref readonly CollisionLayer second = ref layers.ReadKnown(pair.Item2);
            if ((first.AllowedCategories & second.Categories) == 0 ||
                (second.AllowedCategories & first.Categories) == 0) continue;
            if (!output.Add(in pair)) return false;
        }
        return true;
    }
}
