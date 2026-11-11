using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class LifecycleChecks
{
    internal static void Run()
    {
        using var world = new World(4);
        using var first = world.WatchDestroyed();
        using var second = world.WatchDestroyed();
        Entity[] entities = new Entity[24];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Create(new Position { X = i });
        for (int i = 0; i < 12; i++) world.Destroy(entities[i]);
        first.Advance(Check(first, entities.AsSpan(0, 12), 1));
        for (int i = 12; i < entities.Length; i++) world.DestroyDeferred(entities[i]);
        ulong through = Check(first, entities.AsSpan(12), 13);
        second.Advance(Check(second, entities, 1));
        Check(first, entities.AsSpan(12), 13);
        first.Advance(through);
        Require(world.EntityCount == 0, "both destroy entries complete ordinary entity lifetimes");

        world.Clear();
        Entity retained = world.Create(new RemovedValue(42));
        world.Destroy(retained);
        world.Destroy(retained);
        Require(world.IsPendingCleanup(retained) && world.Read<Removed<RemovedValue>>(retained).Value.Value == 42,
            "destroy retains the old component value until cleanup");
        Check(first, new[] { retained }, through + 1);
        world.ClearRemoved<RemovedValue>();
        Require(!world.IsAlive(retained), "consuming retained values completes the entity lifetime");
        Check(second, new[] { retained }, through + 1);

        world.Clear();
        using var read = first.Open();
        Require(read.Count == 0 && read.Through == through + 1,
            "clear advances observer positions while preserving the sequence");
        Console.WriteLine("Destroy observers, independent progress, deferred facts and final cleanup: passed.");
    }

    private static ulong Check(DestroyedReader reader, ReadOnlySpan<Entity> expected, ulong sequence)
    {
        using var facts = reader.Open();
        Require(facts.Count == expected.Length, "each observer retains its unconsumed facts");
        int index = 0;
        foreach (DestroyedEntity fact in facts)
        {
            Require(fact.Entity == expected[index] && fact.Sequence == sequence + (ulong)index,
                "destroy facts preserve the full identity and operation order");
            index++;
        }
        return facts.Through;
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private readonly record struct RemovedValue(int Value) : IRemovedComponent;
}
