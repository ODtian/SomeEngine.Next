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
                if (!query.TryGetRow(pair.Item1, out var first) || !first.TryReadRef<CollisionLayer>(out var firstLayer) ||
                    !query.TryGetRow(pair.Item2, out var second) || !second.TryReadRef<CollisionLayer>(out var secondLayer))
                    continue;
                if ((firstLayer.Value.AllowedCategories & secondLayer.Value.Categories) == 0)
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
                if (!query.TryGetRow(pair.Item1, out var first) || !first.TryReadRef<CollisionLayer>(out var firstLayer) ||
                    !query.TryGetRow(pair.Item2, out var second) || !second.TryReadRef<CollisionLayer>(out var secondLayer))
                    continue;
                if ((firstLayer.Value.AllowedCategories & secondLayer.Value.Categories) == 0 ||
                    (secondLayer.Value.AllowedCategories & firstLayer.Value.Categories) == 0)
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
