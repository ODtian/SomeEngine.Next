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
        return TryReadComponents(in pair, out var first, out var second)
            && TPermission.Matches<TRelation, TComponent, TImplementation>(in _relation, ref _implementation,
                in first.Value, in second.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(in (Entity, Entity) pair, scoped in QueryRow first, scoped in QueryRow second)
    {
        if (!TryReadComponents(in first, in second, out var firstValue, out var secondValue))
            return false;
        return TPermission.Matches<TRelation, TComponent, TImplementation>(in _relation, ref _implementation,
            in firstValue.Value, in secondValue.Value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadComponents(in (Entity, Entity) pair, out ComponentReference<TComponent> firstValue,
        out ComponentReference<TComponent> secondValue)
    {
        firstValue = default;
        secondValue = default;
        if (!_query.TryGetRow(pair.Item1, out var first) || !_query.TryGetRow(pair.Item2, out var second))
            return false;
        return TryReadComponents(in first, in second, out firstValue, out secondValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryReadComponents(scoped in QueryRow first, scoped in QueryRow second,
        out ComponentReference<TComponent> firstValue, out ComponentReference<TComponent> secondValue)
    {
        secondValue = default;
        return first.TryReadRef(out firstValue) && second.TryReadRef(out secondValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Evaluate<TItem, TOutput, TResult>(scoped in TItem item, in TComponent first, in TComponent second,
        scoped ref TOutput output)
        where TItem : allows ref struct
        where TOutput : allows ref struct
        where TResult : struct, ICategoryPermissionOutput<TItem, TOutput>
    {
        return TPermission.Evaluate<TPermission, TRelation, TComponent, TImplementation, TItem, TOutput, TResult>(
            in _relation, ref _implementation, in first, in second, in item, ref output);
    }
}
