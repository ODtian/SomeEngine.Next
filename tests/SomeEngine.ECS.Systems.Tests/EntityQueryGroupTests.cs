using System.Collections.Concurrent;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class EntityQueryGroupTests
{
    [Fact]
    public void DerivedRangesKeepTheirWritesAcrossRepeatedUseAndRefreshedMembers()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new PointValue(1));
        Entity second = world.CreateEntity(new PointValue(7));
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        var global = new PointGroups(writes, writes, [[first, second]], [[first, second]]);

        EntityQueryJobs.ScheduleGroup(world, in global, new ReuseThenRefresh(writes, first, second)).Complete();

        Assert.Equal(3, world.Read<PointValue>(first).Value);
        Assert.Equal(8, world.Read<PointValue>(second).Value);
        Assert.Equal(world.WriteVersion<PointValue>(first), world.WriteVersion<PointValue>(second));
        world.ReleaseQuery(writes);
    }

    [Fact]
    public void ParallelRangesJoinBeforeTheFollowingGroupReadsTheirWrites()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new PointValue(1));
        Entity second = world.CreateEntity(new PointValue(3));
        Entity anchor = world.CreateEntity(new PointValue(10));
        Entity total = world.CreateEntity(new PointValue(0));
        QueryHandle reads = world.Query(new QueryDefinitionBuilder().Read<PointValue>());
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        using var together = new Barrier(2);
        var work = new PointGroups(reads, writes, [[anchor, first], [anchor, second], [first, second]], [[first], [second], [total]]);

        EntityQueryJobs.ScheduleGroup(world, in work, new AddThenSum(together)).Complete();

        Assert.Equal(11, world.Read<PointValue>(first).Value);
        Assert.Equal(13, world.Read<PointValue>(second).Value);
        Assert.Equal(24, world.Read<PointValue>(total).Value);
        Assert.Equal(world.WriteVersion<PointValue>(first), world.WriteVersion<PointValue>(total));
        world.ReleaseQuery(writes);
        world.ReleaseQuery(reads);
    }

    [Fact]
    public void ChangedMembersBindTheirCurrentReferencesBeforeTheFirstWrite()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new ReferencedValues([3]));
        world.Add(first, new PointValue(0));
        Entity second = world.CreateEntity(new ReferencedValues([43]));
        world.Add(second, new PointValue(0));
        QueryHandle reads = world.Query(new QueryDefinitionBuilder().Read<ReferencedValues>());
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        QueryDefinition membersDefinition = new QueryDefinitionBuilder().Read<PointValue>().Build();
        QueryHandle members = world.Query(membersDefinition);
        var selected = new SelectedEntities([first]);
        var work = new SelectedWork(reads, writes, selected);
        var resources = new QueryReadResources();
        resources.Add<ReferencedValues, ReadValueResources>();
        var accesses = new JobResourceAccess[QueryJobAccess.Count(membersDefinition)];
        QueryJobAccess.CopyAccesses(world, membersDefinition, accesses);

        EntityQueryJobs.ScheduleGroup(world, in work,
            new RefreshMembersThenRead(world, scheduler, members, selected), accesses, resources).Complete();

        Assert.Equal(3, world.Read<PointValue>(first).Value);
        Assert.Equal(43, world.Read<PointValue>(second).Value);
        world.ReleaseQuery(members);
        world.ReleaseQuery(writes);
        world.ReleaseQuery(reads);
    }

    [Fact]
    public void TheSelectedRangesRemainHeldUntilEveryGroupPhaseFinishes()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity point = world.CreateEntity(new PointValue(7));
        QueryDefinition definition = new QueryDefinitionBuilder().ReadWrite<PointValue>().Build();
        QueryHandle writes = world.Query(definition);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        var work = new PointGroups(writes, writes, [[point]], [[point]]);
        JobHandle group = EntityQueryJobs.ScheduleGroup(world, in work, new HoldThenIncrement(entered, release, events));
        JobHandle other;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
            QueryJobAccess.CopyAccesses(world, definition, accesses);
            other = scheduler.Schedule(new AddOneHundred(world, writes, events), accesses);
        }
        finally { release.Set(); }
        group.Complete();
        other.Complete();

        Assert.Equal(["group-started", "group-complete", "other-written"], events.ToArray());
        Assert.Equal(109, world.Read<PointValue>(point).Value);
        world.ReleaseQuery(writes);
    }

    [Fact]
    public void GroupRangesFollowAnEntityMovedBeforeTheirFirstWrite()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity point = world.CreateEntity(new PointValue(7));
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        using var captured = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var work = new CapturedWork(writes, [point], captured, release);

        JobHandle group = EntityQueryJobs.ScheduleGroup(world, in work, new IncrementGroups());
        Assert.True(captured.Wait(TimeSpan.FromSeconds(10)));
        JobHandle moved = scheduler.Schedule(new AddChosen(world, point), [RelationshipJobAccess.TopologyWrite(world)]);
        release.Set();
        group.Complete();
        moved.Complete();

        Assert.True(world.Has<Chosen>(point));
        Assert.Equal(8, world.Read<PointValue>(point).Value);
        world.ReleaseQuery(writes);
    }

    [Fact]
    public void DerivedRangesUseTheInitializedValuesAndTheSameWriteVersion()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new PointValue(1));
        Entity second = world.CreateEntity(new PointValue(3));
        Entity anchor = world.CreateEntity(new PointValue(10));
        QueryHandle reads = world.Query(new QueryDefinitionBuilder().Read<PointValue>());
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        var global = new PointGroups(reads, writes, [[anchor]], [[first, second]]);
        using var together = new Barrier(2);

        EntityQueryJobs.ScheduleGroup(world, in global,
            new InitializeThenDerive(reads, writes, anchor, first, second, together)).Complete();

        Assert.Equal(21, world.Read<PointValue>(first).Value);
        Assert.Equal(23, world.Read<PointValue>(second).Value);
        Assert.Equal(world.WriteVersion<PointValue>(first), world.WriteVersion<PointValue>(second));
        world.ReleaseQuery(writes);
        world.ReleaseQuery(reads);
    }

    [Fact]
    public void SharedArrayWritesRunInOrderWhileAnIndependentRangeAdvances()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        int[] shared = [0], separate = [0];
        Entity first = world.CreateEntity(new ReferencedValues(shared));
        world.Add(first, new PointValue(0));
        Entity second = world.CreateEntity(new ReferencedValues(shared));
        world.Add(second, new PointValue(0));
        Entity third = world.CreateEntity(new ReferencedValues(separate));
        world.Add(third, new PointValue(0));
        QueryHandle reads = world.Query(new QueryDefinitionBuilder().Read<ReferencedValues>());
        QueryHandle writes = world.Query(new QueryDefinitionBuilder().ReadWrite<PointValue>());
        var resources = new QueryReadResources();
        resources.Add<ReferencedValues, WriteValueResources>();
        using var entered = new ManualResetEventSlim();
        using var independent = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<int>();
        var work = new PointGroups(reads, writes, [[first], [second], [third]], [[first], [second], [third]]);

        JobHandle complete = EntityQueryJobs.ScheduleGroup(world, in work,
            new IncrementArrayGroups(entered, independent, release, events), resources: resources);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(independent.Wait(TimeSpan.FromSeconds(10)));
        }
        finally { release.Set(); }
        complete.Complete();

        Assert.Equal([0, 1], events.ToArray());
        Assert.Equal(2, shared[0]);
        Assert.Equal(1, separate[0]);
        Assert.Equal(1, world.Read<PointValue>(first).Value);
        Assert.Equal(2, world.Read<PointValue>(second).Value);
        Assert.Equal(1, world.Read<PointValue>(third).Value);
        world.ReleaseQuery(writes);
        world.ReleaseQuery(reads);
    }

    private readonly struct WriteValueResources : IJobComponentResources<ReferencedValues>
    {
        public static void Collect(in ReferencedValues component, ref JobResourceAccessBuilder resources) => resources.Write(component.Values);
    }
    private readonly struct IncrementArrayGroups(ManualResetEventSlim entered, ManualResetEventSlim independent,
        ManualResetEventSlim release, ConcurrentQueue<int> events) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            group.ExecuteParallel(new IncrementArrays(entered, independent, release, events), 0, group.Count);
            return true;
        }
    }
    private readonly struct IncrementArrays(ManualResetEventSlim entered, ManualResetEventSlim independent,
        ManualResetEventSlim release, ConcurrentQueue<int> events) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            EntityQuery source = queries.Get(0), target = queries.Get(1);
            Entity entity = source.Entities[0];
            source.TryReadRef<ReferencedValues>(entity, out var values);
            int current = values.Value.Values[0];
            if (index == 0) { entered.Set(); release.Wait(); }
            if (index == 2) independent.Set();
            if (index < 2) Assert.Equal(index, current);
            values.Value.Values[0] = current + 1;
            target.TryGetRow(entity, out QueryRow row);
            row.ReadWrite<PointValue>() = new(current + 1);
            if (index < 2) events.Enqueue(index);
        }
    }

    private readonly struct InitializeThenDerive(QueryHandle reads, QueryHandle writes, Entity anchor, Entity first,
        Entity second, Barrier together) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            foreach (QueryRow row in group.Get(0).Get(1).Rows)
                row.ReadWrite<PointValue>() = new(row.Read<PointValue>().Value + 10);
            var derived = new PointGroups(reads, writes, [[], [anchor], [anchor]], [[], [first], [second]]);
            group.ExecuteParallel(in derived, new AddAnchor(together), 1, 2);
            return true;
        }
    }

    private readonly struct ReuseThenRefresh(QueryHandle writes, Entity first, Entity second) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            var work = new PointGroups(writes, writes, [[first]], [[first]]);
            using EntityQueryGroup selected = group.Borrow(in work);
            selected.ExecuteParallel(new IncrementPoints(), 0, 1);
            selected.ExecuteParallel(new IncrementPoints(), 0, 1);
            work = new PointGroups(writes, writes, [[second]], [[second]]);
            selected.Refresh(in work);
            selected.ExecuteParallel(new IncrementPoints(), 0, 1);
            return true;
        }
    }

    private readonly struct CapturedWork(QueryHandle query, Entity[] entities, ManualResetEventSlim captured,
        ManualResetEventSlim release) : IEntityQueryWork
    {
        public int Count => 1;
        public int QueryCount => 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => query;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex)
        {
            captured.Set();
            release.Wait();
            return entities;
        }
    }
    private readonly record struct AddChosen(World World, Entity Entity) : IJob
    {
        public void Execute() => World.AddTag<Chosen>(Entity);
    }
    private readonly struct Chosen : ITag;
    private readonly struct IncrementGroups : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            group.ExecuteParallel(new IncrementPoints(), 0, group.Count);
            return true;
        }
    }
    private readonly struct IncrementPoints : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            foreach (QueryRow row in queries.Get(0).Rows)
                row.ReadWrite<PointValue>() = new(row.Read<PointValue>().Value + 1);
        }
    }
    private readonly struct HoldThenIncrement(ManualResetEventSlim entered, ManualResetEventSlim release, ConcurrentQueue<string> events)
        : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            foreach (QueryRow row in group.Get(0).Get(0).Rows)
                row.ReadWrite<PointValue>() = new(row.Read<PointValue>().Value + 1);
            events.Enqueue("group-started");
            entered.Set();
            release.Wait();
            group.ExecuteParallel(new IncrementPoints(), 0, group.Count);
            events.Enqueue("group-complete");
            return true;
        }
    }
    private readonly record struct AddOneHundred(World World, QueryHandle Query, ConcurrentQueue<string> Events) : IJob
    {
        public void Execute()
        {
            World.ExecuteQuery(Query, static (scoped QueryCursor rows) =>
            {
                foreach (QueryRow row in rows.Rows) row.ReadWrite<PointValue>() = new(row.Read<PointValue>().Value + 100);
            });
            Events.Enqueue("other-written");
        }
    }

    private sealed class SelectedEntities(Entity[] entities)
    {
        internal Entity[] Entities = entities;
    }
    private readonly struct SelectedWork(QueryHandle reads, QueryHandle writes, SelectedEntities selected) : IEntityQueryWork
    {
        public int Count => 1;
        public int QueryCount => 2;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => queryIndex == 0 ? reads : writes;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => selected.Entities;
    }
    private readonly struct RefreshMembersThenRead(World world, Scheduler scheduler, QueryHandle members, SelectedEntities selected)
        : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            var current = new List<Entity>();
            world.ExecuteReadQuery(members, ref current, static (scoped QueryCursor rows, ref List<Entity> entities) =>
            {
                foreach (QueryRow row in rows.Rows) entities.Add(row.Entity);
            });
            if (!selected.Entities.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(current)))
            {
                selected.Entities = current.ToArray();
                return false;
            }
            new ReadValues(scheduler).Execute(0, group.Get(0));
            return true;
        }
    }
    private readonly record struct ReferencedValues(int[] Values) : IComponent;
    private readonly struct ReadValueResources : IJobComponentResources<ReferencedValues>
    {
        public static void Collect(in ReferencedValues component, ref JobResourceAccessBuilder resources) => resources.Read(component.Values);
    }
    private readonly struct ReadValues(Scheduler scheduler) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            EntityQuery source = queries.Get(0), target = queries.Get(1);
            foreach (Entity entity in source.Entities)
            {
                source.TryReadRef<ReferencedValues>(entity, out var values);
                scheduler.ExecuteParallel(new ReadArray(values.Value.Values), 1, 1,
                    [JobResourceAccess.Read(scheduler, values.Value.Values)]);
                target.TryGetRow(entity, out QueryRow row);
                row.ReadWrite<PointValue>() = new(values.Value.Values[0]);
            }
        }
    }
    private readonly record struct ReadArray(int[] Values) : IJobParallelFor
    {
        public void Execute(int index) => Assert.InRange(Values[index], 3, 43);
    }

    private readonly record struct PointValue(int Value) : IComponent;
    private readonly struct PointGroups(QueryHandle reads, QueryHandle writes, Entity[][] inputs, Entity[][] outputs) : IEntityQueryWork
    {
        public int Count => outputs.Length;
        public int QueryCount => 2;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => queryIndex == 0 ? reads : writes;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex) => queryIndex == 0 ? inputs[workIndex] : outputs[workIndex];
    }
    private readonly struct AddThenSum(Barrier together) : IEntityQueryGroupJob
    {
        public bool Execute(scoped EntityQueryGroup group)
        {
            group.ExecuteParallel(new AddAnchor(together), 0, 2);
            group.ExecuteParallel(new SumPoints(), 2, 1);
            return true;
        }
    }
    private readonly struct AddAnchor(Barrier together) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            Assert.True(together.SignalAndWait(TimeSpan.FromSeconds(10)));
            EntityQuery inputs = queries.Get(0);
            inputs.TryReadRef<PointValue>(inputs.Entities[0], out var anchor);
            foreach (QueryRow row in queries.Get(1).Rows)
                row.ReadWrite<PointValue>() = new(row.Read<PointValue>().Value + anchor.Value.Value);
        }
    }
    private readonly struct SumPoints : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            Assert.Equal(2, index);
            int sum = 0;
            foreach (QueryRow row in queries.Get(0).Rows) sum += row.Read<PointValue>().Value;
            foreach (QueryRow row in queries.Get(1).Rows) row.ReadWrite<PointValue>() = new(sum);
        }
    }
}
