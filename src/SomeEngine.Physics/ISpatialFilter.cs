using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.Physics;

public enum GroupMatch : byte
{
    Unknown,
    None,
    All,
}

/// <summary>Ordered node-pair proofs and callback-scoped leaf reads for the collision tree.</summary>
public interface ISpatialFilter<TGroupResults> where TGroupResults : unmanaged
{
    QueryDefinition AddReads(QueryDefinition definition);

    /// <summary>Writes attached arrays during initial build or an explicit rebuild.</summary>
    void RebuildSummaries(QueryCursor access);

    /// <summary>Writes one leaf or a branch whose children have already been updated.</summary>
    void RefitSummary(int node, QueryCursor access);

    /// <summary>Reports row writes that affect this filter's cached leaf data.</summary>
    bool HasSummaryChange(QueryChunkView chunk, int row) => false;

    GroupMatch MatchGroups(int firstNode, int secondNode,
        in TGroupResults inherited, out TGroupResults results);

    /// <summary>Reads a pair of leaves ordered by their complete Entity identities.</summary>
    bool MatchesLeaves(QueryCursor access, int firstNode, int secondNode, in TGroupResults results);
}

internal static class GroupMatches
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static GroupMatch And(GroupMatch first, GroupMatch second) =>
        first == GroupMatch.None || second == GroupMatch.None ? GroupMatch.None :
        first == GroupMatch.All && second == GroupMatch.All ? GroupMatch.All : GroupMatch.Unknown;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static GroupMatch Except(GroupMatch match) => match switch
    {
        GroupMatch.None => GroupMatch.All,
        GroupMatch.All => GroupMatch.None,
        _ => GroupMatch.Unknown,
    };
}
