using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using System.Runtime.CompilerServices;

namespace SomeEngine.Physics;

/// <summary>Composes concrete pair rules while retaining the candidate source's partition type.</summary>
public struct CollisionPairCandidates<TSource, TPartition, TFilter>(TSource source, TFilter filter)
    : ICollisionPartitionEnumerable<TPartition>
    where TSource : struct, ICollisionPartitionEnumerable<TPartition>
    where TPartition : struct
    where TFilter : struct, ICollisionPairFilter
{
    public TSource Source => source;
    public TFilter Filter => filter;
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TSource.ReadsFromSingleQuery;
    public QueryHandle GetQuery(int position) => source.GetQuery(position);
    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        filter.AddReads(source.AddReads(definition, position));
    public void Prepare(scoped RelationQueryViews queries) => source.Prepare(queries);
    public int CreatePartitions(Span<TPartition> partitions) => source.CreatePartitions(partitions);

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var filtered = new FilteredOutput<TOutput>(filter, queries.Access, new(ref output));
        return source.Enumerate(queries, ref filtered);
    }

    public bool Enumerate<TOutput>(TPartition partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var filtered = new FilteredOutput<TOutput>(filter, queries.Access, new(ref output));
        return source.Enumerate(partition, queries, ref filtered);
    }

    private ref struct FilteredOutput<TOutput> : IQueryRelationOutput<(Entity, Entity)>
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private TFilter _filter;
        private readonly QueryCursor _access;
        private readonly PairOutputReference<TOutput> _output;

        public FilteredOutput(TFilter filter, QueryCursor access, PairOutputReference<TOutput> output)
        {
            _filter = filter;
            _access = access;
            _output = output;
        }

        public bool Add(in (Entity, Entity) pair)
        {
            _access.TryGetRow(pair.Item1, out QueryRow first);
            _access.TryGetRow(pair.Item2, out QueryRow second);
            if (!_filter.Matches(first, second))
                return true;
            return TOutput.ReadsComponentRows ? _output.Value.Add(in pair, first, second) : _output.Value.Add(in pair);
        }
        public bool Add(in (Entity, Entity) pair, scoped QueryRow first, scoped QueryRow second) => Add(in pair);
    }

    private readonly ref struct PairOutputReference<TOutput> where TOutput : allows ref struct
    {
        // Preserve the caller's managed output address across callback-local query borrows.
        private readonly ref byte _value;
        internal PairOutputReference(ref TOutput output) => _value = ref Unsafe.As<TOutput, byte>(ref output);
        internal ref TOutput Value => ref Unsafe.As<byte, TOutput>(ref _value);
    }
}
