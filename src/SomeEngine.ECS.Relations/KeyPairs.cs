using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryCursor first = queries.First;
        if (TPairing.QueryCount == 1 && TPairing.ReadsFromSingleQuery)
            return Enumerate(World, _exclude, in _relation, in _implementation, first, first, first, ref output);
        QueryCursor second = queries.Second;
        QueryCursor access = queries.Access;
        return Enumerate(World, _exclude, in _relation, in _implementation, first, second, access, ref output);
    }

    private static bool Enumerate<TOutput>(World world, bool exclude, scoped in TRelation relation,
        scoped in TImplementation implementation, scoped QueryCursor first, scoped QueryCursor second,
        scoped QueryCursor access, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var firstGroups = TPairing.ReadsFromSingleQuery ? new KeyGroups<TComponent, TKey>(world, in first)
            : new KeyGroups<TComponent, TKey>(world, in first, in access);
        var secondGroups = TPairing.QueryCount == 1 ? firstGroups : new KeyGroups<TComponent, TKey>(world, in second, in access);
        var firstValues = firstGroups.GetEnumerator();
        while (firstValues.MoveNext(out var firstGroup))
        {
            var secondValues = secondGroups.GetEnumerator();
            while (secondValues.MoveNext(out var secondGroup))
            {
                bool hasKeys = firstGroup.HasKey && secondGroup.HasKey;
                bool forwardMatch = hasKeys && implementation.Matches(in relation, in firstGroup.Key, in secondGroup.Key);
                bool reverseMatch = TPairing.ReordersPairs
                    ? hasKeys && implementation.Matches(in relation, in secondGroup.Key, in firstGroup.Key)
                    : forwardMatch;
                bool forwardAccepted = forwardMatch != exclude;
                bool reverseAccepted = reverseMatch != exclude;
                if (!forwardAccepted && !reverseAccepted)
                    continue;
                foreach (Entity firstEntity in firstGroup.Entities)
                {
                    scoped QueryRow firstRow = default;
                    bool firstMember = TPairing.ReadsFromSingleQuery && TOutput.ReadsComponentRows
                        ? first.TryGetRow(firstEntity, out firstRow)
                        : first.Contains(firstEntity);
                    if (!firstMember)
                        continue;
                    foreach (Entity secondEntity in secondGroup.Entities)
                    {
                        if (!TPairing.TryPair(in first, in second, firstEntity, secondEntity, out var pair))
                            continue;
                        scoped QueryRow secondRow = default;
                        bool secondMember;
                        if (TPairing.ReadsFromSingleQuery && TOutput.ReadsComponentRows)
                            secondMember = first.TryGetRow(secondEntity, out secondRow);
                        else
                            secondMember = TPairing.QueryCount == 1
                                ? first.Contains(secondEntity) : second.Contains(secondEntity);
                        if (!secondMember)
                            continue;
                        bool accepted = TPairing.ReordersPairs && pair.Item1 != firstEntity
                            ? reverseAccepted : forwardAccepted;
                        if (!accepted)
                            continue;
                        bool continued = TPairing.ReadsFromSingleQuery && TOutput.ReadsComponentRows
                            ? output.Add(in pair, firstRow, secondRow) : output.Add(in pair);
                        if (!continued)
                            return false;
                    }
                }
            }
        }
        return true;
    }
}
