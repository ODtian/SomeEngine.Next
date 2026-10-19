using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

public ref struct BitmapRelation<TRelation, TImplementation, TComponent, TPermission> : IQueryRelation<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{
    private readonly QueryCursor _query;
    private TRelation _relation;
    private TImplementation _implementation;

    public BitmapRelation(QueryCursor query, TRelation relation, TImplementation implementation)
    {
        _query = query;
        _relation = relation;
        _implementation = implementation;
    }

    public bool Matches(in (Entity, Entity) pair)
    {
        return _query.TryGetRow(pair.Item1, out var first) && _query.TryGetRow(pair.Item2, out var second)
            && Matches(in pair, in first, in second);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(in (Entity, Entity) pair, scoped in QueryRow first, scoped in QueryRow second)
    {
        if (!first.TryReadRef<TComponent>(out var firstValue) || !second.TryReadRef<TComponent>(out var secondValue))
            return false;
        return TPermission.Matches<TRelation, TComponent, TImplementation>(in _relation, ref _implementation,
            in firstValue.Value, in secondValue.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Evaluate<TOutput, TResult>(in (Entity, Entity) pair, scoped ref TOutput output)
        where TOutput : allows ref struct
        where TResult : struct, ICategoryPermissionOutput<(Entity, Entity), TOutput>
    {
        if (!_query.TryGetRow(pair.Item1, out var first) || !_query.TryGetRow(pair.Item2, out var second))
            return TResult.Unmatched(in pair, ref output);
        return Evaluate<(Entity, Entity), TOutput, TResult>(in pair, in first, in second, ref output);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Evaluate<TItem, TOutput, TResult>(scoped in TItem item, scoped in QueryRow first, scoped in QueryRow second,
        scoped ref TOutput output)
        where TItem : allows ref struct
        where TOutput : allows ref struct
        where TResult : struct, ICategoryPermissionOutput<TItem, TOutput>
    {
        if (!first.TryReadRef<TComponent>(out var firstValue) || !second.TryReadRef<TComponent>(out var secondValue))
            return TResult.Unmatched(in item, ref output);
        return TPermission.Evaluate<TPermission, TRelation, TComponent, TImplementation, TItem, TOutput, TResult>(
            in _relation, ref _implementation, in firstValue.Value, in secondValue.Value, in item, ref output);
    }
}
