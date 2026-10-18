using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public static class SpatialQueries
{
    public static void RebuildSummaries<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        this RelationQuery<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> query)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged => query.Enumerable.RebuildSummaries();
}

public readonly struct SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>
    : ICollisionPartitionEnumerable
    where TGeometry : struct
    where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
    where TFilter : struct, ISpatialFilter<TGroupResults>
    where TGroupResults : unmanaged
{
    public SpatialCandidates(CollisionCandidates<TGeometry, TBoundsReader> source, TFilter filter)
    {
        Source = source;
        Filter = filter;
    }

    public CollisionCandidates<TGeometry, TBoundsReader> Source { get; }
    public TFilter Filter { get; }
    public CollisionTree Tree => Source.Tree;
    public World World => Source.World;
    public int QueryCount => 1;

    public static bool ReadsFromSingleQuery => true;
    public QueryHandle GetQuery(int position) => Source.GetQuery(position);

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        Filter.AddReads(Source.AddReads(definition, position));

    /// <summary>Populates this query's attached summaries before reading a caller-held tree.</summary>
    public void RebuildSummaries()
    {
        QueryDefinition reads = QueryDefinition.Empty.WithAccessFrom(World.GetQueryDefinition(GetQuery(0)));
        QueryHandle query = World.Query(AddReads(reads, 0));
        var execution = new SummaryExecution(Filter);
        try
        {
            World.ExecuteQuery(query, ref execution);
        }
        finally
        {
            World.ReleaseQuery(query);
        }
    }

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        Prepare(queries);
        return SpatialTraversal.Enumerate<TFilter, TGroupResults, TOutput>(
            Tree, queries, Filter, !Source.BuildsFromQuery, ref output);
    }

    public void Prepare(RelationQueryViews queries)
    {
        if (Source.BuildsFromQuery)
        {
            Source.BuildTree(queries);
            Filter.RebuildSummaries(queries.Access);
        }
    }

    public bool Enumerate<TOutput>(CollisionNodePair partition, RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        SpatialTraversal.Enumerate<TFilter, TGroupResults, TOutput>(Tree, queries, Filter, true, partition, ref output);

    private struct SummaryExecution(TFilter filter) : IQueryExecution
    {
        public void Execute(QueryCursor access) => filter.RebuildSummaries(access);
    }
}

internal static class SpatialTraversal
{
    internal static bool Enumerate<TFilter, TGroupResults, TOutput>(CollisionTree tree,
        RelationQueryViews queries, TFilter filter, bool checkMembers, ref TOutput output)
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        => Enumerate<TFilter, TGroupResults, TOutput>(tree, queries, filter, checkMembers,
            new(tree.Root, tree.Root), ref output);

    internal static bool Enumerate<TFilter, TGroupResults, TOutput>(CollisionTree tree,
        RelationQueryViews queries, TFilter filter, bool checkMembers, CollisionNodePair partition, ref TOutput output)
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        if (tree.Root < 0)
            return true;
        ReadOnlySpan<CollisionNode> nodes = tree.Nodes;
        Span<SpatialNodePair<TGroupResults>> nodePairs = stackalloc SpatialNodePair<TGroupResults>[2 * tree.Height + 1];
        int pairCount = 0;
        nodePairs[pairCount++] = new(partition.First, partition.Second, default, default);
        while (pairCount > 0)
        {
            SpatialNodePair<TGroupResults> pair = nodePairs[--pairCount];
            ref readonly CollisionNode first = ref nodes[pair.First];
            ref readonly CollisionNode second = ref nodes[pair.Second];
            if (pair.First == pair.Second && first.IsLeaf ||
                pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                continue;

            GroupMatch forward = filter.MatchGroups(pair.First, pair.Second, in first, in second, in pair.Forward, out var forwardResults);
            GroupMatch reverse = filter.MatchGroups(pair.Second, pair.First, in second, in first, in pair.Reverse, out var reverseResults);
            if (forward == GroupMatch.None && reverse == GroupMatch.None)
                continue;

            if (pair.First == pair.Second)
            {
                nodePairs[pairCount++] = new(first.SecondChild, first.SecondChild, forwardResults, reverseResults);
                nodePairs[pairCount++] = new(first.FirstChild, first.SecondChild, forwardResults, reverseResults);
                nodePairs[pairCount++] = new(first.FirstChild, first.FirstChild, forwardResults, reverseResults);
            }
            else if (first.IsLeaf && second.IsLeaf)
            {
                bool ascending = EntityOrder.Compare(first.Entity, second.Entity) < 0;
                int firstNode = ascending ? pair.First : pair.Second;
                int secondNode = ascending ? pair.Second : pair.First;
                GroupMatch match = ascending ? forward : reverse;
                TGroupResults results = ascending ? forwardResults : reverseResults;
                if (match == GroupMatch.None)
                    continue;
                var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                QueryRow firstRow = default, secondRow = default;
                if (checkMembers || TFilter.ReadsComponentRows)
                    if (!queries.First.TryGetRow(entities.Item1, out firstRow) ||
                        !queries.First.TryGetRow(entities.Item2, out secondRow))
                        continue;
                if (match == GroupMatch.Unknown && !filter.MatchesLeaves(in firstRow, in secondRow, firstNode, secondNode, in results))
                    continue;
                if (!output.Add(in entities))
                    return false;
            }
            else if (second.IsLeaf || !first.IsLeaf && first.Height >= second.Height)
            {
                nodePairs[pairCount++] = new(first.SecondChild, pair.Second, forwardResults, reverseResults);
                nodePairs[pairCount++] = new(first.FirstChild, pair.Second, forwardResults, reverseResults);
            }
            else
            {
                nodePairs[pairCount++] = new(pair.First, second.SecondChild, forwardResults, reverseResults);
                nodePairs[pairCount++] = new(pair.First, second.FirstChild, forwardResults, reverseResults);
            }
        }
        return true;
    }
}

internal readonly struct SpatialNodePair<TGroupResults>(int first, int second,
    TGroupResults forward, TGroupResults reverse) where TGroupResults : unmanaged
{
    internal readonly int First = first;
    internal readonly int Second = second;
    internal readonly TGroupResults Forward = forward;
    internal readonly TGroupResults Reverse = reverse;
}
