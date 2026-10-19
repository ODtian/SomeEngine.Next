using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>A disjoint part of the tree's unordered pair space.</summary>
public readonly record struct CollisionNodePair(int First, int Second);

/// <summary>A single-input collision source with typed output over disjoint node pairs.</summary>
public interface ICollisionPartitionEnumerable : IQueryRelationEnumerable<(Entity, Entity)>
{
    CollisionTree Tree { get; }

    void Prepare(scoped RelationQueryViews queries);

    bool Enumerate<TOutput>(CollisionNodePair partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct;
}

public sealed partial class CollisionTree
{
    /// <summary>Splits node pairs into disjoint work, choosing the largest remaining pair space.</summary>
    public int CreatePartitions(Span<CollisionNodePair> partitions)
    {
        if (Root < 0)
            return 0;
        int count = 1;
        partitions[0] = new(Root, Root);
        while (count < partitions.Length)
        {
            int selected = -1;
            long largest = -1;
            for (int index = 0; index < count; index++)
            {
                CollisionNodePair pair = partitions[index];
                ref readonly CollisionNode first = ref _nodes[pair.First];
                ref readonly CollisionNode second = ref _nodes[pair.Second];
                bool diagonal = pair.First == pair.Second;
                if (first.IsLeaf && second.IsLeaf || diagonal && count + 2 > partitions.Length)
                    continue;
                long pairs = diagonal ? (long)first.MemberCount * (first.MemberCount - 1) / 2 :
                    (long)first.MemberCount * second.MemberCount;
                if (pairs > largest)
                {
                    largest = pairs;
                    selected = index;
                }
            }
            if (selected < 0)
                break;
            CollisionNodePair next = partitions[selected];
            CollisionNode left = _nodes[next.First];
            CollisionNode right = _nodes[next.Second];
            if (next.First == next.Second)
            {
                partitions[selected] = new(left.FirstChild, left.FirstChild);
                partitions[count++] = new(left.FirstChild, left.SecondChild);
                partitions[count++] = new(left.SecondChild, left.SecondChild);
            }
            else if (right.IsLeaf || !left.IsLeaf && left.Height >= right.Height)
            {
                partitions[selected] = new(left.FirstChild, next.Second);
                partitions[count++] = new(left.SecondChild, next.Second);
            }
            else
            {
                partitions[selected] = new(next.First, right.FirstChild);
                partitions[count++] = new(next.First, right.SecondChild);
            }
        }
        return count;
    }
}
