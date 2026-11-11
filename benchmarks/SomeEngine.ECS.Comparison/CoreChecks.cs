using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class CoreChecks
{
    internal static void Run(int chunkCapacity = 0)
    {
        using var world = new World(4, chunkCapacity: chunkCapacity);
        Entity first = world.Create(new Position { X = 10 }, new Velocity { X = 2 }, new Health { Value = 90 });
        Entity second = world.Create(new Position { X = 20 }, new Velocity { X = 3 }, new Health { Value = 80 });
        Query query = world.Query(new QueryDescription().With<Position>().With<Velocity>());
        using (var cached = world.OpenRead<Position>())
            Require(cached.Read(first).X == 10, "cached access reads the original row");
        world.Add(first, new Extra { Value = 7 });
        world.Remove<Extra>(first);
        world.Set(second, new Position { X = 21 });
        Require(world.Read<Position>(first).X == 10 && world.Read<Velocity>(first).X == 2 &&
            world.Read<Health>(first).Value == 90 && !world.Has<Extra>(first), "move and fill hole preserve every column");

        Entity[] batch = new Entity[100];
        world.Create(batch, new Position { X = 30 }, new Velocity { X = 4 }, new Health { Value = 70 });
        Require(world.EntityCount == 102 && world.Read<Position>(batch[^1]).X == 30 &&
            world.Read<Velocity>(batch[1]).X == 4 && world.Read<Health>(batch[50]).Value == 70,
            "bulk creation initializes all output identities and columns");
        using (var cached = world.OpenRead<Position>())
            Require(cached.Read(first).X == 10 && cached.Read(second).X == 21 && cached.Read(batch[^1]).X == 30,
                "cached access follows migration, fill hole and growth");
        int seen = 0;
        float total = 0;
        using (var scope = query.Open())
            foreach (var chunk in scope)
            {
                seen += chunk.Count;
                foreach (Position position in chunk.Read<Position>()) total += position.X;
            }
        Require(seen == 102 && total == 3031, "query binds current grown storage");
        world.Destroy(first);
        Require(!world.IsAlive(first) && world.Read<Position>(second).X == 21, "destroy preserves moved identity");
        world.Destroy(batch);
        Entity replacement = world.Create(new Position { X = 40 });
        Require(world.EntityCount == 2 && world.IsAlive(replacement) && world.Read<Position>(replacement).X == 40,
            "recycled identities contain the new value");
        using (var cached = world.OpenCachedRead<Position>())
            Require(!cached.Has(batch[0]) && !cached.Has(batch[^1]) && cached.Read(second).X == 21
                && cached.Has(replacement) && cached.Read(replacement).X == 40,
                "batch reclamation clears old bindings and binds the recycled identity");

        world.Add(second, new Label { Value = "retained" });
        world.Remove<Position>(second);
        using (var labels = world.OpenRead<Label>())
            Require(labels.Read(second).Value == "retained" && world.Read<Health>(second).Value == 80,
                "managed values survive structural movement");
        world.Clear();
        Require(world.EntityCount == 0 && !world.IsAlive(second) && !world.IsAlive(replacement), "clear ends previous identities");
        Console.WriteLine("Core public behavior: passed.");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private struct Label { internal string Value; }
}
