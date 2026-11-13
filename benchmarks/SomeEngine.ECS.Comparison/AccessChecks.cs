using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class AccessChecks
{
    internal static void Run()
    {
        using var world = new World(2);
        Entity first = world.Create(new Position { X = 1 });
        Entity second = world.Create(new Position { X = 2 });
        using (var values = world.OpenWrite<Position>()) values.Get(first).X = 11;
        using (var values = world.OpenWrite<Position>()) values.Set(second, new Position { X = 22 });
        world.Add(first, new Extra { Value = 3 });
        using (var values = world.OpenRead<Position>())
            Require(values.Has(first) && values.Read(first).X == 11 && values.Read(second).X == 22,
                "both write scopes update the component subsequently read through the location cache");

        world.Set(first, new Extra { Value = 10 });
        world.Set(first, new Label { Value = "first" });
        world.Set(second, new Extra { Value = 20 });
        world.Remove<Extra>(first);
        using (var values = world.OpenSparseValues<Extra>())
            Require(values.Count == 1 && values.Entities[0] == second && values.Values[0].Value == 20,
                "sparse removal compacts entity and value arrays together");
        using (var values = world.OpenWrite<Extra>()) values.Get(second).Value = 21;
        world.Destroy(first);
        Entity replacement = world.Create(new Position { X = 33 });
        Require(!world.Has<Label>(replacement) && world.Read<Extra>(second).Value == 21,
            "destroy releases every attached type while preserving another entity's sparse value");
        using (var values = world.OpenRead<Position>())
            Require(!values.Has(first) && values.Has(replacement) && values.Read(replacement).X == 33
                && values.Read(second).X == 22,
                "location bindings follow the replacement identity and retain another entity's value");
        for (int i = 0; i < 4200; i++)
        {
            Entity entity = world.Create();
            world.Set(entity, new Extra { Value = i });
        }
        using (var values = world.OpenSparseValues<Extra>())
            Require(values.Count == 4201 && values.Values[^1].Value == 4199,
                "sparse pages and dense arrays grow with their attached entities");
        world.Clear();
        Entity afterClear = world.Create(new Position { X = 44 });
        world.Set(afterClear, new Extra { Value = 44 });
        using (var values = world.OpenRead<Position>())
            Require(!values.Has(replacement) && !values.Has(second) && values.Has(afterClear)
                && values.ReadKnown(afterClear).X == 44,
                "clear binds the new identity and position through the existing location directory");
        using (var values = world.OpenSparseValues<Extra>())
            Require(values.Count == 1 && values.Entities[0] == afterClear && values.Values[0].Value == 44,
                "clear reuses sparse storage with only the new members");
        Console.WriteLine("Bound write and sparse lifecycle behavior: passed.");
    }

    private static void Require(bool value, string behavior)
    {
        if (!value) throw new InvalidOperationException(behavior);
    }

    private struct Label { internal string Value; }
}
