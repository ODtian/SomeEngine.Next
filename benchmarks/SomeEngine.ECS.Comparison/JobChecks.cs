using System.Collections.Concurrent;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static class JobChecks
{
    internal static void Run()
    {
        FollowingValueReadsAdvanceWhileStructureWriterWaits();
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false, MaxQueuedWorkItems = 512 });
        int[] items = Enumerable.Range(0, 257).ToArray();
        int[] visits = new int[items.Length];
        using var together = new Barrier(4);
        var threads = new ConcurrentDictionary<int, byte>();
        scheduler.ExecuteRanges(items, items.Length, new ConcurrentVisits(visits, together, threads));
        Require(visits.All(count => count == 1) && threads.Count == 4, "caller and three workers complete each range once");

        Array.Clear(visits);
        try
        {
            scheduler.ExecuteRanges(items, items.Length, new FaultedVisits(visits));
            throw new InvalidOperationException("The range fault was not propagated.");
        }
        catch (RangeFailure) { }
        Require(visits.All(count => count == 1), "fault propagation follows completion of all ranges");
        scheduler.ExecuteRanges(items, items.Length, new Visits(visits));
        Require(visits.All(count => count == 2), "the next batch reuses its data after a fault");

        int[] nestedItems = Enumerable.Range(0, 17).ToArray();
        int[] nestedVisits = new int[9 * nestedItems.Length];
        scheduler.ExecuteRanges(Enumerable.Range(0, 9).ToArray(), 9,
            new NestedVisits(scheduler, nestedItems, nestedVisits));
        Require(nestedVisits.All(count => count == 1), "nested synchronous ranges complete before the outer range");

        JobResource resource = scheduler.CreateResource("range values");
        Array.Clear(visits);
        using (var access = scheduler.AcquireSynchronousAccess([JobResourceAccess.Write(resource)]))
            scheduler.ExecuteRanges(items, items.Length, new ResourceVisits(scheduler, resource, visits), access.Handle);
        Require(visits.All(count => count == 1), "all participants inherit the acquired resource access");

        JobHandle first = scheduler.Schedule(new AddValues(scheduler, resource, visits, 2), [JobResourceAccess.Write(resource)]);
        JobHandle second = scheduler.Schedule(new AddValues(scheduler, resource, visits, 3), [JobResourceAccess.Write(resource)], first);
        second.Complete();
        Require(visits.All(count => count == 6), "resource dependencies and generic jobs complete in order");
        scheduler.ReleaseResource(resource);
        CheckStages(scheduler, items);
        RangeCountersIncludePublishedHelpers();
        Console.WriteLine("Job ranges and access stages: shared workers, completion, faults/reuse, nesting, resource context, dependencies and two-phase access passed.");
    }

    private static void FollowingValueReadsAdvanceWhileStructureWriterWaits()
    {
        using var scheduler = new Scheduler(new JobConfiguration
        {
            WorkerCount = 0, EnableCounters = false, MaxQueuedWorkItems = 64,
            MaxCompletionStates = 64, MaxResourceStates = 16,
        });
        JobResource structure = scheduler.CreateResource("structure before values");
        JobResource values = scheduler.CreateResource("following values");
        int[] data = [42], observed = [-1, -1], written = [0];
        using var waiting = new ManualResetEventSlim();
        using var read = new ManualResetEventSlim();
        SynchronousResourceAccess first = scheduler.AcquireSynchronousAccess([JobResourceAccess.Read(structure)]);
        JobHandle writer = scheduler.Schedule(new MarkStructureWrite(written), [JobResourceAccess.Write(structure)]);
        // With zero workers, the waiting synchronous caller executes this signal.
        JobHandle signal = scheduler.Schedule(new SignalSynchronousWait(waiting));
        Task caller = Task.Run(() =>
        {
            AccessBuilder<JobResourceAccess> following = new();
            following.Add(JobResourceAccess.Write(values));
            try
            {
                using var access = scheduler.AcquireSynchronousAccess(JobResourceAccess.Read(structure), following);
                observed[1] = Volatile.Read(ref written[0]);
                data[0]++;
            }
            finally { following.Clear(); }
        });
        JobHandle reader = default;
        bool reached = false, advanced = false;
        try
        {
            reached = waiting.Wait(TimeSpan.FromSeconds(30));
            if (reached)
            {
                reader = scheduler.Schedule(new ObserveFollowingValue(data, observed, read), [JobResourceAccess.Read(values)]);
                advanced = read.Wait(TimeSpan.FromSeconds(30));
            }
        }
        finally { first.Dispose(); }
        bool completed = caller.Wait(TimeSpan.FromSeconds(30));
        writer.Complete(); signal.Complete(); reader.Complete();
        Require(reached && advanced && completed && observed[0] == 42 && observed[1] == 1 && data[0] == 43,
            "following values remain available while a structure writer waits, then both writer and caller complete");
        scheduler.ReleaseResource(values);
        scheduler.ReleaseResource(structure);
    }

    private readonly struct SignalSynchronousWait(ManualResetEventSlim reached) : IJob
    {
        public void Execute() => reached.Set();
    }

    private readonly struct MarkStructureWrite(int[] written) : IJob
    {
        public void Execute() => Volatile.Write(ref written[0], 1);
    }

    private readonly struct ObserveFollowingValue(int[] data, int[] observed, ManualResetEventSlim read) : IJob
    {
        public void Execute() { observed[0] = data[0]; read.Set(); }
    }

    private static void RangeCountersIncludePublishedHelpers()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = true });
        int[] items = Enumerable.Range(0, 257).ToArray();
        int[] visits = new int[items.Length];
        scheduler.ExecuteRanges(items, items.Length, new Visits(visits));
        scheduler.Dispose();
        JobStatistics stats = scheduler.GetStats();
        Require(visits.All(count => count == 1) && stats.ReadyTicketsPublished == 3
            && stats.ReadyTicketsExecuted == 3 && stats.QueuedWorkItems == 3,
            "range counters include each published helper through queue completion");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private readonly struct Visits(int[] visits, int offset = 0) : IRangeWork<int>
    {
        public void Execute(in int item) => Interlocked.Increment(ref visits[offset + item]);
    }

    private readonly struct ConcurrentVisits(
        int[] visits, Barrier together, ConcurrentDictionary<int, byte> threads) : IRangeWork<int>
    {
        public void Execute(in int item)
        {
            threads.TryAdd(Environment.CurrentManagedThreadId, 0);
            if (item < 4) together.SignalAndWait();
            Interlocked.Increment(ref visits[item]);
        }
    }

    private sealed class RangeFailure : Exception;

    private readonly struct FaultedVisits(int[] visits) : IRangeWork<int>
    {
        public void Execute(in int item)
        {
            Interlocked.Increment(ref visits[item]);
            if (item == 37) throw new RangeFailure();
        }
    }

    private readonly struct NestedVisits(Scheduler scheduler, int[] items, int[] visits) : IRangeWork<int>
    {
        public void Execute(in int item) =>
            scheduler.ExecuteRanges(items, items.Length, new Visits(visits, item * items.Length));
    }

    private readonly struct ResourceVisits(Scheduler scheduler, JobResource resource, int[] visits) : IRangeWork<int>
    {
        public void Execute(in int item)
        {
            scheduler.RequireCurrentAccess(JobResourceAccess.Write(resource, item, 1), requireSingleWorkItem: false);
            Interlocked.Increment(ref visits[item]);
        }
    }

    private readonly struct AddValues(Scheduler scheduler, JobResource resource, int[] values, int add) : IJob
    {
        public void Execute()
        {
            scheduler.RequireCurrentAccess(JobResourceAccess.Write(resource), requireSingleWorkItem: true);
            for (int i = 0; i < values.Length; i++) values[i] += add;
        }
    }

    private static void CheckStages(Scheduler scheduler, int[] items)
    {
        JobResource structure = scheduler.CreateResource("world structure");
        JobResource values = scheduler.CreateResource("column values");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle blocker = scheduler.Schedule(new HoldValues(entered, release), [JobResourceAccess.Write(values)]);
        entered.Wait();
        using var data = new StageData(scheduler, structure, values, items);
        JobHandle dependency = scheduler.Schedule(new SetDependency(data));
        JobHandle work = scheduler.ScheduleStages(new StageWork(data), dependency);
        data.ValuesPrepared.Wait();
        Require(data.Executed == 0, "the value stage waits for its resource predecessor");
        release.Set();
        work.Complete();
        blocker.Complete();
        Require(data.Prepared == 2 && data.Executed == 1 && data.Released == 1 && data.Visits.All(count => count == 1),
            "one state retains both access stages through synchronous range completion");

        using var failed = new StageData(scheduler, structure, values, items) { FailPrepare = true };
        JobHandle failedWork = scheduler.ScheduleStages(new StageWork(failed), scheduler.Schedule(new SetDependency(failed)));
        try { failedWork.Complete(); throw new InvalidOperationException("Preparation fault was not propagated."); }
        catch (RangeFailure) { }
        Require(failed.Prepared == 2 && failed.Executed == 0 && failed.Released == 1, "a preparation fault releases its retained structure access");

        using var dependent = new StageData(scheduler, structure, values, items);
        JobHandle failedDependency = scheduler.Schedule(new FailDependency());
        JobHandle canceled = scheduler.ScheduleStages(new StageWork(dependent), failedDependency);
        try { canceled.Complete(); throw new InvalidOperationException("Dependency fault was not propagated."); }
        catch (RangeFailure) { }
        Require(dependent.Prepared == 0 && dependent.Executed == 0 && dependent.Released == 1,
            "an explicit dependency fault completes before preparation");
        scheduler.ReleaseResource(structure);
        scheduler.ReleaseResource(values);
    }

    private sealed class StageData(Scheduler scheduler, JobResource structure, JobResource values, int[] items) : IDisposable
    {
        internal readonly Scheduler Scheduler = scheduler;
        internal readonly JobResource Structure = structure, Values = values;
        internal readonly int[] Items = items, Visits = new int[items.Length];
        internal readonly ManualResetEventSlim ValuesPrepared = new();
        internal int Dependency, Prepared, Executed, Released;
        internal bool FailPrepare;
        public void Dispose() => ValuesPrepared.Dispose();
    }

    private readonly struct SetDependency(StageData data) : IJob
    {
        public void Execute() => Volatile.Write(ref data.Dependency, 1);
    }

    private readonly struct FailDependency : IJob { public void Execute() => throw new RangeFailure(); }

    private readonly struct HoldValues(ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute() { entered.Set(); release.Wait(); }
    }

    private readonly struct StageWork(StageData data) : IStagedJob
    {
        public static int StageCount => 2;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            Require(Volatile.Read(ref data.Dependency) == 1, "explicit dependency completes before preparing resources");
            Interlocked.Increment(ref data.Prepared);
            if (stage == 0) accesses.Add(JobResourceAccess.Read(data.Structure));
            else
            {
                data.Scheduler.RequireCurrentAccess(JobResourceAccess.Read(data.Structure), requireSingleWorkItem: false);
                if (data.FailPrepare) throw new RangeFailure();
                accesses.Add(JobResourceAccess.Write(data.Values));
                data.ValuesPrepared.Set();
            }
        }
        public void Execute()
        {
            Interlocked.Increment(ref data.Executed);
            data.Scheduler.RequireCurrentAccess(JobResourceAccess.Read(data.Structure), requireSingleWorkItem: false);
            data.Scheduler.ExecuteRanges(data.Items, data.Items.Length, new ResourceVisits(data.Scheduler, data.Values, data.Visits));
        }
        public void Release() => Interlocked.Increment(ref data.Released);
    }
}
