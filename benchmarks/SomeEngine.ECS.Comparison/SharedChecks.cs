using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static partial class SharedChecks
{
    private partial struct ColoredPosition : IComponentBundle { internal Color Color; internal Position Position; }
    internal static void Run()
    {
        using var world = new World();
        SharedId<Color> blue = world.InternShared(new Color { Value = 7 });
        Require(world.InternShared(new Color { Value = 7 }) == blue && world.SharedValueCount<Color>() == 2,
            "equal shared values use one world lifetime identity");
        Query blueRows = world.Query(new QueryDescription().WithShared(blue));
        var entities = new Entity[201];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position { X = i });
            world.SetShared(entities[i], new Color { Value = i % 16 });
            world.SetShared(entities[i], new Season { Value = i % 2 });
        }
        Query combined = world.Query(new QueryDescription().With<Position>().WithShared(blue)
            .WithShared(world.InternShared(new Season { Value = 1 })));
        Require(Count(combined) == 13, "multiple shared ID masks agree with scalar membership");
        Require(Count(blueRows) == 13 && world.SharedValueCount<Color>() == 16,
            "cached value queries observe new single and multiple shared combinations");
        ulong beforeSameValue = world.Version;
        world.SetShared(entities[0], new Color { Value = 0 });
        Require(world.Query(new QueryDescription().WithChanged<Color>(beforeSameValue)).Count == 1,
            "setting an equal shared value records the component write");
        world.SetShared(entities[7], new Season { Value = 0 });
        world.Add(entities[7], new Extra { Value = 4 });
        var copy = world.Clone(entities[7]);
        Require(world.ReadShared<Color>(copy).Value == 7 && world.ReadShared<Season>(copy).Value == 0
            && world.Read<Position>(copy).X == 7, "ordinary migration and clone preserve all shared identities and main values");
        using (var colors = world.OpenRead<Color>())
            Require(colors.Read(copy).Value == 7 && colors.ReadKnown(entities[7]).Value == 7 && colors.Has(copy),
                "typed shared reads bind the shared values for their access scope");
        world.RemoveShared<Color>(entities[7]);
        Require(!world.Has<Color>(entities[7]) && Count(blueRows) == 13
            && world.Read<Removed<Color>>(entities[7]).Value.Value == 7,
            "shared removal updates presence and retains the old shared value");
        world.SetShared(entities[7], blue);
        world.Destroy(entities[3]);
        world.ClearRemoved<Color>();
        Require(Count(blueRows) == 14 && world.ReadShared<Season>(entities[7]).Value == 0,
            "readding by identity and fact cleanup retain the other shared dimensions");
        using (var scope = blueRows.Open())
            foreach (var rows in scope) Require(rows.Shared<Color>().Value == 7, "shared values bind once per returned range");
        world.Disable<Color>(copy);
        world.SetShared(copy, new Color { Value = 13 });
        Require(Count(blueRows) == 13 && world.ReadShared<Color>(copy).Value == 13,
            "disabled shared values retain their main value outside active membership");
        world.Enable<Color>(copy);
        using (var members = world.FindShared(world.InternShared(new Color { Value = 13 })))
            Require(members.Entities.Contains(copy), "enable publishes the current shared identity");
        world.SetShared(copy, blue);
        using (var scope = world.Query(new QueryDescription().With<Color>().With<Season>()).Open())
            foreach (var rows in scope)
            {
                var colors = rows.ReadShared<Color>();
                var positions = rows.Read<Position>();
                for (int row = 0; row < rows.Count; row++)
                    Require(colors[row].Value == (int)positions[row].X % 16, "shared row views follow ordinary column order");
            }
        world.Clear();
        var next = world.Spawn(new ColoredPosition { Color = new() { Value = 7 }, Position = new() { X = 42 } });
        Require(Count(blueRows) == 1 && world.ReadShared<Color>(next).Value == 7,
            "clear reuses shared identities and existing query bindings");
        Console.WriteLine("Shared interning, group queries, migration and old-value lifecycle behavior: passed.");
    }
    private static int Count(Query query)
    {
        int count = 0;
        using var scope = query.Open();
        foreach (var rows in scope) count += rows.Count;
        Require(count == query.Count, "shared member counts agree with filtered row ranges");
        return count;
    }
    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
    private struct Color : ISharedComponent, IAddedComponent, IRemovedComponent, IEnableableComponent { internal int Value; }
    private struct Season : ISharedComponent { internal int Value; }
}
