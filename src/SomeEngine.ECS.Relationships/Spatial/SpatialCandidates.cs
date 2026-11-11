using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Spatial;

public static partial class SpatialQueries
{
    public static void RebuildSummaries<TGeometry, TBoundsReader, TFilter, TGroupResults>(
        this RelationQuery<SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> query)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry>
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged => query.Enumerable.RebuildSummaries();
}

public readonly struct SpatialCandidates<TGeometry, TBoundsReader, TFilter, TGroupResults>
    : ISpatialPartitionEnumerable<BoundsNodePair>
    where TGeometry : struct
    where TBoundsReader : struct, ISpatialBoundsReader<TGeometry>
    where TFilter : struct, ISpatialFilter<TGroupResults>
    where TGroupResults : unmanaged
{
    private readonly SpatialQueryData _data;

    public SpatialCandidates(TreeCandidates<TGeometry, TBoundsReader> source, TFilter filter) =>
        _data = new(source, filter);

    public TreeCandidates<TGeometry, TBoundsReader> Source => _data.Source;
    public TFilter Filter => _data.Filter;
    public BoundsTree Tree => Source.Tree;
    public World World => Source.World;
    public int QueryCount => 1;

    public static bool ReadsFromSingleQuery => true;
    public Query GetQuery(int position) => Source.GetQuery(position);

    public QueryDescription AddReads(QueryDescription definition, int position) =>
        Filter.AddReads(Source.AddReads(definition, position));

    /// <summary>Populates this query's attached summaries before reading a caller-held tree.</summary>
    public void RebuildSummaries()
    {
        using QueryScope scope = SpatialQueries.Reads(GetQuery(0), AddReads(new QueryDescription(), 0)).OpenRead();
        Filter.RebuildSummaries(World);
    }

    public int CreatePartitions(Span<BoundsNodePair> partitions) => Tree.CreatePartitions(partitions);

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        Prepare(queries);
        return SpatialTraversal.Enumerate<TFilter, TGroupResults, TOutput>(
            Tree, queries, Filter, !Source.BuildsFromQuery, ref output);
    }

    public void Prepare(scoped RelationQueryViews queries)
    {
        if (Source.BuildsFromQuery)
        {
            Source.BuildTree(queries);
            Filter.RebuildSummaries(World);
        }
    }

    public bool Enumerate<TOutput>(BoundsNodePair partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        SpatialTraversal.Enumerate<TFilter, TGroupResults, TOutput>(Tree, queries, Filter, true, partition, ref output);

    private sealed class SpatialQueryData(TreeCandidates<TGeometry, TBoundsReader> source, TFilter filter)
    {
        internal TreeCandidates<TGeometry, TBoundsReader> Source { get; } = source;
        internal TFilter Filter { get; } = filter;
    }
}

internal static class SpatialTraversal
{
    internal static bool Enumerate<TFilter, TGroupResults, TOutput>(BoundsTree tree,
        scoped RelationQueryViews queries, TFilter filter, bool checkMembers, ref TOutput output)
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        => Enumerate<TFilter, TGroupResults, TOutput>(tree, queries, filter, checkMembers,
            new(tree.Root, tree.Root), ref output);

    internal static bool Enumerate<TFilter, TGroupResults, TOutput>(BoundsTree tree,
        scoped RelationQueryViews queries, TFilter filter, bool checkMembers, BoundsNodePair partition, ref TOutput output)
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        if (tree.Root < 0)
            return true;
        ReadOnlySpan<BoundsNode> nodes = tree.Nodes;
        Span<SpatialNodePair<TGroupResults>> nodePairs = stackalloc SpatialNodePair<TGroupResults>[2 * tree.Height + 1];
        int pairCount = 0;
        nodePairs[pairCount++] = new(partition.First, partition.Second, default, default);
        while (pairCount > 0)
        {
            SpatialNodePair<TGroupResults> pair = nodePairs[--pairCount];
            ref readonly BoundsNode first = ref nodes[pair.First];
            ref readonly BoundsNode second = ref nodes[pair.Second];
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
                bool ascending = EntityOrder.Precedes(first.Entity, second.Entity);
                int firstNode = ascending ? pair.First : pair.Second;
                int secondNode = ascending ? pair.Second : pair.First;
                GroupMatch match = ascending ? forward : reverse;
                TGroupResults results = ascending ? forwardResults : reverseResults;
                if (match == GroupMatch.None)
                    continue;
                var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                if (checkMembers && (!queries.First.Contains(entities.Item1) || !queries.First.Contains(entities.Item2)))
                    continue;
                if (match == GroupMatch.Unknown)
                {
                    ref var firstRecord = ref queries.World.Records[entities.Item1.Id];
                    ref var secondRecord = ref queries.World.Records[entities.Item2.Id];
                    var firstChunk = new QueryChunk(firstRecord.Chunk!, 0, firstRecord.Chunk!.Count, write: false);
                    var secondChunk = new QueryChunk(secondRecord.Chunk!, 0, secondRecord.Chunk!.Count, write: false);
                    if (!filter.MatchesLeaves(firstChunk, firstRecord.Row, secondChunk, secondRecord.Row,
                        firstNode, secondNode, in results)) continue;
                }
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
