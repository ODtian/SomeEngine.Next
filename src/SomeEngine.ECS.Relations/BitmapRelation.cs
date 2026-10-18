using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public ref struct BitmapRelation<TRelation, TImplementation, TComponent, TPermission> : IRelation<(Entity, Entity)>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{
    private readonly QueryCursor _query;
    private TRelation _relation;
    private TImplementation _implementation;
    private readonly bool _exclude;

    public BitmapRelation(QueryCursor query, TRelation relation, TImplementation implementation, bool exclude)
    {
        _query = query;
        _relation = relation;
        _implementation = implementation;
        _exclude = exclude;
    }

    public bool Matches(in (Entity, Entity) pair)
    {
        bool matches = false;
        if (_query.TryGetRow(pair.Item1, out var first) && first.Has<TComponent>() &&
            _query.TryGetRow(pair.Item2, out var second) && second.Has<TComponent>())
        {
            CategoryBits firstBits = _implementation.Read(in _relation, in first.ReadRef<TComponent>());
            CategoryBits secondBits = _implementation.Read(in _relation, in second.ReadRef<TComponent>());
            matches = TPermission.Matches(in firstBits, in secondBits);
        }
        return matches != _exclude;
    }
}
