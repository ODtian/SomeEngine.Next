using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Remaining-row pair combinations with the default motion rule written directly.</summary>
public static class HandwrittenDefaultPairs
{
    public static bool Enumerate<TOutput>(World world, QueryHandle bodies, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryHandle members = world.Query(world.GetQueryDefinition(bodies).WithOptionalRead<Body>());
        var execution = new PairExecution<TOutput>(output);
        try
        {
            world.ExecuteQuery(members, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            world.ReleaseQuery(members);
        }
    }

    private ref struct PairExecution<TOutput>(TOutput output) : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor members)
        {
            var firstRows = members.ReadRows<Body, Body>();
            while (firstRows.MoveNext(out var first, out var firstBody, out _))
            {
                Entity firstEntity = first.Entity;
                var secondRows = firstRows;
                while (secondRows.MoveNext(out var second, out var secondBody, out _))
                {
                    if (firstBody.Value.Kind != MotionKind.Dynamic && secondBody.Value.Kind != MotionKind.Dynamic)
                        continue;
                    var pair = EntityOrder.OrderPair(firstEntity, second.Entity);
                    if (!Output.Add(in pair))
                    {
                        Completed = false;
                        return;
                    }
                }
            }
            Completed = true;
        }
    }
}
