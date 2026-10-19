using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
    public TOutput Output;

    public static bool ReadsComponentRows => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in (Entity, Entity) pair)
    {
        if (TOutput.ReadsComponentRows)
        {
            if (Relation.Matches(in pair) != TSelection.RequiresMatch)
                return true;
            return Output.Add(in pair);
        }
        return Relation.Evaluate<TOutput, IdentityPermissionOutput<TOutput, TSelection>>(in pair, ref Output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in (Entity, Entity) pair, scoped in QueryRow first, scoped in QueryRow second)
    {
        if (TOutput.ReadsComponentRows)
        {
            if (Relation.Matches(in pair, in first, in second) != TSelection.RequiresMatch)
                return true;
            return Output.Add(in pair, in first, in second);
        }
        if (!BitmapRelation<TRelation, TImplementation, TComponent, TPermission>.TryReadComponents(
            in first, in second, out var firstValue, out var secondValue))
            return IdentityPermissionOutput<TOutput, TSelection>.Unmatched(in pair, ref Output);
        return Relation.Evaluate<(Entity, Entity), TOutput, IdentityPermissionOutput<TOutput, TSelection>>(
            in pair, in firstValue.Value, in secondValue.Value, ref Output);
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
