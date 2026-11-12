using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public static partial class HandwrittenSpatialCollision
{
    public static bool Enumerate<TOutput>(World world, Query bodies, BoundsTree tree, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope members = bodies.WithOptionalRead<Body>().WithOptionalRead<Hair>().OpenRead();
        using ReadLookup<Body> values = world.OpenRead<Body>();
        using ReadLookup<Hair> hairs = world.OpenRead<Hair>();
        if (tree.Root < 0)
            return true;

        ReadOnlySpan<BoundsNode> nodes = tree.Nodes;
        Span<(int First, int Second)> work = stackalloc (int, int)[2 * tree.Height + 1];
        int count = 0;
        work[count++] = (tree.Root, tree.Root);
        while (count > 0)
        {
            var pair = work[--count];
            ref readonly BoundsNode first = ref nodes[pair.First];
            ref readonly BoundsNode second = ref nodes[pair.Second];
            if (pair.First == pair.Second)
            {
                if (first.IsLeaf)
                    continue;
                work[count++] = (first.SecondChild, first.SecondChild);
                work[count++] = (first.FirstChild, first.SecondChild);
                work[count++] = (first.FirstChild, first.FirstChild);
                continue;
            }
            if (!first.Bounds.Overlaps(in second.Bounds))
                continue;
            if (first.IsLeaf && second.IsLeaf)
            {
                var entities = EntityOrder.OrderPair(first.Entity, second.Entity);
                if (!members.Contains(entities.Item1) || !members.Contains(entities.Item2))
                    continue;
                ref readonly Body firstBody = ref values.ReadKnown(entities.Item1);
                ref readonly Body secondBody = ref values.ReadKnown(entities.Item2);
                if (firstBody.Kind != MotionKind.Dynamic && secondBody.Kind != MotionKind.Dynamic)
                    continue;
                if (hairs.Has(entities.Item1) && hairs.Has(entities.Item2) &&
                    hairs.ReadKnown(entities.Item1).Character == hairs.ReadKnown(entities.Item2).Character)
                    continue;
                if (!output.Add(in entities))
                    return false;
            }
            else if (second.IsLeaf || !first.IsLeaf && first.Height >= second.Height)
            {
                work[count++] = (first.SecondChild, pair.Second);
                work[count++] = (first.FirstChild, pair.Second);
            }
            else
            {
                work[count++] = (pair.First, second.SecondChild);
                work[count++] = (pair.First, second.FirstChild);
            }
        }
        return true;
    }
}
