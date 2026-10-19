using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

/// <summary>Within-query pairing that binds its existing component rows before emitting identities.</summary>
public readonly struct ComponentPairsWithin<TRelation, TFirst, TSecond>(
    EntityPairsWithin source, TRelation relation, bool exclude) : IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
{
    public World World => source.World;
    public int QueryCount => 1;
    public static bool ReadsFromSingleQuery => true;
    public QueryHandle GetQuery(int position) => source.GetQuery(position);
    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        definition.WithOptionalRead<TFirst>().WithOptionalRead<TSecond>();

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        => exclude ? Enumerate<ExceptSelection, TOutput>(queries, ref output)
            : Enumerate<WhereSelection, TOutput>(queries, ref output);

    private bool Enumerate<TSelection, TOutput>(RelationQueryViews queries, ref TOutput output)
        where TSelection : struct, IRelationSelection
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        bool required = queries.First.Requires<TFirst>() &&
            (typeof(TFirst) == typeof(TSecond) || queries.First.Requires<TSecond>());
        return required ? Enumerate<TSelection, RequiredRelationComponents, TOutput>(queries, ref output)
            : Enumerate<TSelection, OptionalRelationComponents, TOutput>(queries, ref output);
    }

    private bool Enumerate<TSelection, TPresence, TOutput>(RelationQueryViews queries, ref TOutput output)
        where TSelection : struct, IRelationSelection
        where TPresence : struct, IRelationComponentPresence
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        TRelation predicate = relation;
        var firstRows = queries.First.ReadRows<TFirst, TSecond>();
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
                    ? ascending ? output.Add(in pair, in first, in second) : output.Add(in pair, in second, in first)
                    : output.Add(in pair);
                if (!continued)
                    return false;
            }
        }
        return true;
    }
}
