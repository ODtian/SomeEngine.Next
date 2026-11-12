using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Spatial;

public static partial class SpatialQueries
{
    internal static Query Reads(Query members, QueryDescription reads)
    {
        foreach (int type in members.Description.Definition.AccessTypes) reads.OptionalReads.Add(type);
        return members.World.Query(reads);
    }

    public static RelationQuery<TreeCandidates<SpatialBounds, SpatialBoundsReader>, (Entity, Entity)>
        Candidates(World world, Query bodies) =>
        Candidates<SpatialBounds, SpatialBoundsReader>(world, bodies, default);

    public static RelationQuery<TreeCandidates<SpatialBounds, SpatialBoundsReader>, (Entity, Entity)>
        Candidates(World world, Query bodies, BoundsTree tree) =>
        Candidates<SpatialBounds, SpatialBoundsReader>(world, bodies, tree, default);

    public static RelationQuery<TreeCandidates<TComponent, TBoundsReader>, (Entity, Entity)>
        Candidates<TComponent, TBoundsReader>(World world, Query bodies, TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TComponent> =>
        new(new TreeCandidates<TComponent, TBoundsReader>(world, bodies, boundsReader));

    public static RelationQuery<TreeCandidates<TComponent, TBoundsReader>, (Entity, Entity)>
        Candidates<TComponent, TBoundsReader>(World world, Query bodies, BoundsTree tree,
            TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TComponent> =>
        new(new TreeCandidates<TComponent, TBoundsReader>(world, bodies, tree, boundsReader));

    public static BoundsTree Build(World world, Query bodies) =>
        Build<SpatialBounds, SpatialBoundsReader>(world, bodies, default);

    public static BoundsTree Build<TComponent, TBoundsReader>(World world, Query bodies,
        TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TComponent>
    {
        var tree = new BoundsTree();
        tree.Rebuild<TComponent, TBoundsReader>(world, bodies, boundsReader);
        return tree;
    }
}

/// <summary>Builds the current input's AABB tree and emits unique unordered overlaps.</summary>
public readonly struct TreeCandidates<TComponent, TBoundsReader> : ISpatialPartitionEnumerable<BoundsNodePair>
    where TComponent : struct
    where TBoundsReader : struct, ISpatialBoundsReader<TComponent>
{
    private readonly Query _bodies;
    private readonly TBoundsReader _boundsReader;
    private readonly bool _buildFromQuery;

    public TreeCandidates(World world, Query bodies, TBoundsReader boundsReader)
        : this(world, bodies, new BoundsTree(), boundsReader, true) { }

    public TreeCandidates(World world, Query bodies, BoundsTree tree, TBoundsReader boundsReader)
        : this(world, bodies, tree, boundsReader, false) { }

    private TreeCandidates(World world, Query bodies, BoundsTree tree,
        TBoundsReader boundsReader, bool buildFromQuery)
    {
        World = world;
        _bodies = bodies;
        _boundsReader = boundsReader;
        _buildFromQuery = buildFromQuery;
        Tree = tree;
    }

    public World World { get; }

    public BoundsTree Tree { get; }

    internal bool BuildsFromQuery => _buildFromQuery;

    internal SpatialBounds ReadBounds(in TComponent value) => _boundsReader.Read(in value);

    internal TreeCandidates<TComponent, TBoundsReader> WithTree() =>
        new(World, _bodies, Tree, _boundsReader);

    internal void BuildTree(scoped RelationQueryViews queries) =>
        Tree.Rebuild<TComponent, TBoundsReader>(queries.First, _boundsReader);

    public int CreatePartitions(Span<BoundsNodePair> partitions) => Tree.CreatePartitions(partitions);

    public int QueryCount => 1;

    public static bool ReadsFromSingleQuery => true;

    public Query GetQuery(int position) => _bodies;

    public QueryDescription AddReads(QueryDescription definition, int position) =>
        definition.Optional<TComponent>();

    public void Prepare(scoped RelationQueryViews queries)
    {
        if (_buildFromQuery)
            BuildTree(queries);
    }

    public bool Enumerate<TOutput>(BoundsNodePair partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        SpatialTraversal.Enumerate<SpatialAll, GroupMatch, TOutput>(Tree, queries, default, true, partition, ref output);

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        if (_buildFromQuery)
            Tree.Rebuild<TComponent, TBoundsReader>(queries.First, _boundsReader);
        if (Tree.Root < 0)
            return true;

        ReadOnlySpan<BoundsNode> nodes = Tree.Nodes;
        Span<(int First, int Second)> work = stackalloc (int, int)[2 * Tree.Height + 1];
        int count = 0;
        work[count++] = (Tree.Root, Tree.Root);
        while (count > 0)
        {
            var pair = work[--count];
            ref readonly BoundsNode first = ref nodes[pair.First];
            ref readonly BoundsNode second = ref nodes[pair.Second];
            if (pair.First == pair.Second)
            {
                if (first.IsLeaf)
                    continue;
                work[count++] = (first.SecondChild, first.SecondChild);
                work[count++] = (first.FirstChild, first.SecondChild);
                work[count++] = (first.FirstChild, first.FirstChild);
                continue;
            }
            if (!first.Bounds.Overlaps(in second.Bounds))
                continue;
            if (first.IsLeaf && second.IsLeaf)
            {
                var entities = EntityOrder.OrderPair(first.Entity, second.Entity);
                if (!_buildFromQuery &&
                    (!queries.First.Contains(entities.Item1) || !queries.First.Contains(entities.Item2)))
                    continue;
                if (!output.Add(in entities))
                    return false;
            }
            else if (second.IsLeaf || !first.IsLeaf && first.Height >= second.Height)
            {
                work[count++] = (first.SecondChild, pair.Second);
                work[count++] = (first.FirstChild, pair.Second);
            }
            else
            {
                work[count++] = (pair.First, second.SecondChild);
                work[count++] = (pair.First, second.FirstChild);
            }
        }
        return true;
    }
}
