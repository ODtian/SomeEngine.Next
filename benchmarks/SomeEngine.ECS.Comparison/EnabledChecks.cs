using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class EnabledChecks
{
    internal static void Run()
    {
        using var world = new World();
        var entities = new Entity[130];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Create(new Signal { Value = i });
        Query active = world.Query(new QueryDescription().With<Signal>());
        Query disabled = world.Query(new QueryDescription().WithDisabled<Signal>());
        Query inactive = world.Query(new QueryDescription().Without<Signal>());
        Require(Count(active) == 130 && Count(disabled) == 0, "new components are enabled");
        for (int i = 0; i < entities.Length; i += 3) world.Disable<Signal>(entities[i]);
        world.Set(entities[0], new Signal { Value = 42 });
        world.Add(entities[0], new Extra { Value = 7 });
        world.Destroy(entities[1]);
        Require(world.Has<Signal>(entities[0]) && world.Read<Signal>(entities[0]).Value == 42
            && !world.IsEnabled<Signal>(entities[0]) && Count(active) == 85 && Count(disabled) == 44
            && Count(inactive) == 44, "disabled values survive writes, migration and fill hole with their query state");
        world.Remove<Signal>(entities[0]);
        Require(Count(disabled) == 43 && !world.Has<Signal>(entities[0]), "removal ends a disabled component's membership");
        world.Add(entities[0], new Signal { Value = 99 });
        for (int i = 0; i < 300; i++) world.Create(new Signal { Value = 1 });
        Require(world.IsEnabled<Signal>(entities[0]) && Count(active) == 386 && Count(disabled) == 43,
            "readded and newly allocated components start enabled after growth");
        world.Clear();
        Entity next = world.Create(new Signal { Value = 9 });
        Require(world.IsEnabled<Signal>(next) && Count(active) == 1 && Count(disabled) == 0,
            "clear reuses enabled storage and cached queries");
        Console.WriteLine("Enableable stored values, active queries and lifecycle behavior: passed.");
    }

    private static int Count(Query query)
    {
        int count = 0;
        using var scope = query.Open();
        foreach (var rows in scope) count += rows.Count;
        return count;
    }

    private static void Require(bool value, string behavior)
    {
        if (!value) throw new InvalidOperationException(behavior);
    }

    private struct Signal : IEnableableComponent { internal int Value; }
}
