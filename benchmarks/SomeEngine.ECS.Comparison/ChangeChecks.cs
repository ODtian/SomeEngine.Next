using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class ChangeChecks
{
    internal static void Run()
    {
        BatchStampMatchesScalar();
        BatchRemovalRetainsOrderedOldValues();
        using (var batch = new World())
        {
            Entity[] members = [batch.Create(new Score()), batch.Create(new Extra()), batch.Create(new Extra())];
            ulong before = batch.Version;
            batch.Set<Score>(members, new Score { Value = 9 });
            Require(batch.Version == before + 1
                && Count(batch.Query(new QueryDescription().WithChanged<Score>(before))) == 3
                && Count(batch.Query(new QueryDescription().WithAdded<Score>(before))) == 2,
                "one batch version covers replacements and newly added values");
        }
        using var world = new World();
        var entities = new Entity[4_200];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Create(new Score { Value = i });
        ulong baseline = world.Version;
        Query changed = world.Query(new QueryDescription().WithChanged<Score>(baseline));
        Query added = world.Query(new QueryDescription().WithAdded<Score>(baseline));
        using (var values = world.OpenWrite<Score>())
            for (int i = 0; i < entities.Length; i += 100) values.Set(entities[i], new Score { Value = 42 });
        ulong written = world.Version;
        world.Add(entities[0], new Extra { Value = 3 });
        world.Destroy(entities[1]);
        Require(Count(changed) == 42 && Count(added) == 0 && world.Version > written,
            "writes select exact rows across blocks and preserve versions during migration and fill holes");
        ulong beforeRead = world.Version;
        _ = world.Read<Score>(entities[0]).Value;
        using (var values = world.OpenRead<Score>()) _ = values.Read(entities[0]).Value;
        Require(world.Version == beforeRead, "read views preserve the observation baseline");
        world.Remove<Score>(entities[0]);
        Require(!world.Has<Score>(entities[0]) && world.Read<Removed<Score>>(entities[0]).Value.Value == 42,
            "removal retains the old value on the original identity");
        world.Add(entities[0], new Score { Value = 99 });
        Require(Count(added) == 1, "readding starts a new component lifetime");
        world.Remove<Score>(entities[0]);
        Require(world.Read<Removed<Score>>(entities[0]).Value.Value == 99,
            "a second removal replaces the existing old value");
        world.ClearRemoved<Score>();
        Require(!world.Has<Removed<Score>>(entities[0]) && !world.IsAlive(entities[1]),
            "fact cleanup releases destroyed identities and keeps the active entity");
        Entity retained = world.Create(new Score { Value = 7 });
        world.Destroy(retained);
        Require(world.IsAlive(retained) && world.IsPendingCleanup(retained)
            && world.Read<Removed<Score>>(retained).Value.Value == 7,
            "destroyed entities remain readable until their old values are consumed");
        world.ClearRemoved<Score>();
        Require(!world.IsAlive(retained), "consuming the final retained fact completes identity release");
        using (var values = world.OpenCachedWrite<Score>()) values.Set(entities[100], new Score { Value = 8 });
        changed.Since(beforeRead);
        Require(Count(changed) == 1, "cached writes publish their version to ordinary changed queries");
        Console.WriteLine("Added, Changed, Removed and cleanup lifecycle behavior: passed.");
    }

    // Check-only reflection preserves the production assembly's internal API.
    // The existing per-row implementation is the oracle, including non-monotone versions.
    private static void BatchStampMatchesScalar()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Type type = typeof(World).Assembly.GetType("SomeEngine.ECS.Storage.ComponentChanges", throwOnError: true)!;
        var constructor = type.GetConstructor(flags, null, [typeof(int), typeof(bool)], null)!;
        var stamp = type.GetMethod("Stamp", flags)!;
        var word = type.GetMethod("StampWord", flags)!;
        var range = type.GetMethod("StampRange", flags)!;
        var merge = type.GetMethod("MergeWords", flags)!;
        var select = type.GetMethod("Select", flags)!;
        var contains = type.GetMethod("Contains", flags)!;
        var rows = type.GetField("Rows", flags)!;
        var words = type.GetField("Words", flags)!;
        var blocks = type.GetField("Blocks", flags)!;
        var lastWrite = type.GetField("LastWrite", flags)!;
        const int capacity = 4_163;
        int[] selectedRows = [0, 1, 7, 63, 64, 65, 127, 128, 4_090, 4_095, 4_096, 4_109, 4_160, 4_162];
        ulong[] thresholds = [0, 3, 5, 9, ulong.MaxValue];
        (int Start, int Count, ulong Version)[] operations =
        [
            (0, 0, 4), (0, 1, 4), (63, 2, 4), (64, 64, 4),
            (7, 121, 3), (4_090, 20, 9), (0, capacity, ulong.MaxValue),
            (4_160, 3, 5), (1, 65, 5), (0, 0, 0)
        ];
        foreach (bool added in new[] { false, true })
        foreach (bool publish in new[] { false, true })
        {
            object expected = constructor.Invoke([capacity, added]);
            object actual = constructor.Invoke([capacity, added]);
            for (int row = 0; row < capacity; row++)
            {
                object[] seed = [row, (ulong)(1 + row % 3), true, added];
                stamp.Invoke(expected, seed); stamp.Invoke(actual, seed);
            }
            foreach (var operation in operations)
            {
                for (int row = operation.Start; row < operation.Start + operation.Count; row++)
                {
                    if (publish) stamp.Invoke(expected, [row, operation.Version, false, added]);
                    else word.Invoke(expected, [row, operation.Version, added]);
                }
                range.Invoke(actual, [operation.Start, operation.Count, operation.Version, added, publish]);
                Equal();
                if (!publish && operation.Count != 0)
                {
                    object[] arguments = [operation.Start >> 6, (operation.Start + operation.Count + 63) >> 6, operation.Version];
                    merge.Invoke(expected, arguments); merge.Invoke(actual, arguments);
                    Equal();
                }
            }
            void Equal()
            {
                foreach (var field in new[] { rows, words, blocks })
                {
                    var left = (Array)field.GetValue(expected)!;
                    var right = (Array)field.GetValue(actual)!;
                    for (int i = 0; i < left.Length; i++)
                        Require(Equals(left.GetValue(i), right.GetValue(i)), "batched marks match scalar rows and conservative summaries");
                }
                Require(Equals(lastWrite.GetValue(expected), lastWrite.GetValue(actual)), "batched marks retain serial/parallel publication ownership");
                foreach (int row in selectedRows)
                foreach (ulong since in thresholds)
                foreach (bool selectAdded in new[] { false, true })
                {
                    ulong mask = (ulong)select.Invoke(expected, [row >> 6, capacity, since, selectAdded, added])!;
                    bool member = (bool)contains.Invoke(actual, [row, since, selectAdded, added])!;
                    Require(member == ((mask & (1UL << (row & 63))) != 0),
                        "single-row Contains is exactly Select's live bit before and after summary publication");
                }
            }
        }
        Console.WriteLine("Batched marks and sparse single-row Contains: scalar-equivalent rows, Added preservation, partial/full words, block crossing, repeated/older versions and join publication passed.");
    }

    private static void BatchRemovalRetainsOrderedOldValues()
    {
        using var world = new World();
        Entity[] members = [world.Create(new Score { Value = 1 }),
            world.Create(new Score { Value = 2 }, new Extra()), world.Create(new Score { Value = 3 })];
        world.Remove<Score>(members);
        var first = world.Read<Removed<Score>>(members[0]);
        var second = world.Read<Removed<Score>>(members[1]);
        var third = world.Read<Removed<Score>>(members[2]);
        Require(first.Value.Value == 1 && second.Value.Value == 2 && third.Value.Value == 3
            && first.Version < second.Version && second.Version < third.Version
            && Count(world.Query<Score>()) == 0 && Count(world.Query<Removed<Score>>()) == 3,
            "batch removal preserves ordered old-value lifetimes across layouts");
    }

    private static int Count(Query query)
    {
        int count = 0;
        using var scope = query.Open();
        foreach (var rows in scope) count += rows.Count;
        return count;
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private struct Score : IAddedComponent, IRemovedComponent { internal int Value; }
}
