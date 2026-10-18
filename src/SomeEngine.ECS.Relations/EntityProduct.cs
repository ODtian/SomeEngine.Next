using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<EntityProduct, Entity> Product(this World world, QueryHandle members) =>
        new(new EntityProduct(world, members));
}

public readonly struct EntityProduct : IQueryRelationEnumerable<Entity>
{
    private readonly QueryHandle _members;

    public EntityProduct(World world, QueryHandle members)
    {
        World = world;
        _members = members;
    }

    public World World { get; }

    public int QueryCount => 1;

    public QueryHandle GetQuery(int position) => _members;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<Entity>, allows ref struct
    {
        foreach (var row in queries.First.Rows)
        {
            Entity entity = row.Entity;
            if (!output.Add(in entity))
                return false;
        }
        return true;
    }
}
