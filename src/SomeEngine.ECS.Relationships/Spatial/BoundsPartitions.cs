using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Spatial;

/// <summary>A disjoint part of the tree's unordered pair space.</summary>
public readonly record struct BoundsNodePair(int First, int Second);

/// <summary>An entity-pair source with typed partitions and output.</summary>
public interface ISpatialPartitionEnumerable<TPartition> : IQueryRelationEnumerable<(Entity, Entity)>
    where TPartition : struct
{
    void Prepare(scoped RelationQueryViews queries);
    int CreatePartitions(Span<TPartition> partitions);
    bool Enumerate<TOutput>(TPartition partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct;
}

public sealed partial class BoundsTree
{
    /// <summary>Splits pair space by size, omitting diagonal leaves and disjoint bounds from the resulting work.</summary>
    public int CreatePartitions(Span<BoundsNodePair> partitions)
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
                BoundsNodePair pair = partitions[index];
                ref readonly BoundsNode first = ref _nodes[pair.First];
                ref readonly BoundsNode second = ref _nodes[pair.Second];
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
            BoundsNodePair next = partitions[selected];
            BoundsNode left = _nodes[next.First];
            BoundsNode right = _nodes[next.Second];
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
        int overlapCount = 0;
        foreach (BoundsNodePair pair in partitions[..count])
        {
            ref readonly BoundsNode first = ref _nodes[pair.First];
            ref readonly BoundsNode second = ref _nodes[pair.Second];
            if (pair.First == pair.Second && first.IsLeaf ||
                pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                continue;
            partitions[overlapCount++] = pair;
        }
        return overlapCount;
    }
}
