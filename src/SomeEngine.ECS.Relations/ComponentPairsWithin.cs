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
        TRelation predicate = relation;
        var firstRows = queries.First.Rows;
        while (firstRows.MoveNext(out var first))
        {
            bool hasFirst = first.TryReadRef<TFirst>(out var firstValue);
            bool hasSecond = first.TryReadRef<TSecond>(out var secondValue);
            var secondRows = firstRows;
            while (secondRows.MoveNext(out var second))
            {
                bool ascending = EntityOrder.Compare(first.Entity, second.Entity) < 0;
                bool matches = ascending
                    ? hasFirst && second.TryReadRef<TSecond>(out var lastValue) && predicate.Matches(in firstValue.Value, in lastValue.Value)
                    : hasSecond && second.TryReadRef<TFirst>(out var startValue) && predicate.Matches(in startValue.Value, in secondValue.Value);
                if (!TSelection.Accepts(matches))
                    continue;
                var pair = ascending ? (first.Entity, second.Entity) : (second.Entity, first.Entity);
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
