using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public static class Collision
{
    public static RelationQuery<CollisionCandidates<CollisionBounds, CollisionBoundsReader>, (Entity, Entity)>
        Candidates(World world, QueryHandle bodies) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(world, bodies, default);

    public static RelationQuery<CollisionCandidates<CollisionBounds, CollisionBoundsReader>, (Entity, Entity)>
        Candidates(World world, QueryHandle bodies, CollisionTree tree) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(world, bodies, tree, default);

    public static RelationQuery<CollisionCandidates<TComponent, TBoundsReader>, (Entity, Entity)>
        Candidates<TComponent, TBoundsReader>(World world, QueryHandle bodies, TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent> =>
        new(new CollisionCandidates<TComponent, TBoundsReader>(world, bodies, boundsReader));

    public static RelationQuery<CollisionCandidates<TComponent, TBoundsReader>, (Entity, Entity)>
        Candidates<TComponent, TBoundsReader>(World world, QueryHandle bodies, CollisionTree tree,
            TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent> =>
        new(new CollisionCandidates<TComponent, TBoundsReader>(world, bodies, tree, boundsReader));

    public static CollisionTree Build(World world, QueryHandle bodies) =>
        Build<CollisionBounds, CollisionBoundsReader>(world, bodies, default);

    public static CollisionTree Build<TComponent, TBoundsReader>(World world, QueryHandle bodies,
        TBoundsReader boundsReader)
        where TComponent : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
    {
        var tree = new CollisionTree();
        tree.Rebuild<TComponent, TBoundsReader>(world, bodies, boundsReader);
        return tree;
    }
}

/// <summary>Builds the current input's AABB tree and emits unique unordered overlaps.</summary>
public readonly struct CollisionCandidates<TComponent, TBoundsReader> : IQueryRelationEnumerable<(Entity, Entity)>
    where TComponent : struct
    where TBoundsReader : struct, ICollisionBoundsReader<TComponent>
{
    private readonly QueryHandle _bodies;
    private readonly TBoundsReader _boundsReader;
    private readonly bool _buildFromQuery;

    public CollisionCandidates(World world, QueryHandle bodies, TBoundsReader boundsReader)
        : this(world, bodies, new CollisionTree(), boundsReader, true) { }

    public CollisionCandidates(World world, QueryHandle bodies, CollisionTree tree, TBoundsReader boundsReader)
        : this(world, bodies, tree, boundsReader, false) { }

    private CollisionCandidates(World world, QueryHandle bodies, CollisionTree tree,
        TBoundsReader boundsReader, bool buildFromQuery)
    {
        World = world;
        _bodies = bodies;
        _boundsReader = boundsReader;
        _buildFromQuery = buildFromQuery;
        Tree = tree;
    }

    public World World { get; }

    public CollisionTree Tree { get; }

    internal bool BuildsFromQuery => _buildFromQuery;

    internal CollisionBounds ReadBounds(QueryRow row) => _boundsReader.Read(in row.ReadRef<TComponent>());

    internal CollisionCandidates<TComponent, TBoundsReader> WithTree() =>
        new(World, _bodies, Tree, _boundsReader);

    internal void BuildTree(RelationQueryViews queries) =>
        Tree.Rebuild<TComponent, TBoundsReader>(queries.First, queries.Access, _boundsReader);

    public int QueryCount => 1;

    public QueryHandle GetQuery(int position) => _bodies;

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        definition.WithOptionalRead<TComponent>();

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        if (_buildFromQuery)
            Tree.Rebuild<TComponent, TBoundsReader>(queries.First, queries.Access, _boundsReader);
        if (Tree.Root < 0)
            return true;

        ReadOnlySpan<CollisionNode> nodes = Tree.Nodes;
        Span<(int First, int Second)> work = stackalloc (int, int)[2 * Tree.Height + 1];
        int count = 0;
        work[count++] = (Tree.Root, Tree.Root);
        while (count > 0)
        {
            var pair = work[--count];
            ref readonly CollisionNode first = ref nodes[pair.First];
            ref readonly CollisionNode second = ref nodes[pair.Second];
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
                    (!queries.First.TryGetRow(entities.Item1, out _) || !queries.First.TryGetRow(entities.Item2, out _)))
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
