using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class IndexChecks
{
    internal static void Run()
    {
        using var world = new World();
        var entities = new Entity[130];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Create(new IndexedValue { Key = i % 16, Value = i });
        IndexedValue.Projections = 0;
        ValueIndex<IndexedValue, int> index = world.Index<IndexedValue, int>();
        Require(IndexedValue.Projections == 130 && index.KeyCount == 16, "activation projects each active main value once");
        IndexedValue.Projections = 0;
        world.Set(entities[0], new IndexedValue { Key = 0, Value = 42 });
        Require(IndexedValue.Projections == 1 && Count(index, 0) == 9, "same-key writes use one projection and preserve membership");
        world.Set(entities[0], new IndexedValue { Key = 17 });
        using (var groups = index.Groups())
        {
            int previous = -1, count = 0;
            foreach (var group in groups) { Require(group.Key > previous, "groups follow key order"); previous = group.Key; count++; }
            Require(count == 17, "ordered groups include the new key");
        }
        world.Set(entities[0], new IndexedValue { Key = 18 });
        using (var groups = index.Range(17, 18))
        {
            int count = 0;
            foreach (var group in groups) { Require(group.Key == 18 && group.Entities.Length == 1, "singleton rekey updates ordered membership"); count++; }
            Require(count == 1, "range selects the current key only");
        }
        IndexedValue.Projections = 0;
        world.Disable<IndexedValue>(entities[1]);
        world.Set(entities[1], new IndexedValue { Key = 19 });
        Require(IndexedValue.Projections == 0 && Count(index, 1) == 8 && Count(index, 19) == 0,
            "disabled values stay outside the index while their main value changes");
        world.Enable<IndexedValue>(entities[1]);
        Require(IndexedValue.Projections == 1 && Count(index, 19) == 1, "enable indexes the current value with one projection");
        world.Remove<IndexedValue>(entities[2]);
        world.Destroy(entities[3]);
        IndexedValue.Projections = 0;
        var copy = world.Clone(entities[4]);
        Require(Count(index, 2) == 7 && Count(index, 3) == 7 && Count(index, 4) == 9 && IndexedValue.Projections == 1,
            "remove, destroy and clone keep compact members aligned with the live main values");
        world.Clear();
        var next = world.Create(new IndexedValue { Key = 5 });
        using (var members = index.Find(5))
            Require(index.KeyCount == 1 && members.Count == 1 && members.Entities[0] == next,
                "clear reuses the index and tracks newly created identities");
        Console.WriteLine("Value index projection counts, compact groups, ordering and lifecycle behavior: passed.");
    }

    private static int Count(ValueIndex<IndexedValue, int> index, int key)
    {
        using var members = index.Find(key);
        return members.Count;
    }
    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }
    private struct IndexedValue : IIndexedComponent<int>, IEnableableComponent, ITrackedComponent
    {
        internal static int Projections;
        internal int Key, Value;
        public readonly int GetKey() { Projections++; return Key; }
    }
}
