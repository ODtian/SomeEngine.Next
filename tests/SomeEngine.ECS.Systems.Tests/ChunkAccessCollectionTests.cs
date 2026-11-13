using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class ChunkAccessCollectionTests : JobTest
{
    [Fact]
    public void CollectedChunkWritesBorrowTheirQueriesAndRunDisjointValuesTogether()
    {
        World world = CreateJobWorld();
        var first = world.Create(new Value { Number = 3 }, new First());
        var second = world.Create(new Value { Number = 7 }, new Second());
        Query a = world.Query(new QueryDescription().With<Value>().With<First>());
        Query b = world.Query(new QueryDescription().With<Value>().With<Second>());
        using var meeting = new Barrier(2);
        var jobs = (WorldJobs)world.Execution!;
        JobHandle left = Jobs.Schedule(new Increment(world, a, meeting), jobs.CollectAccess<IncrementValues>(a));
        JobHandle right = Jobs.Schedule(new Increment(world, b, meeting), jobs.CollectAccess<IncrementValues>(b));
        Jobs.CombineDependencies([left, right]).Complete();
        Assert.Equal(4, world.ReadComponentData<Value>(first).Number);
        Assert.Equal(8, world.ReadComponentData<Value>(second).Number);
    }

    [Fact]
    public void DescriptorCollectionReadsOnlyItsSourceBeforeTheChildWritesState()
    {
        World world = CreateJobWorld();
        var entity = world.Create(new Source([19]), new Value());
        Query query = world.Query<Source, Value>();
        Jobs.Schedule(new Capture(world, query), ((WorldJobs)world.Execution!).StructureRead).Complete();
        Assert.Equal(19, world.ReadComponentData<Value>(entity).Number);
    }

    private struct Value : IComponent { public int Number; }
    private readonly struct First : ITag;
    private readonly struct Second : ITag;
    private readonly record struct Source(int[] Values) : IComponent;
    private readonly struct SourceResources : IJobComponentResources<Source>
    {
        public static void Collect(in Source source, ref JobResourceAccessBuilder resources) => resources.Read(source.Values);
    }
    private readonly struct Increment(World world, Query query, Barrier meeting) : IJob
    {
        public void Execute()
        {
            using QueryScope scope = query.OpenRead();
            foreach (QueryChunk chunk in scope) _ = chunk.Read<Value>()[0].Number;
            if (!meeting.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            new SystemRunner(world).ExecuteParallel(query, new IncrementValues());
        }
    }
    private readonly struct IncrementValues : IChunkAction
    {
        private static readonly ChunkAccess[] Accesses = [ChunkAccess.Write<Value>()];
        public static ReadOnlySpan<ChunkAccess> Components => Accesses;
        public void Execute(SystemChunk rows) { foreach (ref Value value in rows.Write<Value>()) value.Number++; }
    }
    private readonly struct SourceRead : IChunkAction
    {
        private static readonly ChunkAccess[] Accesses = [ChunkAccess.Read<Source>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Accesses;
        public void Execute(SystemChunk rows) { }
    }
    private readonly struct ReadValues : IChunkAction
    {
        private static readonly ChunkAccess[] Accesses = [ChunkAccess.Read<Source>(hasResources: true), ChunkAccess.Write<Value>()];
        public static ReadOnlySpan<ChunkAccess> Components => Accesses;
        public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources) =>
            ComponentResources.CollectRange<Source, SourceResources>(rows.Read<Source>(), ref resources);
        public void Execute(SystemChunk rows)
        {
            var sources = rows.Read<Source>(); var values = rows.Write<Value>();
            for (int i = 0; i < values.Length; i++) values[i].Number = sources[i].Values[0];
        }
    }
    private readonly struct Capture(World world, Query query) : IJob
    {
        public void Execute()
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.Scheduler.Schedule(new Collect(world, query), jobs.CollectAccess<SourceRead>(query));
        }
    }
    private readonly struct Collect(World world, Query query) : IJob
    {
        public void Execute()
        {
            var jobs = (WorldJobs)world.Execution!;
            JobResourceAccess[] reads = jobs.CollectReadAccess<Source, SourceResources>(query);
            jobs.Scheduler.Schedule(new Read(world, query), [.. reads, .. jobs.CollectAccess<ReadValues>(query)]);
        }
    }
    private readonly struct Read(World world, Query query) : IJob
    {
        public void Execute() => new SystemRunner(world).ExecuteParallel(query, new ReadValues());
    }
}
