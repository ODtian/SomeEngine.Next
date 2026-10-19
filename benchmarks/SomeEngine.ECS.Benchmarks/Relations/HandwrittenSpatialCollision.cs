using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>The same tree traversal with the example's rules written directly at each leaf pair.</summary>
public static partial class HandwrittenSpatialCollision
{
    public static bool Enumerate<TOutput>(World world, QueryHandle bodies, CollisionTree tree, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryDefinition definition = world.GetQueryDefinition(bodies);
        bool temporaryQuery = definition.CanWrite;
        QueryHandle members = temporaryQuery ? world.Query(definition) : bodies;
        try
        {
            QueryHandle access = world.Query(QueryDefinition.Empty.WithAccessFrom(definition)
                .WithOptionalRead<Body>().WithOptionalRead<Hair>());
            var execution = new AccessExecution<TOutput>(world, members, tree, output);
            try
            {
                world.ExecuteQuery(access, ref execution);
                return execution.Completed;
            }
            finally
            {
                output = execution.Output;
                world.ReleaseQuery(access);
            }
        }
        finally
        {
            if (temporaryQuery)
                world.ReleaseQuery(members);
        }
    }

    private ref struct AccessExecution<TOutput>(World world, QueryHandle members, CollisionTree tree, TOutput output)
        : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor access)
        {
            var execution = new PairExecution<TOutput>(tree, access, Output);
            try
            {
                world.ExecuteQuery(members, ref execution);
                Completed = execution.Completed;
            }
            finally
            {
                Output = execution.Output;
            }
        }
    }

    private ref struct PairExecution<TOutput> : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly CollisionTree _tree;
        private readonly QueryCursor _access;

        public PairExecution(CollisionTree tree, QueryCursor access, TOutput output)
        {
            _tree = tree;
            _access = access;
            Output = output;
            Completed = false;
        }

        public TOutput Output;
        public bool Completed;

        public void Execute(QueryCursor members)
        {
            Completed = Traverse(members);
        }

        private bool Traverse(QueryCursor members)
        {
            if (_tree.Root < 0)
                return true;

            ReadOnlySpan<CollisionNode> nodes = _tree.Nodes;
            Span<(int First, int Second)> work = stackalloc (int, int)[2 * _tree.Height + 1];
            int count = 0;
            work[count++] = (_tree.Root, _tree.Root);
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
                    if (!members.TryGetRow(entities.Item1, out _) || !members.TryGetRow(entities.Item2, out _))
                        continue;
                    _access.TryGetRow(entities.Item1, out var firstRow);
                    _access.TryGetRow(entities.Item2, out var secondRow);
                    ref readonly Body firstBody = ref firstRow.ReadRef<Body>();
                    ref readonly Body secondBody = ref secondRow.ReadRef<Body>();
                    if (firstBody.Kind != MotionKind.Dynamic && secondBody.Kind != MotionKind.Dynamic)
                        continue;
                    if (firstRow.Has<Hair>() && secondRow.Has<Hair>() &&
                        firstRow.ReadRef<Hair>().Character == secondRow.ReadRef<Hair>().Character)
                        continue;
                    if (!Output.Add(in entities))
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
}
