using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public ref struct ComponentRelation<TRelation, TComponent> : IRelation<Entity>
    where TRelation : struct, IRelation<TComponent>, allows ref struct
    where TComponent : struct
{
    private readonly QueryCursor _query;
    private TRelation _relation;

    public ComponentRelation(QueryCursor query, TRelation relation)
    {
        _query = query;
        _relation = relation;
    }

    public bool Matches(in Entity entity)
    {
        if (!_query.TryGetRow(entity, out var row) || !row.Has<TComponent>())
            return false;
        return _relation.Matches(in row.ReadRef<TComponent>());
    }
}
