using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Bitmap;

/// <summary>Filters the same candidate list with direct component-field mask operations.</summary>
public static class HandwrittenBitmapCollision
{
    public static bool OneWay<TOutput>(World world, QueryHandle members,
        ReadOnlySpan<(Entity, Entity)> candidates, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryHandle query = world.Query(world.GetQueryDefinition(members).WithOptionalRead<CollisionLayer>());
        var execution = new OneWayExecution<TOutput>(candidates, output);
        try
        {
            world.ExecuteQuery(query, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            world.ReleaseQuery(query);
        }
    }

    public static bool Mutual<TOutput>(World world, QueryHandle members,
        ReadOnlySpan<(Entity, Entity)> candidates, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryHandle query = world.Query(world.GetQueryDefinition(members).WithOptionalRead<CollisionLayer>());
        var execution = new MutualExecution<TOutput>(candidates, output);
        try
        {
            world.ExecuteQuery(query, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            world.ReleaseQuery(query);
        }
    }

    private ref struct OneWayExecution<TOutput> : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<(Entity, Entity)> _candidates;

        public OneWayExecution(ReadOnlySpan<(Entity, Entity)> candidates, TOutput output)
        {
            _candidates = candidates;
            Output = output;
        }

        public TOutput Output;
        public bool Completed;

        public void Execute(QueryCursor query)
        {
            foreach (var pair in _candidates)
            {
                if (!query.TryGetRow(pair.Item1, out var first) || !first.Has<CollisionLayer>() ||
                    !query.TryGetRow(pair.Item2, out var second) || !second.Has<CollisionLayer>())
                    continue;
                ref readonly CollisionLayer firstLayer = ref first.ReadRef<CollisionLayer>();
                ref readonly CollisionLayer secondLayer = ref second.ReadRef<CollisionLayer>();
                if ((firstLayer.AllowedCategories & secondLayer.Categories) == 0)
                    continue;
                if (!Output.Add(in pair))
                {
                    Completed = false;
                    return;
                }
            }
            Completed = true;
        }
    }

    private ref struct MutualExecution<TOutput> : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<(Entity, Entity)> _candidates;

        public MutualExecution(ReadOnlySpan<(Entity, Entity)> candidates, TOutput output)
        {
            _candidates = candidates;
            Output = output;
        }

        public TOutput Output;
        public bool Completed;

        public void Execute(QueryCursor query)
        {
            foreach (var pair in _candidates)
            {
                if (!query.TryGetRow(pair.Item1, out var first) || !first.Has<CollisionLayer>() ||
                    !query.TryGetRow(pair.Item2, out var second) || !second.Has<CollisionLayer>())
                    continue;
                ref readonly CollisionLayer firstLayer = ref first.ReadRef<CollisionLayer>();
                ref readonly CollisionLayer secondLayer = ref second.ReadRef<CollisionLayer>();
                if ((firstLayer.AllowedCategories & secondLayer.Categories) == 0 ||
                    (secondLayer.AllowedCategories & firstLayer.Categories) == 0)
                    continue;
                if (!Output.Add(in pair))
                {
                    Completed = false;
                    return;
                }
            }
            Completed = true;
        }
    }
}
