using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using System.Reflection;

namespace SomeEngine.ECS.Comparison;

internal static class SystemChecks
{
    private struct SystemValue(int value) : IAddedComponent { public int Value = value; public int Ordinal = value; }
    private struct SystemStep(int value) : IEnableableComponent { public int Value = value; }
    private struct RandomValue(int value) : IAddedComponent { public int Value = value; }
    private readonly struct Advance : IComponentAction<SystemValue, SystemStep>
    {
        public void Execute(Span<SystemValue> values, ReadOnlySpan<SystemStep> steps)
        {
            for (int i = 0; i < values.Length; i++) values[i].Value += steps[i].Value;
        }
    }

    internal static void Run()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        CheckCrossThreadPools(scheduler);
        Check(scheduler, 10_000, 0);
        Check(scheduler, 1_000, 128);
        CheckConnected(scheduler);
        CheckQueryReadRejectsStructureChanges(scheduler);
        CheckQueryWriteKeepsReadGuard(scheduler);
        CheckScheduledOptionalRead(scheduler);
        CheckOptionalReadFollowsWriter(scheduler);
        CheckParallelRangeWrites(scheduler);
        CheckParallelRangeRejectsOtherRows(scheduler);
        CheckParallelQueryViewRejectsOtherRows(scheduler);
        Console.WriteLine("System ranges: connected synchronous/asynchronous access, random read/write policy, dependencies, exact changes, one batch version and multiple chunks passed.");
    }


    private sealed class PoolProbeState(object poolLock)
    {
        internal int Releases, ReleaseThread;
        internal bool ReleasedUnderPoolLock;
        internal void Release()
        {
            Releases++;
            ReleaseThread = Environment.CurrentManagedThreadId;
            ReleasedUnderPoolLock = Monitor.IsEntered(poolLock);
        }
    }

    private struct PoolProbeJob(PoolProbeState state) : IStagedJob
    {
        internal PoolProbeState? State = state;
        public static int StageCount => 0;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses) { }
        public void Execute() { }
        public void Release() => State?.Release();
    }

    private static void CheckCrossThreadPools(Scheduler scheduler)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const int count = 8; // Exceeds the original four-slot initial capacity; this is not a cache cap.
        int submittingThread = Environment.CurrentManagedThreadId;
        static void OnOtherThread(Action action)
        {
            Exception? failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } })
                { IsBackground = true, Name = "ECS cross-thread pool check" };
            thread.Start();
            if (!thread.Join(5_000)) throw new InvalidOperationException("Cross-thread pool return did not finish.");
            if (failure is not null) throw new InvalidOperationException("Cross-thread pool return failed.", failure);
        }

        Type stagedType = typeof(Scheduler).GetNestedType("StagedJobData`1", BindingFlags.NonPublic)!
            .MakeGenericType(typeof(PoolProbeJob));
        MethodInfo rentStaged = stagedType.GetMethod("Rent", statics)!;
        MethodInfo releaseStaged = stagedType.GetMethod("ReleaseReference", instance)!;
        FieldInfo jobField = stagedType.GetField("_job", instance)!;
        FieldInfo schedulerField = stagedType.GetField("_scheduler", instance)!;
        FieldInfo handleField = stagedType.GetField("Handle", instance)!;
        FieldInfo refsField = stagedType.GetField("_references", instance)!;
        object stagedLock = stagedType.GetField("FreeLock", statics)!.GetValue(null)!;
        var states = new PoolProbeState[count];
        var staged = new object[count];
        for (int i = 0; i < count; i++)
        {
            states[i] = new(stagedLock);
            staged[i] = rentStaged.Invoke(null, [scheduler, default(JobHandle), new PoolProbeJob(states[i])])!;
        }
        Require(staged.Distinct(ReferenceEqualityComparer.Instance).Count() == count, "simultaneously rented staged objects are distinct");
        OnOtherThread(() => { foreach (object item in staged) releaseStaged.Invoke(item, null); });
        for (int i = 0; i < count; i++)
            Require(states[i].Releases == 1 && states[i].ReleaseThread != submittingThread
                && !states[i].ReleasedUnderPoolLock && schedulerField.GetValue(staged[i]) is null
                && ((PoolProbeJob)jobField.GetValue(staged[i])!).State is null
                && ((JobHandle)handleField.GetValue(staged[i])!).Equals(default(JobHandle))
                && (int)refsField.GetValue(staged[i])! == 0,
                "last-reference return clears staged payload and releases once outside the pool lock on the other thread");
        var stagedSet = new HashSet<object>(staged, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < count; i++)
        {
            staged[i] = rentStaged.Invoke(null, [scheduler, default(JobHandle), new PoolProbeJob(new(stagedLock))])!;
            Require(stagedSet.Remove(staged[i]) && (int)refsField.GetValue(staged[i])! == 1,
                "submitting thread reuses every cross-thread returned staged object exactly once");
        }
        foreach (object item in staged) releaseStaged.Invoke(item, null);

        Type rangesType = typeof(SystemRunner).Assembly.GetType("SomeEngine.ECS.Systems.QueryRanges")!;
        MethodInfo rentRanges = rangesType.GetMethod("Rent", statics)!;
        MethodInfo returnRanges = rangesType.GetMethod("Return", instance)!;
        MethodInfo buildRanges = rangesType.GetMethod("Build", instance)!;
        FieldInfo itemsField = rangesType.GetField("Items", instance)!;
        FieldInfo countField = rangesType.GetField("Count", instance)!;
        FieldInfo filterField = rangesType.GetField("_hasFilters", instance)!;
        using var world = new World();
        for (int i = 0; i < 257; i++) world.Create(new SystemValue(i), new SystemStep(1));
        Query query = world.Query(new QueryDescription().With<SystemStep>().WithChanged<SystemValue>(0));
        using var queryRead = query.OpenRead();
        var ranges = new object[count];
        var arrays = new Dictionary<object, Array>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < count; i++)
        {
            ranges[i] = rentRanges.Invoke(null, null)!;
            buildRanges.Invoke(ranges[i], [query, 1]);
            Array items = (Array)itemsField.GetValue(ranges[i])!;
            Require((int)countField.GetValue(ranges[i])! == 257 && (bool)filterField.GetValue(ranges[i])!,
                "range pool fixture has populated filtered ranges");
            arrays.Add(ranges[i], items);
        }
        OnOtherThread(() => { foreach (object item in ranges) returnRanges.Invoke(item, null); });
        var rangesSet = new HashSet<object>(ranges, ReferenceEqualityComparer.Instance);
        long retainedRangeSlots = 0;
        for (int i = 0; i < count; i++)
        {
            ranges[i] = rentRanges.Invoke(null, null)!;
            Array items = (Array)itemsField.GetValue(ranges[i])!;
            Require(rangesSet.Remove(ranges[i]) && ReferenceEquals(items, arrays[ranges[i]])
                && (int)countField.GetValue(ranges[i])! == 0 && !(bool)filterField.GetValue(ranges[i])!,
                "submitting thread reuses range objects and grown arrays with effective state reset");
            Type rangeType = items.GetType().GetElementType()!;
            FieldInfo chunk = rangeType.GetField("Chunk", instance)!, filter = rangeType.GetField("Filter", instance)!;
            foreach (object item in items)
                Require(chunk.GetValue(item) is null && filter.GetValue(item) is null, "returned range capacity does not retain chunk or filter references");
            retainedRangeSlots += items.Length;
        }
        foreach (object item in ranges) returnRanges.Invoke(item, null);
        int stagedCount = (int)stagedType.GetField("s_freeCount", statics)!.GetValue(null)!;
        int stagedCapacity = ((Array)stagedType.GetField("s_free", statics)!.GetValue(null)!).Length;
        int rangesCount = (int)rangesType.GetField("s_freeCount", statics)!.GetValue(null)!;
        int rangesCapacity = ((Array)rangesType.GetField("s_free", statics)!.GetValue(null)!).Length;
        Console.WriteLine($"Cross-thread pools: {count} distinct staged and range objects reused; payload references cleared; release callbacks outside lock; staged free={stagedCount}/{stagedCapacity}, ranges free={rangesCount}/{rangesCapacity}, retained range slots={retainedRangeSlots}.");
    }

    private static void CheckQueryReadRejectsStructureChanges(Scheduler scheduler)
    {
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity original = world.Create(new Position { X = 7 }, new Velocity { X = 1 });
        var action = new CreateDuringQuery(world);
        bool rejected = false;
        try { new SystemRunner(world, scheduler).Execute<Position, Velocity, CreateDuringQuery>(world.Query<Position, Velocity>(), ref action); }
        catch (JobResourceSafetyException) { rejected = true; }
        Entity created = world.Create(new Position { X = 9 });
        Require(rejected && action.Calls == 1 && world.EntityCount == 2
            && world.ReadValue<Position>(original).X == 7 && world.ReadValue<Position>(created).X == 9,
            "query structure reads reject callback creation and release access when the callback ends");
    }

    private static void CheckQueryWriteKeepsReadGuard(Scheduler scheduler)
    {
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity original = world.Create(new Position { X = 7 }, new Velocity { X = 1 });
        using var structure = world.OpenStructureWrite();
        var action = new CreateDuringQuery(world);
        bool rejected = false;
        try { world.Query<Position, Velocity>().ForEach<Position, Velocity, CreateDuringQuery>(ref action); }
        catch (InvalidOperationException) { rejected = true; }
        Entity created = world.Create(new Position { X = 9 });
        Require(rejected && action.Calls == 1 && world.EntityCount == 2
            && world.ReadValue<Position>(original).X == 7 && world.ReadValue<Position>(created).X == 9,
            "queries under structure write access protect borrowed rows until the callback ends");
    }

    private struct CreateDuringQuery(World world) : IComponentAction<Position, Velocity>
    {
        internal int Calls;
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
        {
            Calls++;
            world.Create(new Position { X = 17 });
        }
    }

    private static void CheckScheduledOptionalRead(Scheduler scheduler)
    {
        using var world = new World(256, execution: new WorldJobs(scheduler));
        var entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new SystemValue(i), new SystemStep(1), new RandomValue(2));
        Query query = world.Query(new QueryDescription().With<SystemValue>().With<SystemStep>().Optional<RandomValue>());
        var runner = new SystemRunner(world, scheduler);
        runner.Schedule<SystemValue, SystemStep, ReadRandom>(query, new(world, entities)).Complete();
        for (int i = 0; i < entities.Length; i++)
            Require(world.ReadValue<SystemValue>(entities[i]).Value == i + 3, "scheduled optional values are readable through the declared query access");
    }

    private static void CheckOptionalReadFollowsWriter(Scheduler scheduler)
    {
        using var world = new World(256, execution: new WorldJobs(scheduler));
        var entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new SystemValue(i), new SystemStep(1), new RandomValue(2));
        Query query = world.Query(new QueryDescription().With<SystemValue>().With<SystemStep>().Optional<RandomValue>());
        var group = new SystemGroup(world, scheduler);
        group.Add<RandomValue, SystemStep, SetRandomFive>(world.Query<RandomValue, SystemStep>(), new());
        group.Add<SystemValue, SystemStep, ReadRandom>(query, new(world, entities));
        group.Execute();
        for (int i = 0; i < entities.Length; i++)
            Require(world.ReadValue<SystemValue>(entities[i]).Value == i + 6, "optional query reads follow the preceding writer in the system graph");
    }

    private readonly struct SetRandomFive : IComponentAction<RandomValue, SystemStep>
    {
        public void Execute(Span<RandomValue> values, ReadOnlySpan<SystemStep> steps)
        {
            for (int i = 0; i < values.Length; i++) values[i].Value = 5;
        }
    }

    private static void CheckParallelRangeWrites(Scheduler scheduler)
    {
        using var world = new World(256, execution: new WorldJobs(scheduler));
        var entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new Position { X = i }, new Velocity { X = 1, Y = 2 }, new JobOrdinal(i));
        using (var binding = world.OpenCachedWrite<Position>()) { }
        var runner = new SystemRunner(world, scheduler, 64);
        runner.Schedule<Position, Velocity, JobOrdinal, WriteCurrentRows>(world.Query<Position, Velocity, JobOrdinal>(), new(world, entities)).Complete();
        for (int i = 0; i < entities.Length; i++)
        {
            Position value = world.ReadValue<Position>(entities[i]);
            Require(value.X == i + 2 && value.Y == 2, "parallel World and Lookup writes stay in their assigned rows");
        }
    }

    private readonly struct WriteCurrentRows(World world, Entity[] entities) : IComponentAction<Position, Velocity, JobOrdinal>
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
        {
            using var writes = world.OpenCachedWrite<Position>();
            for (int row = 0; row < positions.Length; row++)
            {
                Entity entity = entities[ordinals[row].Value];
                world.Get<Position>(entity).X += velocities[row].X;
                Position value = world.ReadValue<Position>(entity);
                value.Y += velocities[row].Y;
                world.Set(entity, value);
                writes.GetKnown(entity).X++;
            }
        }
    }

    private static void CheckParallelRangeRejectsOtherRows(Scheduler scheduler)
    {
        using var world = new World(256, execution: new WorldJobs(scheduler));
        var entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new Position { X = i }, new Velocity(), new JobOrdinal(i));
        var runner = new SystemRunner(world, scheduler, 64);
        bool failed = false;
        try { runner.Schedule<Position, Velocity, JobOrdinal, WriteOtherRange>(world.Query<Position, Velocity, JobOrdinal>(), new(world, entities)).Complete(); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed, "a parallel World write requires membership in the current range");
        for (int i = 0; i < entities.Length; i++)
            Require(world.ReadValue<Position>(entities[i]).X == i, "rejected range writes preserve the other rows");
    }

    private readonly struct WriteOtherRange(World world, Entity[] entities) : IComponentAction<Position, Velocity, JobOrdinal>
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
            => world.Set(entities[ordinals[0].Value == 0 ? 127 : 0], new Position { X = -1 });
    }

    private static void CheckParallelQueryViewRejectsOtherRows(Scheduler scheduler)
    {
        using var world = new World(256, execution: new WorldJobs(scheduler));
        var entities = new Entity[128];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new Position { X = i }, new Velocity());
        Query query = world.Query<Position, Velocity>();
        var runner = new SystemRunner(world, scheduler, 64);
        bool failed = false;
        try { runner.Schedule<Position, Velocity, WriteThroughQuery>(query, new(query)).Complete(); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed, "a parallel query write view fits the current work range");
        for (int i = 0; i < entities.Length; i++)
            Require(world.ReadValue<Position>(entities[i]).X == i, "rejected wider query views preserve the other rows");
    }

    private readonly struct WriteThroughQuery(Query query) : IComponentAction<Position, Velocity>
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
        {
            using var scope = query.OpenRead();
            foreach (var rows in scope) rows.Write<Position>()[127].X = -1;
        }
    }

    private static void Check(Scheduler scheduler, int count, int chunkCapacity)
    {
        using var world = new World(count, chunkCapacity: chunkCapacity);
        var entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            entities[i] = world.Create(new SystemValue(i), new SystemStep(2));
            if (i % 4 == 0) world.Disable<SystemStep>(entities[i]);
        }
        var runner = new SystemRunner(world, scheduler, rangeRows: 256);
        ulong before = world.Version;
        runner.ExecuteParallel<SystemValue, SystemStep, Advance>(world.Query<SystemValue, SystemStep>(), new Advance());
        Require(world.Version == before + 1, "one version for the complete parallel write");
        int selected = 0;
        for (int i = 0; i < count; i++)
        {
            bool enabled = i % 4 != 0;
            Require(world.Read<SystemValue>(entities[i]).Value == i + (enabled ? 2 : 0), "selected rows receive their values");
            if (enabled) selected++;
        }
        Require(world.Query(new QueryDescription().WithChanged<SystemValue>(before)).Count == selected,
            "completed block summaries expose every written row");
        Require(world.Query(new QueryDescription().WithAdded<SystemValue>(before)).Count == 0, "write batches keep the existing added versions");

        before = world.Version;
        int changed = 0;
        for (int i = 1; i < count; i += 8)
        {
            world.Set(entities[i], new SystemValue(-10));
            changed++;
        }
        Query query = world.Query(new QueryDescription().With<SystemStep>().WithChanged<SystemValue>(before));
        ulong beforeWork = world.Version;
        runner.ExecuteParallel<SystemValue, SystemStep, Advance>(query, new Advance());
        Require(world.Version == beforeWork + 1, "filtered writes share their batch version");
        Require(world.Query(new QueryDescription().WithChanged<SystemValue>(beforeWork)).Count == changed, "changed filters select the original changed members");
        for (int i = 1; i < count; i += 8) Require(world.Read<SystemValue>(entities[i]).Value == -8, "changed row values are published");
        world.Create(new SystemValue(1), new SystemStep(1));
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private static void CheckConnected(Scheduler scheduler)
    {
        const int count = 1_000;
        using var world = new World(count, execution: new WorldJobs(scheduler));
        Entity[] entities = new Entity[count];
        for (int i = 0; i < count; i++)
            entities[i] = world.Create(new SystemValue(i), new SystemStep(2), new RandomValue(1));
        var runner = new SystemRunner(world, scheduler, rangeRows: 256);
        Query query = world.Query<SystemValue, SystemStep>();
        ulong before = world.Version;
        runner.ExecuteParallel<SystemValue, SystemStep, Advance>(query, new Advance());
        Require(world.Version == before + 1, "connected synchronous work uses one write version");
        Require(world.Query(new QueryDescription().WithChanged<SystemValue>(before)).Count == count, "connected synchronous summaries are complete");

        var reads = new SystemAccesses().ReadRandom<RandomValue>();
        JobHandle first = runner.Schedule<SystemValue, SystemStep, ReadRandom>(query, new ReadRandom(world, entities), reads);
        JobHandle second = runner.Schedule<SystemValue, SystemStep, Advance>(query, new Advance(), dependency: first);
        second.Complete();
        for (int i = 0; i < count; i++)
            Require(world.ReadValue<SystemValue>(entities[i]).Value == i + 7, "dependent asynchronous ranges publish all row values");

        before = world.Version;
        var writes = new SystemAccesses().WriteRandom<RandomValue>();
        JobHandle written = runner.Schedule<SystemValue, SystemStep, WriteRandom>(query, new WriteRandom(world, entities[0]), writes);
        written.Complete();
        Require(world.Version == before + 1, "partitioned and random writes share one batch version");
        Require(world.ReadValue<RandomValue>(entities[0]).Value == count + 1, "global random writes run in one execution item");
        Require(world.Query(new QueryDescription().WithChanged<RandomValue>(before)).Count == 1, "random write facts retain exact members");
        world.Clear();
        Require(world.EntityCount == 0, "clear releases the completed connected world");
    }

    private readonly struct ReadRandom(World world, Entity[] entities) : IComponentAction<SystemValue, SystemStep>
    {
        public void Execute(Span<SystemValue> values, ReadOnlySpan<SystemStep> steps)
        {
            using var read = world.OpenRead<RandomValue>();
            for (int i = 0; i < values.Length; i++)
                values[i].Value += steps[i].Value + read.Read(entities[(values[i].Ordinal * 17) % entities.Length]).Value;
        }
    }

    private readonly struct WriteRandom(World world, Entity target) : IComponentAction<SystemValue, SystemStep>
    {
        public void Execute(Span<SystemValue> values, ReadOnlySpan<SystemStep> steps)
        {
            using var write = world.OpenWrite<RandomValue>();
            for (int i = 0; i < values.Length; i++)
            {
                values[i].Value += steps[i].Value;
                write.Get(target).Value++;
            }
        }
    }

    private sealed class GroupResult
    {
        internal DeferredEntity Created;
        internal int Children;
        internal bool Fail;
    }

    internal static void CheckArities()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        using var world = new World(execution: new WorldJobs(scheduler), chunkCapacity: 128);
        Entity[] entities = new Entity[257];
        for (int i = 0; i < entities.Length; i++) entities[i] = world.Create(new SystemValue(i), new SystemStep(2), new RandomValue(3));
        var runner = new SystemRunner(world, scheduler, 128);
        ulong before = world.Version;
        runner.ExecuteParallel<SystemValue, AdvanceOne>(world.Query<SystemValue>(), new AdvanceOne());
        runner.Schedule<SystemValue, SystemStep, RandomValue, AdvanceThree>(world.Query<SystemValue, SystemStep, RandomValue>(), new AdvanceThree()).Complete();
        Require(world.Version == before + 2, "one and three component work each use one batch version");
        foreach (Entity entity in entities)
        {
            SystemValue value = world.ReadValue<SystemValue>(entity);
            Require(value.Value == value.Ordinal + 6, "one and three component work publish every row");
        }
        var group = new SystemGroup(world, scheduler, 128);
        group.Add<SystemValue, AdvanceOne>(world.Query<SystemValue>(), new AdvanceOne());
        group.Add<SystemValue, SystemStep, RandomValue, AdvanceThree>(world.Query<SystemValue, SystemStep, RandomValue>(), new AdvanceThree());
        group.Execute();
        foreach (Entity entity in entities)
        {
            SystemValue value = world.ReadValue<SystemValue>(entity);
            Require(value.Value == value.Ordinal + 12, "one and three component systems follow their common dependency graph");
        }
        CheckSynchronousArities(scheduler);
        Console.WriteLine("System arities: one, two and three component kernels share range scheduling, access resources and completion stages; synchronous typed and explicit-access paths passed.");
    }

    private static void CheckSynchronousArities(Scheduler scheduler)
    {
        using var world = new World(execution: new WorldJobs(scheduler), chunkCapacity: 128);
        Entity[] entities = new Entity[257];
        for (int i = 0; i < entities.Length; i++)
            entities[i] = world.Create(new SystemValue(i), new SystemStep(2), new RandomValue(3));
        var runner = new SystemRunner(world, scheduler, 128);
        Query oneQuery = world.Query<SystemValue>();
        Query twoQuery = world.Query<SystemValue, SystemStep>();
        Query threeQuery = world.Query<SystemValue, SystemStep, RandomValue>();
        var one = new AdvanceOne();
        var two = new Advance();
        var three = new AdvanceThree();
        ulong before = world.Version;
        runner.Execute<SystemValue, AdvanceOne>(oneQuery, ref one);
        runner.Execute<SystemValue, SystemStep, Advance>(twoQuery, ref two);
        runner.Execute<SystemValue, SystemStep, RandomValue, AdvanceThree>(threeQuery, ref three);
        var explicitAccesses = new SystemAccesses().ReadRandom<RandomValue>();
        runner.Execute<SystemValue, AdvanceOne>(oneQuery, ref one, explicitAccesses);
        runner.Execute<SystemValue, SystemStep, Advance>(twoQuery, ref two, explicitAccesses);
        runner.Execute<SystemValue, SystemStep, RandomValue, AdvanceThree>(threeQuery, ref three, explicitAccesses);
        Require(world.Version == before + 6, "six synchronous calls each retain one write version");
        foreach (Entity entity in entities)
        {
            SystemValue value = world.ReadValue<SystemValue>(entity);
            Require(value.Value == value.Ordinal + 16, "unchanged public Execute syntax visits all rows for each arity and explicit access");
        }
        Require(world.Query(new QueryDescription().WithChanged<SystemValue>(before)).Count == entities.Length
            && world.Query(new QueryDescription().WithAdded<SystemValue>(before)).Count == 0,
            "batched synchronous writes retain exact changed rows without replacing Added versions");

        using var otherWorld = new World(execution: new WorldJobs(scheduler));
        var otherRunner = new SystemRunner(otherWorld, scheduler);
        otherRunner.Execute<SystemValue, AdvanceOne>(oneQuery, ref one);
        Require(world.ReadValue<SystemValue>(entities[0]).Value == 17 && otherWorld.EntityCount == 0,
            "typed execution selects the query's world rather than the runner's world");
        var failOne = new FailOne();
        var failThree = new FailThree();
        bool failedOne = false, failedThree = false;
        try { runner.Execute<SystemValue, FailOne>(oneQuery, ref failOne); }
        catch (InvalidOperationException error) when (error.Message == "expected typed callback failure") { failedOne = true; }
        try { runner.Execute<SystemValue, SystemStep, RandomValue, FailThree>(threeQuery, ref failThree, explicitAccesses); }
        catch (InvalidOperationException error) when (error.Message == "expected typed callback failure") { failedThree = true; }
        Require(failedOne && failedThree, "typed callbacks preserve exceptions");
        using (world.OpenStructureWrite())
        {
            runner.Execute<SystemValue, AdvanceOne>(oneQuery, ref one, explicitAccesses);
            runner.Execute<SystemValue, SystemStep, RandomValue, AdvanceThree>(threeQuery, ref three, explicitAccesses);
        }
        Require(world.ReadValue<SystemValue>(entities[0]).Value == 23,
            "an existing structure-write scope remains owned after typed explicit-access queries");
        using (world.OpenStructureWrite()) { }

        using var plainWorld = new World();
        Entity plainEntity = plainWorld.Create(new SystemValue(0), new SystemStep(2), new RandomValue(3));
        var plainRunner = new SystemRunner(plainWorld, scheduler);
        ulong plainBefore = plainWorld.Version;
        plainRunner.Execute<SystemValue, AdvanceOne>(plainWorld.Query<SystemValue>(), ref one, explicitAccesses);
        plainRunner.Execute<SystemValue, SystemStep, Advance>(plainWorld.Query<SystemValue, SystemStep>(), ref two, explicitAccesses);
        plainRunner.Execute<SystemValue, SystemStep, RandomValue, AdvanceThree>(plainWorld.Query<SystemValue, SystemStep, RandomValue>(), ref three, explicitAccesses);
        Require(plainWorld.ReadValue<SystemValue>(plainEntity).Value == 8 && plainWorld.Version == plainBefore + 3,
            "explicit-access fallback retains unchanged semantics without WorldJobs");
        using (plainWorld.OpenStructureWrite()) { }
    }

    private readonly struct FailOne : IComponentAction<SystemValue>
    {
        public void Execute(Span<SystemValue> values) => throw new InvalidOperationException("expected typed callback failure");
    }
    private readonly struct FailThree : IComponentAction<SystemValue, SystemStep, RandomValue>
    {
        public void Execute(Span<SystemValue> values, ReadOnlySpan<SystemStep> steps, ReadOnlySpan<RandomValue> additions)
            => throw new InvalidOperationException("expected typed callback failure");
    }

    private readonly struct AdvanceOne : IComponentAction<SystemValue>
    {
        public void Execute(Span<SystemValue> values) { foreach (ref SystemValue value in values) value.Value++; }
    }
    private readonly struct AdvanceThree : IComponentAction<SystemValue, SystemStep, RandomValue>
    {
        public void Execute(Span<SystemValue> values, ReadOnlySpan<SystemStep> steps, ReadOnlySpan<RandomValue> additions)
        {
            for (int i = 0; i < values.Length; i++) values[i].Value += steps[i].Value + additions[i].Value;
        }
    }

    internal static void CheckGroup()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity parent = world.Create(new SystemValue(0), new SystemStep(1));
        using var commands = new CommandBatch(world, 1);
        var result = new GroupResult();
        var accesses = new SystemAccesses();
        var group = new SystemGroup(world, scheduler, rangeRows: 128);
        SystemState first = group.Add<SystemValue, SystemStep, RecordChild>(world.Query<SystemValue, SystemStep>(),
            new RecordChild(commands[0], parent, result), accesses, commands, parallel: false);
        SystemState maintenance = group.AddJob(new MaintainChildren(world), structureWrite: true);
        group.After(maintenance, first);
        SystemState second = group.Add<SystemValue, SystemStep, ObserveChildren>(world.Query<SystemValue, SystemStep>(),
            new ObserveChildren(world, parent, result));
        group.After(second, maintenance);
        JobHandle finished = group.Schedule();
        finished.Complete();
        Require(result.Children == 1 && world.IsAlive(result.Created.Resolve()), "group completion publishes commands, retained results and maintained children");
        Require(first.LastSystemVersion == first.CurrentSystemVersion && second.LastSystemVersion > first.LastSystemVersion,
            "each system commits its observation upper bound after its completion stages");
        group.Execute();
        Require(result.Children == 2 && group.GraphBuildCount == 1, "repeated execution reuses the declared system graph");
        accesses.ReadResource<int>();
        group.Execute();
        Require(group.GraphBuildCount == 2 && result.Children == 3, "declaration changes rebuild the system graph");

        var failing = new SystemGroup(world, scheduler);
        SystemState failed = failing.Add<SystemValue, SystemStep, RecordChild>(world.Query<SystemValue, SystemStep>(),
            new RecordChild(commands[0], parent, result), commands: commands, parallel: false);
        result.Fail = true;
        bool threw = false;
        try { failing.Execute(); }
        catch (InvalidOperationException error) when (error.Message == "group work failed") { threw = true; }
        Require(threw && failed.LastSystemVersion == 0 && commands[0].Count == 0,
            "failed work releases commands while retaining the prior observation baseline");
        using (var children = Hierarchy<DefaultHierarchyDomain>.GetChildren(world, parent))
            Require(children.Entities.Length == 3, "failed work leaves the completed hierarchy available");
        result.Fail = false;
        failing.Execute();
        Require(failed.LastSystemVersion == failed.CurrentSystemVersion && world.IsAlive(result.Created.Resolve()),
            "the next successful run advances the retained baseline");
        Console.WriteLine("System group: cached graph, declaration changes, commands, maintenance, completion handles, success baselines and failure cleanup passed.");
    }

    private readonly struct RecordChild(CommandBuffer commands, Entity parent, GroupResult result) : IComponentAction<SystemValue, SystemStep>
    {
        public void Execute(Span<SystemValue> values, ReadOnlySpan<SystemStep> steps)
        {
            values[0].Value += steps[0].Value;
            result.Created = commands.Create(new Position { X = 17 });
            commands.EditParents<DefaultHierarchyDomain>([new(result.Created, parent)], deferred: true);
            if (result.Fail) throw new InvalidOperationException("group work failed");
        }
    }

    private readonly struct ObserveChildren(World world, Entity parent, GroupResult result) : IComponentAction<SystemValue, SystemStep>
    {
        public void Execute(Span<SystemValue> values, ReadOnlySpan<SystemStep> steps)
        {
            using var children = Hierarchy<DefaultHierarchyDomain>.GetChildren(world, parent);
            result.Children = children.Entities.Length;
        }
    }
    private readonly struct MaintainChildren(World world) : IJob
    {
        public void Execute() => Hierarchy<DefaultHierarchyDomain>.Maintain(world);
    }

}
