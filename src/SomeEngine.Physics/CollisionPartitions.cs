using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A disjoint part of the tree's unordered pair space.</summary>
public readonly record struct CollisionNodePair(int First, int Second);

/// <summary>A candidate source with concrete, disjoint partitions of its entity pair output.</summary>
public interface ICollisionPartitionEnumerable<TPartition> : IQueryRelationEnumerable<(Entity, Entity)>
    where TPartition : struct
{
    void Prepare(scoped RelationQueryViews queries);

    int CreatePartitions(Span<TPartition> partitions);

    bool Enumerate<TOutput>(TPartition partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct;
}

/// <summary>BVH candidate partitions retain their node-pair representation at the tree boundary.</summary>
public interface ICollisionPartitionEnumerable : ICollisionPartitionEnumerable<CollisionNodePair>
{
    CollisionTree Tree { get; }

    int ICollisionPartitionEnumerable<CollisionNodePair>.CreatePartitions(Span<CollisionNodePair> partitions) =>
        Tree.CreatePartitions(partitions);
}

/// <summary>The complete pair range of a general relation query.</summary>
public readonly struct RelationPairPartition;

public static class CollisionPartitionQueries
{
    public static RelationQuery<RelationPairPartitions<TEnumerable>, (Entity, Entity)> PartitionPairs<TEnumerable>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)> => new(new(query.Enumerable));
}

/// <summary>Preserves a general relation's pair semantics as one concrete candidate partition.</summary>
public readonly struct RelationPairPartitions<TEnumerable>(TEnumerable source)
    : ICollisionPartitionEnumerable<RelationPairPartition>
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
{
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TEnumerable.ReadsFromSingleQuery;
    public QueryHandle GetQuery(int position) => source.GetQuery(position);
    public QueryDefinition AddReads(QueryDefinition definition, int position) => source.AddReads(definition, position);
    public void Prepare(scoped RelationQueryViews queries) { }
    public int CreatePartitions(Span<RelationPairPartition> partitions)
    {
        partitions[0] = default;
        return 1;
    }
    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        source.Enumerate(queries, ref output);
    public bool Enumerate<TOutput>(RelationPairPartition partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        source.Enumerate(queries, ref output);
}

public sealed partial class CollisionTree
{
    /// <summary>Splits pair space by size, omitting diagonal leaves and disjoint bounds from the resulting work.</summary>
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
        int overlapCount = 0;
        foreach (CollisionNodePair pair in partitions[..count])
        {
            ref readonly CollisionNode first = ref _nodes[pair.First];
            ref readonly CollisionNode second = ref _nodes[pair.Second];
            if (pair.First == pair.Second && first.IsLeaf ||
                pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                continue;
            partitions[overlapCount++] = pair;
        }
        return overlapCount;
    }
}
