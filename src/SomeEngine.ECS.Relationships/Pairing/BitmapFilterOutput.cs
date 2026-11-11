using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

internal ref struct BitmapFilterOutput<TRelation, TImplementation, TComponent, TPermission, TOutput, TSelection>
    : IQueryRelationOutput<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
    where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    where TSelection : struct, IRelationSelection
{
    internal BitmapRelation<TRelation, TImplementation, TComponent, TPermission> Relation;
    internal RelationOutputReference<TOutput> Output;

    public static bool UsesBoundColumns => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in (Entity, Entity) pair)
    {
        if (TOutput.UsesBoundColumns)
        {
            if (Relation.Matches(in pair) != TSelection.RequiresMatch)
                return true;
            return Output.Value.Add(in pair);
        }
        return Relation.Evaluate<TOutput, IdentityPermissionOutput<TOutput, TSelection>>(in pair, ref Output.Value);
    }

    public void Bind(QueryChunk first, QueryChunk second)
    {
        Relation.Bind(first, second);
        Output.Value.Bind(first, second);
    }

    public bool Add(in (Entity, Entity) pair, int first, int second, bool reversed)
    {
        if (TOutput.UsesBoundColumns)
            return Relation.Matches(in pair, first, second, reversed) != TSelection.RequiresMatch || Output.Value.Add(in pair, first, second, reversed);
        return Relation.EvaluateBound<TOutput, IdentityPermissionOutput<TOutput, TSelection>>(in pair, first, second, reversed, ref Output.Value);
    }

}

internal readonly struct IdentityPermissionOutput<TOutput, TSelection> : ICategoryPermissionOutput<(Entity, Entity), TOutput>
    where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    where TSelection : struct, IRelationSelection
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Matched(scoped in (Entity, Entity) pair, scoped ref TOutput output) =>
        !TSelection.RequiresMatch || output.Add(in pair);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Unmatched(scoped in (Entity, Entity) pair, scoped ref TOutput output) =>
        TSelection.RequiresMatch || output.Add(in pair);
}
