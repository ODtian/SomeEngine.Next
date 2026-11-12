using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Spatial;

public enum GroupMatch : byte
{
    Unknown,
    None,
    All,
}

/// <summary>Ordered node-pair proofs and chunk column reads for the bounds tree.</summary>
public interface ISpatialFilter<TGroupResults> where TGroupResults : unmanaged
{
    static virtual bool ReadsComponentRows => false;

    QueryDescription AddReads(QueryDescription definition);

    /// <summary>Writes attached arrays during initial build or an explicit rebuild.</summary>
    void RebuildSummaries(World world);

    /// <summary>Writes one leaf or a branch whose children have already been updated.</summary>
    void RefitSummary(int node, World world);

    bool HasChanges<TGeometry>(QueryChunk chunk, int row, ulong since) where TGeometry : struct;

    GroupMatch MatchGroups(int firstNode, int secondNode,
        in BoundsNode first, in BoundsNode second,
        in TGroupResults inherited, out TGroupResults results);

    /// <summary>Reads a pair of leaves ordered by their complete Entity identities.</summary>
    bool MatchesLeaves(QueryChunk first, int firstRow, QueryChunk second, int secondRow,
        int firstNode, int secondNode, in TGroupResults results);
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
