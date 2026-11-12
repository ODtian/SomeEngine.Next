using SomeEngine.ECS.Relations;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Spatial;

public interface ISpatialBitmapProof
{
    static abstract GroupMatch Match(in BoundsBitmapData first, in BoundsBitmapData second,
        in BoundsNode firstNode, in BoundsNode secondNode);
}

public readonly struct OneWaySpatialPermission : ISpatialBitmapProof
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GroupMatch Match(in BoundsBitmapData first, in BoundsBitmapData second,
        in BoundsNode firstNode, in BoundsNode secondNode)
    {
        if ((first.AllowedAny & second.CategoriesAny) == 0)
            return GroupMatch.None;
        if (first.ComponentCount == firstNode.MemberCount && second.ComponentCount == secondNode.MemberCount &&
            (first.AllowedCommon & second.CategoriesCommon) != 0)
            return GroupMatch.All;
        return GroupMatch.Unknown;
    }
}

public readonly struct MutualSpatialPermission : ISpatialBitmapProof
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GroupMatch Match(in BoundsBitmapData first, in BoundsBitmapData second,
        in BoundsNode firstNode, in BoundsNode secondNode)
    {
        if ((first.AllowedAny & second.CategoriesAny) == 0 || (second.AllowedAny & first.CategoriesAny) == 0)
            return GroupMatch.None;
        if (first.ComponentCount == firstNode.MemberCount && second.ComponentCount == secondNode.MemberCount &&
            (first.AllowedCommon & second.CategoriesCommon) != 0 && (second.AllowedCommon & first.CategoriesCommon) != 0)
            return GroupMatch.All;
        return GroupMatch.Unknown;
    }
}

public readonly struct UnknownSpatialPermission : ISpatialBitmapProof
{
    public static GroupMatch Match(in BoundsBitmapData first, in BoundsBitmapData second,
        in BoundsNode firstNode, in BoundsNode secondNode) => GroupMatch.Unknown;
}

public readonly struct SpatialBitmapFilter<TRelation, TImplementation, TComponent, TPermission, TGroupPermission>
    : ISpatialFilter<GroupMatch>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
    where TGroupPermission : struct, ISpatialBitmapProof
{
    public SpatialBitmapFilter(BoundsTree tree, TRelation relation, TImplementation implementation) =>
        Summary = tree.CreateBitmapSummary<TRelation, TImplementation, TComponent, TPermission>(relation, implementation);

    public BoundsBitmapSummary<TRelation, TImplementation, TComponent, TPermission> Summary { get; }

    public QueryDescription AddReads(QueryDescription definition) => definition.Optional<TComponent>();

    public void RebuildSummaries(World world) => Summary.Rebuild(world);

    public void RefitSummary(int node, World world) => Summary.Refit(node, world);

    public bool HasChanges<TGeometry>(QueryChunk chunk, int row, ulong since) where TGeometry : struct =>
        typeof(TComponent) != typeof(TGeometry) && chunk.WasWritten<TComponent>(row, since);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GroupMatch MatchGroups(int firstNode, int secondNode,
        in BoundsNode first, in BoundsNode second, in GroupMatch inherited, out GroupMatch results) =>
        results = inherited != GroupMatch.Unknown ? inherited :
            TGroupPermission.Match(in Summary[firstNode], in Summary[secondNode], in first, in second);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesLeaves(QueryChunk firstChunk, int firstRow, QueryChunk secondChunk, int secondRow,
        int firstNode, int secondNode, in GroupMatch results)
    {
        if (results != GroupMatch.Unknown)
            return results == GroupMatch.All;
        ref readonly var first = ref Summary[firstNode];
        ref readonly var second = ref Summary[secondNode];
        if (first.ComponentCount == 0 || second.ComponentCount == 0)
            return false;
        var firstBits = new CategoryBits(first.CategoriesAny, first.AllowedAny);
        var secondBits = new CategoryBits(second.CategoriesAny, second.AllowedAny);
        return TPermission.Matches(in firstBits, in secondBits);
    }
}
