namespace SomeEngine.ECS.Comparison;

internal static class QueryBindingChecks
{
    internal static void Run()
    {
        using var world = new World(4, chunkCapacity: 16);
        var description = new QueryDescription().With<Position>();
        Query positions = world.Query(description);
        world.Create(new Position { X = 1 });
        Require(positions.Count == 1 && world.Query<Position>().Count == 1,
            "dynamic and typed bindings see the same current rows");
        description.Without<Extra>();
        Query filtered = world.Query(description);
        world.Spawn(new PositionExtra { Position = new() { X = 2 } });
        Require(positions.Count == 2 && filtered.Count == 1,
            "changing a builder preserves earlier query predicates");

        var first = world.Create(new Score(1));
        world.Create(new Score(2));
        Query allChanges = world.Query(new QueryDescription().WithChanged<Score>(0));
        Query recentChanges = world.Query(new QueryDescription().WithChanged<Score>(world.Version));
        Require(allChanges.Count == 2 && recentChanges.Count == 0,
            "shared matches retain independent observation versions");
        world.Set(first, new Score(42));
        Query copy = recentChanges;
        copy.Since(world.Version);
        Require(allChanges.Count == 2 && recentChanges.Count == 1 && copy.Count == 0,
            "copying and advancing a query changes its own observation version");
        allChanges.Since(world.Version);
        Require(allChanges.Count == 0 && recentChanges.Count == 1,
            "Since updates the receiving query value");

        var reversed = new QueryDescription().With<Extra>().With<Position>();
        Require(world.Query(reversed).Count == 1 && world.Query<Position, Extra>().Count == 1,
            "predicate order preserves membership");
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        for (int i = 0; i < 64; i++)
        {
            count += world.Query(reversed).Count;
            count += world.Query<Position, Extra>().Count;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(count == 128 && allocated == 0, "repeated dynamic and typed bindings reuse their storage");
        world.Clear();
        world.Create(new Position { X = 3 });
        Require(positions.Count == 1 && filtered.Count == 1 && recentChanges.Count == 0,
            "clear preserves reusable bindings and clears prior row versions");
        Console.WriteLine("Shared query matches, independent versions, builder changes and allocation-free binding: passed.");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private readonly record struct Score(int Value) : IAddedComponent;
}
