using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal static class SparseQueryChecks
{
    internal static void Run()
    {
        using var world = new World();
        var entities = new Entity[200];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Create(new Position { X = i });
            if (i % 2 == 0) world.Add(entities[i], new Team());
            if (i % 5 == 0) world.Set(entities[i], new Attachment { Value = i });
            if (i % 10 == 0) world.Set(entities[i], new OtherAttachment());
        }
        SparseQuery mixed = world.QuerySparse<Attachment>(new QueryDescription().With<Team>()).With<OtherAttachment>();
        Require(Count(mixed) == 20, "mixed queries intersect core presence with every sparse condition");
        ulong baseline = world.Version;
        world.Set(entities[0], new Attachment { Value = 42 });
        world.Remove<Attachment>(entities[10]);
        world.Destroy(entities[20]);
        Require(Count(mixed) == 18 && Count(world.QuerySparse<Attachment>().WithChanged<Attachment>(baseline)) == 1,
            "writes, sparse compaction and destroy preserve exact mixed membership and versions");
        Require(world.Read<Removed<Attachment>>(entities[10]).Value.Value == 10
            && world.IsPendingCleanup(entities[20]), "sparse old values join the ordinary retained facts");
        world.ClearRemoved<Attachment>();
        Require(!world.IsAlive(entities[20]), "sparse fact consumption releases the destroyed identity");
        world.Set(entities[30], new CleanupAttachment { Value = 3 });
        world.Destroy(entities[30]);
        world.ClearRemoved<Attachment>();
        Require(world.IsAlive(entities[30]) && world.Read<CleanupAttachment>(entities[30]).Value == 3,
            "cleanup sparse attachments hold the destroyed identity until explicit removal");
        world.Remove<CleanupAttachment>(entities[30]);
        Require(!world.IsAlive(entities[30]), "removing the final sparse cleanup completes destruction");
        world.Clear();
        Entity next = world.Create(new Position());
        world.Add(next, new Team());
        world.Set(next, new Attachment());
        world.Set(next, new OtherAttachment());
        Require(Count(mixed) == 1, "cached mixed queries bind the reused sparse stores after clear");
        Console.WriteLine("Sparse mixed queries, change versions and retained cleanup behavior: passed.");
    }

    private static int Count(SparseQuery query)
    {
        int count = 0;
        using var scope = query.Open();
        foreach (var entity in scope) count++;
        return count;
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private struct Attachment : IAddedComponent, IRemovedComponent { internal int Value; }
    private struct OtherAttachment { }
    private struct CleanupAttachment : ICleanupComponent { internal int Value; }
}
