using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly struct SpatialComponentFilter<TRelation, TFirst, TSecond>(CollisionTree tree, TRelation relation)
    : ISpatialFilter<GroupMatch>
    where TRelation : struct, IRelation<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
{
    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<TFirst>().WithOptionalRead<TSecond>();

    public void RebuildSummaries(QueryCursor access) { }

    public void RefitSummary(int node, QueryCursor access) { }

    public GroupMatch MatchGroups(int firstNode, int secondNode, in GroupMatch inherited, out GroupMatch results) =>
        results = inherited;

    public bool MatchesLeaves(QueryCursor access, int firstNode, int secondNode, in GroupMatch results)
    {
        if (results != GroupMatch.Unknown)
            return results == GroupMatch.All;
        var pair = (tree.Nodes[firstNode].Entity, tree.Nodes[secondNode].Entity);
        var bound = new ComponentRelation<TRelation, TFirst, TSecond>(access, relation);
        return bound.Matches(in pair);
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

    public SpatialKeyFilter(CollisionTree tree, TRelation relation, TImplementation implementation)
    {
        _relation = relation;
        _implementation = implementation;
        Summary = tree.CreateKeySummary<TComponent, TKey>();
    }

    public CollisionKeySummary<TComponent, TKey> Summary { get; }

    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<TComponent>();

    public void RebuildSummaries(QueryCursor access) => Summary.Rebuild(access);

    public void RefitSummary(int node, QueryCursor access) => Summary.Refit(node, access);

    public bool HasSummaryChange(QueryChunkView chunk, int row) =>
        chunk.Has<TComponent>() && chunk.HasChangedSinceLastSystemVersion<TComponent>() &&
        chunk.RowChangedSinceLastSystemVersion<TComponent>(row);

    public GroupMatch MatchGroups(int firstNode, int secondNode, in GroupMatch inherited, out GroupMatch results)
    {
        if (inherited != GroupMatch.Unknown)
            return results = inherited;
        ref readonly var first = ref Summary.Nodes[firstNode];
        ref readonly var second = ref Summary.Nodes[secondNode];
        if (first.KeyCount == 0 || second.KeyCount == 0)
            return results = GroupMatch.None;
        if (first.HasUniformKey && second.HasUniformKey)
        {
            if (!_implementation.Matches(in _relation, in first.UniformKey, in second.UniformKey))
                return results = GroupMatch.None;
            if (first.KeyCount == Summary.Tree.Nodes[firstNode].MemberCount &&
                second.KeyCount == Summary.Tree.Nodes[secondNode].MemberCount)
                return results = GroupMatch.All;
        }
        return results = GroupMatch.Unknown;
    }

    public bool MatchesLeaves(QueryCursor access, int firstNode, int secondNode, in GroupMatch results)
    {
        if (results != GroupMatch.Unknown)
            return results == GroupMatch.All;
        ref readonly var first = ref Summary.Nodes[firstNode];
        ref readonly var second = ref Summary.Nodes[secondNode];
        return first.KeyCount != 0 && second.KeyCount != 0 &&
            _implementation.Matches(in _relation, in first.UniformKey, in second.UniformKey);
    }
}

public readonly struct SpatialAnd<TFirst, TSecond, TFirstResults, TSecondResults>(TFirst first, TSecond second)
    : ISpatialFilter<(TFirstResults, TSecondResults)>
    where TFirst : struct, ISpatialFilter<TFirstResults>
    where TSecond : struct, ISpatialFilter<TSecondResults>
    where TFirstResults : unmanaged
    where TSecondResults : unmanaged
{
    public TFirst First { get; } = first;
    public TSecond Second { get; } = second;

    public QueryDefinition AddReads(QueryDefinition definition) => Second.AddReads(First.AddReads(definition));

    public void RebuildSummaries(QueryCursor access)
    {
        First.RebuildSummaries(access);
        Second.RebuildSummaries(access);
    }

    public void RefitSummary(int node, QueryCursor access)
    {
        First.RefitSummary(node, access);
        Second.RefitSummary(node, access);
    }

    public bool HasSummaryChange(QueryChunkView chunk, int row) =>
        First.HasSummaryChange(chunk, row) || Second.HasSummaryChange(chunk, row);

    public GroupMatch MatchGroups(int firstNode, int secondNode,
        in (TFirstResults, TSecondResults) inherited, out (TFirstResults, TSecondResults) results)
    {
        GroupMatch firstMatch = First.MatchGroups(firstNode, secondNode, in inherited.Item1, out var firstResults);
        GroupMatch secondMatch = Second.MatchGroups(firstNode, secondNode, in inherited.Item2, out var secondResults);
        results = (firstResults, secondResults);
        return GroupMatches.And(firstMatch, secondMatch);
    }

    public bool MatchesLeaves(QueryCursor access, int firstNode, int secondNode,
        in (TFirstResults, TSecondResults) results) =>
        First.MatchesLeaves(access, firstNode, secondNode, in results.Item1) &&
        Second.MatchesLeaves(access, firstNode, secondNode, in results.Item2);
}

public readonly struct SpatialExcept<TFilter, TGroupResults>(TFilter filter) : ISpatialFilter<TGroupResults>
    where TFilter : struct, ISpatialFilter<TGroupResults>
    where TGroupResults : unmanaged
{
    public TFilter Filter { get; } = filter;

    public QueryDefinition AddReads(QueryDefinition definition) => Filter.AddReads(definition);

    public void RebuildSummaries(QueryCursor access) => Filter.RebuildSummaries(access);

    public void RefitSummary(int node, QueryCursor access) => Filter.RefitSummary(node, access);

    public bool HasSummaryChange(QueryChunkView chunk, int row) => Filter.HasSummaryChange(chunk, row);

    public GroupMatch MatchGroups(int firstNode, int secondNode, in TGroupResults inherited, out TGroupResults results) =>
        GroupMatches.Except(Filter.MatchGroups(firstNode, secondNode, in inherited, out results));

    public bool MatchesLeaves(QueryCursor access, int firstNode, int secondNode, in TGroupResults results) =>
        !Filter.MatchesLeaves(access, firstNode, secondNode, in results);
}
