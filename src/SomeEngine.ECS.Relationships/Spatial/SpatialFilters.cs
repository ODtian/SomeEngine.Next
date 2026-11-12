using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Spatial;

public readonly struct SpatialComponentFilter<TRelation, TFirst, TSecond>(TRelation relation) : ISpatialFilter<GroupMatch>
    where TRelation : struct, IRelation<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
{
    public static bool ReadsComponentRows => true;
    public QueryDescription AddReads(QueryDescription definition) => definition.Optional<TFirst>().Optional<TSecond>();
    public void RebuildSummaries(World world) { }
    public void RefitSummary(int node, World world) { }
    public bool HasChanges<TGeometry>(QueryChunk chunk, int row, ulong since) where TGeometry : struct => false;
    public GroupMatch MatchGroups(int firstNode, int secondNode, in BoundsNode first, in BoundsNode second,
        in GroupMatch inherited, out GroupMatch results) => results = GroupMatch.Unknown;
    public bool MatchesLeaves(QueryChunk first, int firstRow, QueryChunk second, int secondRow,
        int firstNode, int secondNode, in GroupMatch results)
    {
        if (results != GroupMatch.Unknown) return results == GroupMatch.All;
        return first.TryRead<TFirst>(out ReadOnlySpan<TFirst> firstValues)
            && second.TryRead<TSecond>(out ReadOnlySpan<TSecond> secondValues)
            && first.Has<TFirst>(firstRow) && second.Has<TSecond>(secondRow)
            && relation.Matches(in firstValues[firstRow], in secondValues[secondRow]);
    }
}

public readonly struct SpatialKeyFilter<TRelation, TImplementation, TComponent, TKey> : ISpatialFilter<GroupMatch>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly TRelation _relation;
    private readonly TImplementation _implementation;
    public SpatialKeyFilter(BoundsTree tree, TRelation relation, TImplementation implementation)
    {
        _relation = relation;
        _implementation = implementation;
        Summary = tree.CreateKeySummary<TComponent, TKey>();
    }
    public BoundsKeySummary<TComponent, TKey> Summary { get; }
    public QueryDescription AddReads(QueryDescription definition) => definition.Optional<TComponent>();
    public void RebuildSummaries(World world) => Summary.Rebuild(world);
    public void RefitSummary(int node, World world) => Summary.Refit(node, world);
    public bool HasChanges<TGeometry>(QueryChunk chunk, int row, ulong since) where TGeometry : struct =>
        typeof(TComponent) != typeof(TGeometry) && chunk.WasWritten<TComponent>(row, since);
    public GroupMatch MatchGroups(int firstNode, int secondNode,
        in BoundsNode firstTreeNode, in BoundsNode secondTreeNode, in GroupMatch inherited, out GroupMatch results)
    {
        if (inherited != GroupMatch.Unknown) return results = inherited;
        ref readonly var first = ref Summary[firstNode];
        ref readonly var second = ref Summary[secondNode];
        if (first.KeyCount == 0 || second.KeyCount == 0) return results = GroupMatch.None;
        if (first.HasUniformKey && second.HasUniformKey)
        {
            if (!_implementation.Matches(in _relation, in first.UniformKey, in second.UniformKey))
                return results = GroupMatch.None;
            if (first.KeyCount == firstTreeNode.MemberCount && second.KeyCount == secondTreeNode.MemberCount)
                return results = GroupMatch.All;
        }
        return results = GroupMatch.Unknown;
    }
    public bool MatchesLeaves(QueryChunk firstChunk, int firstRow, QueryChunk secondChunk, int secondRow,
        int firstNode, int secondNode, in GroupMatch results)
    {
        if (results != GroupMatch.Unknown) return results == GroupMatch.All;
        ref readonly var first = ref Summary[firstNode];
        ref readonly var second = ref Summary[secondNode];
        return first.KeyCount != 0 && second.KeyCount != 0
            && _implementation.Matches(in _relation, in first.UniformKey, in second.UniformKey);
    }
}

