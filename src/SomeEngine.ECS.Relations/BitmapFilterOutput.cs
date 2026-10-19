using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

internal ref struct BitmapFilterOutput<TRelation, TImplementation, TComponent, TPermission, TOutput, TSelection>
    : IQueryRelationOutput<(Entity, Entity)>, ICategoryPermissionOutput<TOutput>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
    where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    where TSelection : struct, IRelationSelection
{
    private BitmapRelation<TRelation, TImplementation, TComponent, TPermission> _relation;
    private (Entity, Entity) _pair;
    public TOutput Output;

    public BitmapFilterOutput(BitmapRelation<TRelation, TImplementation, TComponent, TPermission> relation, TOutput output)
    {
        _relation = relation;
        _pair = default;
        Output = output;
    }

    public static bool ReadsComponentRows => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in (Entity, Entity) pair)
    {
        if (TOutput.ReadsComponentRows)
        {
            if (_relation.Matches(in pair) != TSelection.RequiresMatch)
                return true;
            return Output.Add(in pair);
        }
        _pair = pair;
        return _relation.Evaluate(in pair, ref Output, ref this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in (Entity, Entity) pair, scoped in QueryRow first, scoped in QueryRow second)
    {
        if (TOutput.ReadsComponentRows)
        {
            if (_relation.Matches(in pair, in first, in second) != TSelection.RequiresMatch)
                return true;
            return Output.Add(in pair, in first, in second);
        }
        _pair = pair;
        return _relation.Evaluate(in first, in second, ref Output, ref this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matched(scoped ref TOutput output) => !TSelection.RequiresMatch || output.Add(in _pair);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Unmatched(scoped ref TOutput output) => TSelection.RequiresMatch || output.Add(in _pair);
}
