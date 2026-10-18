using System.Runtime.CompilerServices;
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
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        TRelation predicate = relation;
        var firstRows = queries.First.Rows;
        while (firstRows.MoveNext(out var first))
        {
            bool hasFirst = first.Has<TFirst>();
            bool hasSecond = first.Has<TSecond>();
            ref readonly TFirst firstValue = ref (hasFirst ? ref first.ReadRef<TFirst>() : ref Unsafe.NullRef<TFirst>());
            ref readonly TSecond secondValue = ref (hasSecond ? ref first.ReadRef<TSecond>() : ref Unsafe.NullRef<TSecond>());
            var secondRows = firstRows;
            while (secondRows.MoveNext(out var second))
            {
                bool ascending = EntityOrder.Compare(first.Entity, second.Entity) < 0;
                bool matches = ascending
                    ? hasFirst && second.Has<TSecond>() && predicate.Matches(in firstValue, in second.ReadRef<TSecond>())
                    : hasSecond && second.Has<TFirst>() && predicate.Matches(in second.ReadRef<TFirst>(), in secondValue);
                if (matches == exclude)
                    continue;
                var pair = ascending ? (first.Entity, second.Entity) : (second.Entity, first.Entity);
                if (!output.Add(in pair))
                    return false;
            }
        }
        return true;
    }
}
