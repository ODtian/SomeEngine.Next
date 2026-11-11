using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements;

internal static class FocusInputData
{
    internal static Entity GetInput(World world)
    {
        DevelopmentCheck.Require(world is not null, "Focus input requires a World.");
        Query query = world!.Query(new QueryDescription().WithStored<InputCapture>());
        (Entity Entity, int Count) result = default;
        using (QueryScope range = query.OpenRead())
            foreach (QueryChunk chunk in range)
                foreach (Entity entity in chunk.Entities)
                {
                    result.Entity = entity;
                    result.Count++;
                }
        DevelopmentCheck.Require(result.Count <= 1, "Focus input requires one InputCapture per World.");
        Entity input = result.Count == 0 ? world.Create(new InputCapture()) : result.Entity;
        if (!world.Has<Buffer<FocusInput>>(input))
            world.AddBuffer<FocusInput>(input);
        return input;
    }

    internal static void Enqueue(World world, FocusInput input)
    {
        Entity entity = GetInput(world);
        using DynamicBuffer<FocusInput> events = world.GetBuffer<FocusInput>(entity);
        events.Add(input);
    }
}
