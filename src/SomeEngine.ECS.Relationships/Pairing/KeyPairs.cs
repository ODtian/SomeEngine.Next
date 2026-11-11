using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public readonly struct KeyPairs<TPairing, TRelation, TImplementation, TComponent, TKey>
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TPairing : struct, IKeyPairing
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly Query _first, _second;
    private readonly TRelation _relation;
    private readonly TImplementation _implementation;
    private readonly bool _exclude;

    public KeyPairs(World world, Query first, Query second, TRelation relation, TImplementation implementation, bool exclude)
    {
        World = world; _first = first; _second = second;
        _relation = relation; _implementation = implementation; _exclude = exclude;
        _ = world.Index<TComponent, TKey>();
    }
    public World World { get; }
    public int QueryCount => TPairing.QueryCount;
    public static bool ReadsFromSingleQuery => TPairing.ReadsFromSingleQuery;
    public Query GetQuery(int position) => position == 0 ? _first : _second;
    public QueryDescription AddReads(QueryDescription definition, int position) => definition.Optional<TComponent>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryScope first = queries.First;
        QueryScope second = TPairing.QueryCount == 1 ? first : queries.Second;
        var firstGroups = new KeyGroups<TComponent, TKey>(World, first);
        var secondGroups = TPairing.QueryCount == 1 ? firstGroups : new KeyGroups<TComponent, TKey>(World, second);
        foreach (KeyGroup<TKey> firstGroup in firstGroups)
            foreach (KeyGroup<TKey> secondGroup in secondGroups)
            {
                bool hasKeys = firstGroup.HasKey && secondGroup.HasKey;
                bool forwardMatch = hasKeys && _implementation.Matches(in _relation, in firstGroup.Key, in secondGroup.Key);
                bool reverseMatch = TPairing.ReordersPairs
                    ? hasKeys && _implementation.Matches(in _relation, in secondGroup.Key, in firstGroup.Key) : forwardMatch;
                bool forward = forwardMatch != _exclude, reverse = reverseMatch != _exclude;
                if (!forward && !reverse) continue;
                foreach (Entity firstEntity in firstGroup.Entities)
                {
                    if (!first.Contains(firstEntity)) continue;
                    foreach (Entity secondEntity in secondGroup.Entities)
                    {
                        if (!second.Contains(secondEntity) || !TPairing.TryPair(in first, in second, firstEntity, secondEntity, out var pair)) continue;
                        bool accepted = TPairing.ReordersPairs && pair.Item1 != firstEntity ? reverse : forward;
                        if (accepted && !output.Add(in pair)) return false;
                    }
                }
            }
        return true;
    }
}
