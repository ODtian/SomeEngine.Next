using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public struct CollisionPairCandidates<TSource, TPartition, TFilter>(TSource source, TFilter filter) : ISpatialPartitionEnumerable<TPartition>
    where TSource : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct where TFilter : struct, ICollisionPairFilter
{
    public TSource Source => source;
    public TFilter Filter => filter;
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TSource.ReadsFromSingleQuery;
    public Query GetQuery(int position) => source.GetQuery(position);
    public QueryDescription AddReads(QueryDescription description, int position) => filter.AddReads(source.AddReads(description, position));
    public void Prepare(scoped RelationQueryViews views) => source.Prepare(views);
    public int CreatePartitions(Span<TPartition> partitions) => source.CreatePartitions(partitions);
    public bool Enumerate<TOutput>(scoped RelationQueryViews views, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var filtered = new Filtered<TOutput>(World, filter, output);
        try { return source.Enumerate(views, ref filtered); } finally { output = filtered.Value; }
    }
    public bool Enumerate<TOutput>(TPartition partition, scoped RelationQueryViews views, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var filtered = new Filtered<TOutput>(World, filter, output);
        try { return source.Enumerate(partition, views, ref filtered); } finally { output = filtered.Value; }
    }
    private ref struct Filtered<TOutput>(World world, TFilter filter, TOutput value) : IQueryRelationOutput<(Entity, Entity)>
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        internal TOutput Value = value;
        public static bool UsesBoundColumns => false;
        public void Bind(QueryChunk first, QueryChunk second) { }
        public bool Add(in (Entity, Entity) pair) => !filter.Matches(world, pair.Item1, pair.Item2) || Value.Add(in pair);
        public bool Add(in (Entity, Entity) pair, int first, int second, bool reversed) => Add(in pair);
    }
}
