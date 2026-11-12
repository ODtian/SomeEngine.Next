using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Chunk pairs with the default motion rule written directly.</summary>
public static class HandwrittenDefaultPairs
{
    public static bool Enumerate<TOutput>(World world, Query bodies, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope members = bodies.WithOptionalRead<Body>().OpenRead();
        int firstIndex = 0;
        foreach (QueryChunk first in members)
        {
            if (!first.TryRead<Body>(out ReadOnlySpan<Body> firstBodies)) { firstIndex++; continue; }
            int secondIndex = 0;
            foreach (QueryChunk second in members)
            {
                int index = secondIndex++;
                if (index < firstIndex || !second.TryRead<Body>(out ReadOnlySpan<Body> secondBodies)) continue;
                for (int a = 0; a < first.Count; a++)
                    for (int b = index == firstIndex ? a + 1 : 0; b < second.Count; b++)
                    {
                        if (!first.Has<Body>(a) || !second.Has<Body>(b)) continue;
                        if (firstBodies[a].Kind != MotionKind.Dynamic && secondBodies[b].Kind != MotionKind.Dynamic) continue;
                        var pair = EntityOrder.OrderPair(first.Entities[a], second.Entities[b]);
                        if (!output.Add(in pair)) return false;
                    }
            }
            firstIndex++;
        }
        return true;
    }
}