public readonly struct SpatialAnd<TFirst, TSecond, TFirstResults, TSecondResults>(TFirst first, TSecond second)
    : ISpatialFilter<(TFirstResults, TSecondResults)>
    where TFirst : struct, ISpatialFilter<TFirstResults>
    where TSecond : struct, ISpatialFilter<TSecondResults>
    where TFirstResults : unmanaged
    where TSecondResults : unmanaged
{
    public static bool ReadsComponentRows => TFirst.ReadsComponentRows || TSecond.ReadsComponentRows;
    public TFirst First { get; } = first;
    public TSecond Second { get; } = second;
    public QueryDescription AddReads(QueryDescription definition) => Second.AddReads(First.AddReads(definition));
    public void RebuildSummaries(World world) { First.RebuildSummaries(world); Second.RebuildSummaries(world); }
    public void RefitSummary(int node, World world) { First.RefitSummary(node, world); Second.RefitSummary(node, world); }
    public bool HasChanges<TGeometry>(QueryChunk chunk, int row, ulong since) where TGeometry : struct =>
        First.HasChanges<TGeometry>(chunk, row, since) || Second.HasChanges<TGeometry>(chunk, row, since);
    public GroupMatch MatchGroups(int firstNode, int secondNode,
        in BoundsNode firstNodeData, in BoundsNode secondNodeData,
        in (TFirstResults, TSecondResults) inherited, out (TFirstResults, TSecondResults) results)
    {
        GroupMatch firstMatch = First.MatchGroups(firstNode, secondNode, in firstNodeData, in secondNodeData,
            in inherited.Item1, out var firstResults);
        GroupMatch secondMatch = Second.MatchGroups(firstNode, secondNode, in firstNodeData, in secondNodeData,
            in inherited.Item2, out var secondResults);
        results = (firstResults, secondResults);
        return GroupMatches.And(firstMatch, secondMatch);
    }
    public bool MatchesLeaves(QueryChunk firstChunk, int firstRow, QueryChunk secondChunk, int secondRow,
        int firstNode, int secondNode, in (TFirstResults, TSecondResults) results) =>
        First.MatchesLeaves(firstChunk, firstRow, secondChunk, secondRow, firstNode, secondNode, in results.Item1)
        && Second.MatchesLeaves(firstChunk, firstRow, secondChunk, secondRow, firstNode, secondNode, in results.Item2);
}

public readonly struct SpatialExcept<TFilter, TGroupResults>(TFilter filter) : ISpatialFilter<TGroupResults>
    where TFilter : struct, ISpatialFilter<TGroupResults>
    where TGroupResults : unmanaged
{
    public static bool ReadsComponentRows => TFilter.ReadsComponentRows;
    public TFilter Filter { get; } = filter;
    public QueryDescription AddReads(QueryDescription definition) => Filter.AddReads(definition);
    public void RebuildSummaries(World world) => Filter.RebuildSummaries(world);
    public void RefitSummary(int node, World world) => Filter.RefitSummary(node, world);
    public bool HasChanges<TGeometry>(QueryChunk chunk, int row, ulong since) where TGeometry : struct =>
        Filter.HasChanges<TGeometry>(chunk, row, since);
    public GroupMatch MatchGroups(int firstNode, int secondNode, in BoundsNode first, in BoundsNode second,
        in TGroupResults inherited, out TGroupResults results) =>
        GroupMatches.Except(Filter.MatchGroups(firstNode, secondNode, in first, in second, in inherited, out results));
    public bool MatchesLeaves(QueryChunk first, int firstRow, QueryChunk second, int secondRow,
        int firstNode, int secondNode, in TGroupResults results) =>
        !Filter.MatchesLeaves(first, firstRow, second, secondRow, firstNode, secondNode, in results);
}
