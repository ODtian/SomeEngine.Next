using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class PresenceChecks
{
    internal static void Run()
    {
        using var world = new World();
        var entities = new Entity[130];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Spawn(new Body { Position = new() { X = i }, Velocity = new() { X = 1 }, Health = new() { Value = 100 } });
        Query present = world.Query(new QueryDescription().With<Position>().With<Velocity>());
        Query absent = world.Query(new QueryDescription().With<Position>().Without<Velocity>());
        Require(Count(present) == 130 && Count(absent) == 0, "queries observe the initial members");
        using (var cached = world.OpenRead<Velocity>()) Require(cached.Has(entities[0]), "cache binds before removal");
        for (int i = 0; i < entities.Length; i += 2) world.Remove<Velocity>(entities[i]);
        Require(Count(present) == 65 && Count(absent) == 65, "cached queries observe presence changes across words");
        world.Add(entities[0], new Extra { Value = 7 });
        world.Destroy(entities[1]);
        Require(!world.Has<Velocity>(entities[0]) && Count(present) == 64 && Count(absent) == 65,
            "migration and fill hole carry actual component presence");
        using (var cached = world.OpenRead<Velocity>())
            Require(!cached.Has(entities[0]) && cached.Has(entities[3]), "cache follows removal and moved members");
        world.Set(entities[0], new Velocity { X = 9 });
        for (int i = 2; i < entities.Length; i += 2) world.Add(entities[i], new Velocity { X = 2 });
        Require(Count(present) == 129 && Count(absent) == 0 && world.Read<Velocity>(entities[0]).X == 9,
            "readding a retained column restores its query membership and value");
        for (int i = 0; i < 300; i++) world.Spawn(new Body { Velocity = new() { X = 3 } });
        Require(Count(present) == 429, "new rows initialize presence after retained storage grows");
        Console.WriteLine("Retained columns, query membership and location cache behavior: passed.");
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
}
