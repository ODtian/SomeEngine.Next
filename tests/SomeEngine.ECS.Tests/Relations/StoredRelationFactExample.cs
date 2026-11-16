using SomeEngine.ECS.Entities;

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
    public static RelationQuery<TransferFacts, (Entity, Entity, Entity)> Transfers(this World world, Query facts) =>
        new(new TransferFacts(world, facts));
}

public readonly struct TransferFacts(World world, Query facts) : IQueryRelationEnumerable<(Entity, Entity, Entity)>
{
    public World World => world;

    public int QueryCount => 1;

    public Query GetQuery(int position) => facts;

    public QueryDescription AddReads(QueryDescription definition, int position) =>
        definition.Optional<TransferFact>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity, Entity)>, allows ref struct
    {
        foreach (QueryChunk chunk in queries.First)
        {
            ReadOnlySpan<TransferFact> facts = chunk.Read<TransferFact>();
            foreach (ref readonly TransferFact fact in facts)
            {
                var transfer = (fact.Source, fact.Carrier, fact.Destination);
                if (!output.Add(in transfer)) return false;
            }
        }
        return true;
    }
}
