using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations.Tests;

// Business example: an ordinary ECS entity holds one three-position transfer fact.
public struct TransferFact : IComponent
{
    public Entity Source;
    public Entity Carrier;
    public Entity Destination;
}

public readonly struct TransferFits : IRelation<Score, Weight, Score>
{
    public bool Matches(in Score source, in Weight carrier, in Score destination) =>
        source.Value + destination.Value <= carrier.Value;
}

public static class TransferQueries
{
    public static RelationQuery<TransferFacts, (Entity, Entity, Entity)> Transfers(this World world, QueryHandle facts) =>
        new(new TransferFacts(world, facts));
}

public readonly struct TransferFacts(World world, QueryHandle facts) : IQueryRelationEnumerable<(Entity, Entity, Entity)>
{
    public World World => world;

    public int QueryCount => 1;

    public QueryHandle GetQuery(int position) => facts;

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        definition.WithOptionalRead<TransferFact>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity, Entity)>, allows ref struct
    {
        foreach (var entity in queries.First.Rows)
        {
            queries.Access.TryGetRow(entity.Entity, out var row);
            ref readonly var fact = ref row.ReadRef<TransferFact>();
            var transfer = (fact.Source, fact.Carrier, fact.Destination);
            if (!output.Add(in transfer))
                return false;
        }
        return true;
    }
}
