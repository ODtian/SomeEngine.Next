using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Elements;

internal static class FocusInputData
{
    internal static Entity GetInput(World world)
    {
        DevelopmentCheck.Require(world is not null, "Focus input requires a World.");
        QueryHandle query = world.Query(world.QueryDefinition().All<InputCapture>());
        (Entity Entity, int Count) result = default;
        try
        {
            world.ExecuteQuery(query, ref result, static (rows, ref value) =>
            {
                foreach (var row in rows.Rows)
                {
                    value.Entity = row.Entity;
                    value.Count++;
                }
            });
        }
        finally
        {
            world.ReleaseQuery(query);
        }
        DevelopmentCheck.Require(result.Count <= 1, "Focus input requires one InputCapture per World.");
        Entity input = result.Count == 0 ? world.CreateEntity(new InputCapture()) : result.Entity;
        if (!world.HasBuffer<FocusInput>(input))
            world.AddBuffer<FocusInput>(input);
        return input;
    }

    internal static void Enqueue(World world, FocusInput input)
    {
        Entity entity = GetInput(world);
        world.ExecuteBufferWrite<FocusInput, FocusInput>(entity, ref input,
            static (events, ref value) => events.Add(value));
    }
}
