namespace SomeEngine.ECS.Comparison;

internal static class QueryConditionChecks
{
    internal static void Run()
    {
        using var world = new World(16, chunkCapacity: 32);
        var entities = new SomeEngine.ECS.Entities.Entity[320];
        var query = world.Query(new QueryDescription().With<Position>().WithAny<First>().WithAny<Second>()
            .Without<Suppressed>().Optional<Label>());
        for (int i = 0; i < entities.Length; i++)
        {
            var entity = entities[i] = world.Create(new Position { X = i });
            if (i % 2 == 0) world.Set(entity, new First { Value = i * 2 });
            if (i % 3 == 0) world.Set(entity, new Second { Value = i * 3 });
            if (i % 5 == 0) world.Set(entity, new Label { Value = "row" + i });
            if (i % 7 == 0) world.Set(entity, new Suppressed());
        }
        int initial = Enumerable.Range(0, entities.Length).Count(i => (i % 2 == 0 || i % 3 == 0) && i % 7 != 0);
        Require(query.Count == initial && query.Any(), "Any selects the union across matching component layouts");
        for (int i = 0; i < entities.Length; i++)
        {
            if (i % 4 == 0) world.Disable<First>(entities[i]);
            if (i % 9 == 0) world.Remove<Second>(entities[i]);
            if (i % 14 == 0) world.Remove<Suppressed>(entities[i]);
        }
        var seen = new bool[entities.Length];
        using (var scope = query.Open())
            foreach (var rows in scope)
                for (int row = 0; row < rows.Count; row++)
                {
                    int ordinal = (int)rows.Read<Position>()[row].X;
                    Require(!seen[ordinal], "each selected entity appears once");
                    seen[ordinal] = true;
                    var first = rows.Optional<First>(row);
                    var second = rows.Optional<Second>(row);
                    var label = rows.Optional<Label>(row);
                    Require(first.IsPresent == (ordinal % 2 == 0) && first.IsEnabled == (ordinal % 2 == 0 && ordinal % 4 != 0)
                        && (!first.IsPresent || first.Value.Value == ordinal * 2), "optional values distinguish stored and enabled state");
                    Require(second.IsPresent == (ordinal % 3 == 0 && ordinal % 9 != 0)
                        && second.IsEnabled == second.IsPresent && (!second.IsPresent || second.Value.Value == ordinal * 3),
                        "optional values reflect component removal");
                    Require(label.IsPresent == (ordinal % 5 == 0)
                        && (!label.IsPresent || label.Value.Value == "row" + ordinal), "optional managed values follow the current row");
                }
        int expected = 0;
        for (int i = 0; i < entities.Length; i++)
        {
            bool selected = (i % 2 == 0 && i % 4 != 0 || i % 3 == 0 && i % 9 != 0) && (i % 7 != 0 || i % 14 == 0);
            Require(seen[i] == selected, "the union and exclusions match their exact current members");
            if (selected) expected++;
        }
        Require(query.Count == expected && query.Any(), "Count and Any use the same filtered membership as enumeration");
        world.Clear();
        Require(query.Count == 0 && !query.Any(), "clear updates existing query aggregates");
        world.Create(new Position(), new Second { Value = 9 });
        Require(query.Count == 1 && query.Any(), "query aggregates observe subsequent creation");
        Console.WriteLine("Any predicates, optional values, enabled state, Count and Any: passed.");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
    private struct First : IEnableableComponent { internal int Value; }
    private struct Second : IComponent { internal int Value; }
    private struct Label : IComponent { internal string Value; }
    private struct Suppressed : ITag;
}
