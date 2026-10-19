using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using System.Runtime.CompilerServices;

namespace SomeEngine.Physics;

public interface ISpatialBitmapProof
{
    static abstract GroupMatch Match(in CollisionBitmapData first, in CollisionBitmapData second,
        in CollisionNode firstNode, in CollisionNode secondNode);
}

public readonly struct OneWaySpatialPermission : ISpatialBitmapProof
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static GroupMatch Match(in CollisionBitmapData first, in CollisionBitmapData second,
        in CollisionNode firstNode, in CollisionNode secondNode)
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
    public static GroupMatch Match(in CollisionBitmapData first, in CollisionBitmapData second,
        in CollisionNode firstNode, in CollisionNode secondNode)
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
    public static GroupMatch Match(in CollisionBitmapData first, in CollisionBitmapData second,
        in CollisionNode firstNode, in CollisionNode secondNode) => GroupMatch.Unknown;
}

public readonly struct SpatialBitmapFilter<TRelation, TImplementation, TComponent, TPermission, TGroupPermission>
    : ISpatialFilter<GroupMatch>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
    where TGroupPermission : struct, ISpatialBitmapProof
{
    public SpatialBitmapFilter(CollisionTree tree, TRelation relation, TImplementation implementation) =>
        Summary = tree.CreateBitmapSummary<TRelation, TImplementation, TComponent, TPermission>(relation, implementation);

    public CollisionBitmapSummary<TRelation, TImplementation, TComponent, TPermission> Summary { get; }

    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<TComponent>();

    public void RebuildSummaries(QueryCursor access) => Summary.Rebuild(access);

    public void RefitSummary(int node, QueryCursor access) => Summary.Refit(node, access);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadChanges<TGeometry, TExecution>(QueryChunkView chunk, TExecution execution)
        where TGeometry : struct
        where TExecution : struct, ISpatialChangeExecution, allows ref struct
    {
        if (typeof(TComponent) == typeof(TGeometry))
            execution.Execute(default(UnchangedQueryRows));
        else
            execution.Execute(chunk.ReadRowChanges<TComponent>());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GroupMatch MatchGroups(int firstNode, int secondNode,
        in CollisionNode first, in CollisionNode second, in GroupMatch inherited, out GroupMatch results) =>
        results = inherited != GroupMatch.Unknown ? inherited :
            TGroupPermission.Match(in Summary[firstNode], in Summary[secondNode], in first, in second);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MatchesLeaves(scoped in QueryRow firstRow, scoped in QueryRow secondRow,
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
