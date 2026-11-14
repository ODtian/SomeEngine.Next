using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static partial class SparseQueryChecks
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
        Query mixed = world.Query(new QueryDescription().With<Team>().With<Attachment>().With<OtherAttachment>());
        Require(Count(mixed) == 20, "mixed queries intersect core presence with every sparse condition");
        ulong baseline = world.Version;
        world.Set(entities[0], new Attachment { Value = 42 });
        world.Remove<Attachment>(entities[10]);
        world.Destroy(entities[20]);
        Require(Count(mixed) == 18 && Count(world.Query(new QueryDescription().WithChanged<Attachment>(baseline))) == 1,
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
        CheckConnectedSparseConditions();
        Console.WriteLine("Sparse mixed queries, change versions and retained cleanup behavior: passed.");
    }

    // Counts through the scope, which may walk a sparse store, and checks the row-filter count agrees.
    // Jobs build their ranges through the same row filter, and a connected scope declares the sparse reads.
    private static void CheckConnectedSparseConditions()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        using var world = new World(execution: new WorldJobs(scheduler));
        int expected = 0, total = 0;
        for (int i = 0; i < 4096; i++)
        {
            Entity entity = world.Create(new Position { X = i });
            if (i % 3 == 0) { world.Set(entity, new Attachment { Value = i }); expected++; total += i; }
        }
        Query attached = world.Query(new QueryDescription().With<Position>().With<Attachment>());
        var rows = new JobResult<int>();
        new CountRows(rows).Schedule(world, query: attached).Complete();
        int values = 0;
        using (QueryScope scope = attached.OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++) values += chunk.Read<Attachment>(row).Value;
        Require(rows.Value == expected && values == total && Count(attached) == expected,
            "jobs and connected scopes select and read only rows with the required sparse value");
    }

    private readonly partial struct CountRows(JobResult<int> rows) : IJobChunk
    {
        public void Execute(ReadOnlySpan<Position> positions) => rows.Value += positions.Length;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(rows);
    }

    private static int Count(Query query)
    {
        int count = 0;
        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope) count += chunk.Count;
        Require(count == query.Count, "sparse store and row filter iteration select the same rows");
        return count;
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private struct Attachment : ISparseComponent, IAddedComponent, IRemovedComponent { internal int Value; }
    private struct OtherAttachment : ISparseComponent { }
    private struct CleanupAttachment : ISparseComponent, ICleanupComponent { internal int Value; }
}
