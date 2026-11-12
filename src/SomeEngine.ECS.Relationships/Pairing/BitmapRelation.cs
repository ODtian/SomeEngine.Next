using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public ref struct BitmapRelation<TRelation, TImplementation, TComponent, TPermission> : IQueryRelation<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{
    private ReadLookup<TComponent> _values;
    private ReadOnlySpan<TComponent> _firstValues, _secondValues;
    private QueryChunk _firstChunk, _secondChunk;
    private TRelation _relation;
    private TImplementation _implementation;

    public BitmapRelation(World world, TRelation relation, TImplementation implementation)
    {
        this = default;
        _values = world.OpenRead<TComponent>();
        _relation = relation; _implementation = implementation;
    }
    public void Dispose() => _values.Dispose();
    public void Bind(QueryChunk first, QueryChunk second)
    {
        _firstChunk = first; _secondChunk = second;
        first.TryRead<TComponent>(out _firstValues); second.TryRead<TComponent>(out _secondValues);
    }
    public bool Matches(in (Entity, Entity) pair)
    {
        if (!_values.Has(pair.Item1) || !_values.Has(pair.Item2)) return false;
        return TPermission.Matches<TRelation, TComponent, TImplementation>(in _relation, ref _implementation,
            in _values.ReadKnown(pair.Item1), in _values.ReadKnown(pair.Item2));
    }
    public bool Matches(in (Entity, Entity) pair, int first, int second, bool reversed)
    {
        if (!HasRows(first, second)) return false;
        return reversed ? TPermission.Matches<TRelation, TComponent, TImplementation>(in _relation, ref _implementation,
            in _secondValues[second], in _firstValues[first]) :
            TPermission.Matches<TRelation, TComponent, TImplementation>(in _relation, ref _implementation,
                in _firstValues[first], in _secondValues[second]);
    }
    private bool HasRows(int first, int second) => !_firstValues.IsEmpty && !_secondValues.IsEmpty &&
        _firstChunk.Has<TComponent>(first) && _secondChunk.Has<TComponent>(second);

    internal bool Evaluate<TOutput, TResult>(in (Entity, Entity) pair, scoped ref TOutput output)
        where TOutput : allows ref struct
        where TResult : struct, ICategoryPermissionOutput<(Entity, Entity), TOutput>
    {
        if (!_values.Has(pair.Item1) || !_values.Has(pair.Item2)) return TResult.Unmatched(in pair, ref output);
        return Evaluate<(Entity, Entity), TOutput, TResult>(in pair, in _values.ReadKnown(pair.Item1), in _values.ReadKnown(pair.Item2), ref output);
    }
    internal bool EvaluateBound<TOutput, TResult>(in (Entity, Entity) pair, int first, int second, bool reversed, scoped ref TOutput output)
        where TOutput : allows ref struct
        where TResult : struct, ICategoryPermissionOutput<(Entity, Entity), TOutput>
    {
        if (!HasRows(first, second)) return TResult.Unmatched(in pair, ref output);
        return reversed ? Evaluate<(Entity, Entity), TOutput, TResult>(in pair, in _secondValues[second], in _firstValues[first], ref output)
            : Evaluate<(Entity, Entity), TOutput, TResult>(in pair, in _firstValues[first], in _secondValues[second], ref output);
    }
    internal bool Evaluate<TItem, TOutput, TResult>(scoped in TItem item, in TComponent first, in TComponent second,
        scoped ref TOutput output)
        where TItem : allows ref struct
        where TOutput : allows ref struct
        where TResult : struct, ICategoryPermissionOutput<TItem, TOutput> =>
        TPermission.Evaluate<TPermission, TRelation, TComponent, TImplementation, TItem, TOutput, TResult>(
            in _relation, ref _implementation, in first, in second, in item, ref output);
}
