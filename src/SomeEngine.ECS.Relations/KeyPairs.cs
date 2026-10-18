using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public readonly struct KeyPairs<TPairing, TRelation, TImplementation, TComponent, TKey>
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TPairing : struct, IKeyPairing
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly QueryHandle _first;
    private readonly QueryHandle _second;
    private readonly TRelation _relation;
    private readonly TImplementation _implementation;
    private readonly bool _exclude;

    public KeyPairs(World world, QueryHandle first, QueryHandle second, TRelation relation,
        TImplementation implementation, bool exclude)
    {
        World = world;
        _first = first;
        _second = second;
        _relation = relation;
        _implementation = implementation;
        _exclude = exclude;
    }

    public World World { get; }

    public int QueryCount => TPairing.QueryCount;

    public static bool ReadsFromSingleQuery => TPairing.ReadsFromSingleQuery;

    public QueryHandle GetQuery(int position) => position == 0 ? _first : _second;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition.WithOptionalRead<TComponent>();

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryCursor second = QueryCount == 1 ? queries.First : queries.Second;
        var firstGroups = TPairing.ReadsFromSingleQuery ? new KeyGroups<TComponent, TKey>(World, queries.First)
            : new KeyGroups<TComponent, TKey>(World, queries.First, queries.Access);
        var secondGroups = QueryCount == 1 ? firstGroups : new KeyGroups<TComponent, TKey>(World, second, queries.Access);
        foreach (var firstGroup in firstGroups)
        {
            foreach (var secondGroup in secondGroups)
            {
                bool hasKeys = firstGroup.HasKey && secondGroup.HasKey;
                bool forwardMatch = hasKeys && _implementation.Matches(in _relation, in firstGroup.Key, in secondGroup.Key);
                bool reverseMatch = TPairing.ReordersPairs
                    ? hasKeys && _implementation.Matches(in _relation, in secondGroup.Key, in firstGroup.Key)
                    : forwardMatch;
                bool forwardAccepted = forwardMatch != _exclude;
                bool reverseAccepted = reverseMatch != _exclude;
                if (!forwardAccepted && !reverseAccepted)
                    continue;
                foreach (Entity firstEntity in firstGroup.Entities)
                {
                    if (!queries.First.TryGetRow(firstEntity, out _))
                        continue;
                    foreach (Entity secondEntity in secondGroup.Entities)
                    {
                        if (!TPairing.TryPair(in queries, firstEntity, secondEntity, out var pair) ||
                            !second.TryGetRow(secondEntity, out _))
                            continue;
                        bool accepted = TPairing.ReordersPairs && pair.Item1 != firstEntity
                            ? reverseAccepted : forwardAccepted;
                        if (!accepted)
                            continue;
                        if (!output.Add(in pair))
                            return false;
                    }
                }
            }
        }
        return true;
    }
}
