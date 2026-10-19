using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

/// <summary>Within-query pairing that binds its existing component rows before emitting identities.</summary>
public readonly struct ComponentPairsWithin<TRelation, TFirst, TSecond, TSelection>(
    EntityPairsWithin source, TRelation relation) : IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
    where TSelection : struct, IRelationSelection
{
    private readonly bool _componentsRequired = ReadComponentRequirements(source);

    public World World => source.World;
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public QueryHandle GetQuery(int position) => source.GetQuery(position);
    public QueryDefinition AddReads(QueryDefinition definition, int position)
    {
        QueryDefinition reads = definition.WithOptionalRead<TFirst>();
        return typeof(TFirst) == typeof(TSecond) ? reads : reads.WithOptionalRead<TSecond>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryCursor members = queries.First;
        return _componentsRequired ? Enumerate<RequiredRelationComponents, TOutput>(members, relation, ref output)
            : Enumerate<OptionalRelationComponents, TOutput>(members, relation, ref output);
    }

    private static bool ReadComponentRequirements(EntityPairsWithin source)
    {
        QueryDefinition definition = source.World.GetQueryDefinition(source.GetQuery(0));
        return definition.Requires<TFirst>() &&
            (typeof(TFirst) == typeof(TSecond) || definition.Requires<TSecond>());
    }

    private static bool Enumerate<TPresence, TOutput>(scoped QueryCursor members, TRelation predicate, ref TOutput output)
        where TPresence : struct, IRelationComponentPresence
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var firstRows = members.ReadRows<TFirst, TSecond>();
        while (firstRows.MoveNext(out var first, out var firstValue, out var secondValue))
        {
            Entity firstEntity = first.Entity;
            var secondRows = firstRows;
            while (secondRows.MoveNext(out var second, out var startValue, out var lastValue))
            {
                Entity secondEntity = default;
                bool ascending = false;
                ComponentReference<TFirst> relationFirst = firstValue;
                ComponentReference<TSecond> relationSecond = lastValue;
                if (typeof(TFirst) != typeof(TSecond) || !TRelation.IsSymmetric)
                {
                    secondEntity = second.Entity;
                    ascending = EntityOrder.Precedes(firstEntity, secondEntity);
                    relationFirst = ascending ? firstValue : startValue;
                    relationSecond = ascending ? lastValue : secondValue;
                }
                if (TSelection.RequiresMatch)
                {
                    if (!TPresence.IsRequired && (!relationFirst.HasValue || !relationSecond.HasValue))
                        continue;
                    if (!predicate.Matches(in relationFirst.Value, in relationSecond.Value))
                        continue;
                }
                else if ((TPresence.IsRequired || (relationFirst.HasValue && relationSecond.HasValue)) &&
                    predicate.Matches(in relationFirst.Value, in relationSecond.Value))
                    continue;
                if (typeof(TFirst) == typeof(TSecond) && TRelation.IsSymmetric)
                {
                    secondEntity = second.Entity;
                    ascending = EntityOrder.Precedes(firstEntity, secondEntity);
                }
                var pair = ascending ? (firstEntity, secondEntity) : (secondEntity, firstEntity);
                bool continued = TOutput.ReadsComponentRows
                    ? ascending ? output.Add(in pair, first, second) : output.Add(in pair, second, first)
                    : output.Add(in pair);
                if (!continued)
                    return false;
            }
        }
        return true;
    }
}
