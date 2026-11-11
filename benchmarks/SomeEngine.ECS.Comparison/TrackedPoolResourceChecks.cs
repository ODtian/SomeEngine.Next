using System.Collections;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

// Diagnostic fixture only: real tracked payload, fixed work/GC counts, no timing
// or allocation/capacity acceptance threshold and no production pool mutation.
internal static class TrackedPoolResourceChecks
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static int Run(string[] args)
    {
        if (args.Length != 4 || args[1] is not ("schedule" or "parallel")
            || !int.TryParse(args[2], out int count) || count < 2)
            throw new ArgumentException("Usage: check-tracked-pool-resources <schedule|parallel> <count >= 2> <new-report.json>");
        string path = Path.GetFullPath(args[3]);
        string artifacts = Path.GetFullPath("artifacts");
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Directory.Exists(artifacts)
            || !string.Equals(Path.GetDirectoryName(path), artifacts, pathComparison) || File.Exists(path))
            throw new ArgumentException("The report must be a new file directly in this project's existing artifacts directory.");

        LifecycleResult result = Exercise(args[1], count);
        CollectOnce();
        WeakObservation[] weak = ObserveWeakTargets(result.Targets);
        bool businessReferencesCollected = weak.All(value => !value.Alive);
        bool freeSlotsCleared = result.FinalPools.Where(value => value.Applicable)
            .All(value => value.Violations.Length == 0 && value.PayloadReferences == 0);
        bool workersReturned = result.Workers.Length == 3 && result.Workers.All(value => value.ExpectedReturnedState);
        bool expectedPoolsObserved = result.FinalPools[0].FreeCount > 0
            && (args[1] == "schedule" ? result.FinalPools[1].FreeCount > 0
                : result.FinalPools.Skip(2).Any(value => value.FreeCount > 0));
        bool diagnosticPassed = businessReferencesCollected && freeSlotsCleared && workersReturned && expectedPoolsObserved
            && result.WorldDisposedCheckPassed;

        var report = new
        {
            Workload = "tracked-job-" + args[1], InitialEntities = count, GrowthEntities = 1000,
            VerifiedRuns = 3, WorkerCount = 3,
            Framework = RuntimeInformation.FrameworkDescription,
            RuntimeVersion = Environment.Version.ToString(), Environment.ProcessorCount,
            ServerGC = GCSettings.IsServerGC,
            AllocationObservation = "Process-wide GC.GetTotalAllocatedBytes(false) deltas; approximate, not a threshold. Checks, reflection and forced GC are outside operation windows.",
            CapacityObservation = "Demand/cached capacity observations for this fixed scenario, not a global envelope.",
            ManagedHeapAndProcessHoldingObserved = false,
            HoldingCoverage = "No held-owner or post-GC managed/process holding snapshots. Slot capacities and weak reachability do not quantify retained bytes.",
            RangePoolCoverage = "Both actual claims closed types are named; TLS pool slots/capacity are visible only on the calling thread. Other worker TLS capacities are unobserved.",
            WorkerObservation = "Actual owned workers after Scheduler.Dispose returned. Static-Free membership is not Thread.Join. Join calls are not instrumented.",
            Collection = "Long weak references (trackResurrection=true), observed after exactly one full blocking compacting GC, WaitForPendingFinalizers, then one full blocking compacting GC, after the NoInlining lifecycle helper returned.",
            result.Phases, result.FinalPools, result.Workers,
            WorkerCacheImplementationLimit = result.WorkerCacheLimit,
            WorkerFreeCountAfterDispose = result.WorkerFreeCount,
            WeakTargets = weak,
            result.WorldDisposedCheckPassed,
            BusinessReferencesCollected = businessReferencesCollected,
            FinalVisibleFreeSlotsCleared = freeSlotsCleared,
            WorkerOwnershipChecksPassed = workersReturned,
            ExpectedActualPoolsObserved = expectedPoolsObserved,
            DiagnosticChecksPassed = diagnosticPassed,
            OverallResourcePassed = (bool?)null,
            TimingAcceptanceClaimed = false,
        };
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Tracked pool diagnostics: mode={args[1]}, verified runs=3, growth=1000, "
            + $"business targets={weak.Length}, collected={businessReferencesCollected}, "
            + $"visible free slots cleared={freeSlotsCleared}, worker ownership={workersReturned}, actual pools observed={expectedPoolsObserved}. "
            + "OverallResourcePassed=null; other worker TLS capacities are unobserved; no timing acceptance.");
        if (!diagnosticPassed)
            throw new InvalidOperationException("Tracked pool diagnostic checks failed; see the report. No resource acceptance threshold was applied.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifecycleResult Exercise(string mode, int count)
    {
        Scheduler? scheduler = null;
        SourceFullArchiveWorld? world = null;
        TrackedArchiveJobs? jobs = null;
        Array? ownedWorkers = null;
        PoolTypes? types = null;
        var phases = new List<PhaseObservation>();
        var targets = new List<WeakTarget>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool disposedCheck = false;
        try
        {
            long before = GC.GetTotalAllocatedBytes(false);
            scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
            phases.Add(new("scheduler-create", GC.GetTotalAllocatedBytes(false) - before, []));

            // The canonical factory is selected, not a replacement Apply/probe job.
            ArchiveComparison.Library library = ArchiveComparison.Libraries(count, true, scheduler)
                .Single(value => value.Name == "SomeEngine");
            before = GC.GetTotalAllocatedBytes(false);
            world = (SourceFullArchiveWorld)library.Create();
            jobs = world.ConnectTrackedJobs(scheduler);
            if (world.World.Execution is not WorldJobs)
                throw new InvalidOperationException("The tracked fixture requires the connected WorldJobs path.");
            phases.Add(new("prepare-create-and-connect", GC.GetTotalAllocatedBytes(false) - before, []));

            RunPhase("initial-run");
            before = GC.GetTotalAllocatedBytes(false);
            world.Grow();
            jobs.RecordGrowth();
            if (world.GrowthEntities.Count != 1000)
                throw new InvalidOperationException("The canonical growth must add exactly 1000 entities.");
            phases.Add(new("grow-and-record", GC.GetTotalAllocatedBytes(false) - before, []));
            RunPhase("first-run-after-growth");
            RunPhase("same-instance-reuse");

            void RunPhase(string phase)
            {
                long start = GC.GetTotalAllocatedBytes(false);
                jobs.Run(mode);
                long bytes = GC.GetTotalAllocatedBytes(false) - start;
                jobs.Check();
                // Do not prewarm Query.GetMatches or pool statics before first Run.
                CaptureTargets(phase, world, jobs, scheduler, targets, seen);
                object queue = RequiredField(scheduler, "_workQueue");
                ownedWorkers ??= (Array)RequiredField(queue, "_workers");
                AddTarget(targets, seen, "scheduler-work-queue", queue);
                AddTarget(targets, seen, "owned-worker-array", ownedWorkers);
                types ??= CreatePoolTypes();
                phases.Add(new(phase, bytes, SnapshotPools(types, mode, false)));
            }
        }
        finally
        {
            try
            {
                if (jobs is not null)
                {
                    long before = GC.GetTotalAllocatedBytes(false);
                    jobs.Dispose();
                    phases.Add(new("jobs-dispose", GC.GetTotalAllocatedBytes(false) - before, []));
                }
            }
            finally
            {
                try
                {
                    if (world is not null)
                    {
                        long before = GC.GetTotalAllocatedBytes(false);
                        world.Dispose();
                        phases.Add(new("world-dispose", GC.GetTotalAllocatedBytes(false) - before, []));
                        world.CheckDisposed();
                        disposedCheck = true;
                    }
                }
                finally
                {
                    if (scheduler is not null)
                    {
                        long before = GC.GetTotalAllocatedBytes(false);
                        scheduler.Dispose();
                        phases.Add(new("scheduler-dispose", GC.GetTotalAllocatedBytes(false) - before, []));
                    }
                }
            }
        }

        PoolObservation[] final = SnapshotPools(types ?? throw new InvalidOperationException("No tracked run completed."), mode, true);
        WorkerState workers = SnapshotWorkers(ownedWorkers ?? throw new InvalidOperationException("No actual worker array was captured."));
        // Strong targets, factory closure, reflected boxes and pool arrays end here.
        return new(targets.ToArray(), phases.ToArray(), final, workers.Workers,
            workers.CacheLimit, workers.FreeCount, disposedCheck);
    }

    private static void CaptureTargets(string phase, SourceFullArchiveWorld world,
        TrackedArchiveJobs jobs, Scheduler scheduler, List<WeakTarget> targets, HashSet<object> seen)
    {
        AddTarget(targets, seen, "archive-wrapper", world);
        AddTarget(targets, seen, "world", world.World);
        AddTarget(targets, seen, "original-entities", world.Entities);
        AddTarget(targets, seen, "original-edges", world.Edges);
        AddTarget(targets, seen, "growth-entities", world.GrowthEntities);
        AddTarget(targets, seen, "tracked-jobs", jobs);
        AddTarget(targets, seen, "system-runner", RequiredField(jobs, "_runner"));
        AddTarget(targets, seen, "scheduler", scheduler);
        object boxedQuery = RequiredField(jobs, "_query");
        object data = RequiredField(boxedQuery, "_data");
        AddTarget(targets, seen, "query-data", data); // Query itself is a struct: never weak-reference its box.
        Array matches = (Array)RequiredField(data, "Matches");
        AddTarget(targets, seen, phase + "/query-matches-array", matches);
        int matchCount = (int)RequiredField(data, "Count");
        if (matchCount < 0 || matchCount > matches.Length)
            throw new InvalidOperationException("Actual query match count is invalid.");
        for (int i = 0; i < matchCount; i++)
        {
            object match = matches.GetValue(i) ?? throw new InvalidOperationException("Missing query match.");
            object archetype = RequiredMember(match, "Archetype");
            AddTarget(targets, seen, phase + "/archetype-" + i, archetype);
            Array? chunks = ReadField(archetype, "_chunks") as Array;
            int chunkCount = (int)RequiredField(archetype, "ChunkCount");
            if (chunks is null)
                AddTarget(targets, seen, phase + "/chunk-" + i + "-0", RequiredField(archetype, "PrimaryChunk"));
            else
            {
                if (chunkCount < 0 || chunkCount > chunks.Length)
                    throw new InvalidOperationException("Actual archetype chunk count is invalid.");
                for (int j = 0; j < chunkCount; j++)
                    AddTarget(targets, seen, phase + "/chunk-" + i + "-" + j,
                        chunks.GetValue(j) ?? throw new InvalidOperationException("Missing actual chunk."));
            }
        }
        if (matchCount == 0)
            throw new InvalidOperationException("The fixture must exercise a nonempty tracked query shape.");
    }

    private static void AddTarget(List<WeakTarget> targets, HashSet<object> seen, string label, object target)
    {
        if (target.GetType().IsValueType)
            throw new InvalidOperationException("A boxed value is not a valid lifecycle target: " + label);
        if (seen.Add(target)) targets.Add(new(label, new WeakReference(target, trackResurrection: true)));
    }

    private static PoolTypes CreatePoolTypes()
    {
        Type runner = typeof(SystemRunner), scheduler = typeof(Scheduler);
        Type apply = Nested(typeof(SourceTrackedArchiveJobs), "Apply");
        Type work = Nested(runner, "ComponentRangeWork" + "\u0060" + "3")
            .MakeGenericType(typeof(SaveCounter), typeof(Position), apply);
        Type partitioned = Nested(runner, "PartitionedRanges" + "\u0060" + "1").MakeGenericType(work);
        Type stages = Nested(runner, "QueryStages" + "\u0060" + "3")
            .MakeGenericType(typeof(SaveCounter), typeof(Position), apply);
        Type range = runner.Assembly.GetType("SomeEngine.ECS.Systems.QueryRange", true)!;
        Type sync = Nested(scheduler, "SynchronousRanges" + "\u0060" + "3");
        return new(runner.Assembly.GetType("SomeEngine.ECS.Systems.QueryRanges", true)!,
            Nested(scheduler, "StagedJobData" + "\u0060" + "1").MakeGenericType(stages),
            sync.MakeGenericType(partitioned, range, Nested(scheduler, "MaskRangeClaims")),
            sync.MakeGenericType(partitioned, range, Nested(scheduler, "SequentialRangeClaims")));
    }

    private static PoolObservation[] SnapshotPools(PoolTypes types, string mode, bool disposed)
        => [SnapshotLockedPool(types.QueryRanges, "query-ranges", disposed),
            mode == "schedule" ? SnapshotLockedPool(types.Staged, "staged-job", disposed)
                : new(types.Staged.ToString(), "not-applicable-to-parallel", false, disposed,
                    Environment.CurrentManagedThreadId, null, null, null, null, []),
            SnapshotTlsPool(types.MaskRanges, disposed), SnapshotTlsPool(types.SequentialRanges, disposed)];

    private static PoolObservation SnapshotLockedPool(Type type, string kind, bool disposed)
        => UnderOriginalLock(RequiredStatic(type, "FreeLock"),
            () => SnapshotPool(type, kind, "global; original FreeLock", disposed));

    private static PoolObservation SnapshotTlsPool(Type type, bool disposed)
    {
        if (!Field(type, "s_free").IsDefined(typeof(ThreadStaticAttribute), false)
            || !Field(type, "s_freeCount").IsDefined(typeof(ThreadStaticAttribute), false))
            throw new InvalidOperationException("The expected range pool is no longer thread-static.");
        return SnapshotPool(type, "synchronous-ranges", "calling-thread TLS only; other worker TLS unobserved", disposed);
    }

    private static PoolObservation SnapshotPool(Type type, string kind, string scope, bool disposed)
    {
        int count = (int)RequiredStatic(type, "s_freeCount");
        Array? free = ReadStatic(type, "s_free") as Array;
        int capacity = free?.Length ?? 0;
        long itemCapacity = 0, references = 0;
        var errors = new List<string>();
        if (count < 0 || count > capacity) errors.Add("free-count-outside-backing-capacity");
        for (int i = 0; i < capacity; i++)
        {
            object? entry = free!.GetValue(i);
            if (i < count && entry is null) errors.Add("null-active-free-slot:" + i);
            if (i >= count && entry is not null) errors.Add("non-null-inactive-free-slot:" + i);
            if (entry is null) continue;
            if (kind == "query-ranges")
            {
                if ((int)RequiredField(entry, "Count") != 0) errors.Add("returned-query-ranges-count:" + i);
                if ((bool)RequiredField(entry, "_hasFilters")) errors.Add("returned-query-ranges-filter-flag:" + i);
                Array items = (Array)RequiredField(entry, "Items");
                itemCapacity += items.Length;
                // Scan the full retained array, not Count (which should be zero).
                for (int j = 0; j < items.Length; j++)
                {
                    object item = items.GetValue(j)!;
                    if (ReadField(item, "Chunk") is not null) references++;
                    if (ReadField(item, "Filter") is not null) references++;
                }
            }
            else if (kind == "staged-job")
            {
                if (ReadField(entry, "_scheduler") is not null) references++;
                references += CountReferenceFields(RequiredField(entry, "_job"));
                references += CountReferenceFields(RequiredField(entry, "Handle"));
                if ((int)RequiredField(entry, "_references") != 0) errors.Add("returned-staged-reference-count:" + i);
            }
            else
            {
                if (ReadField(entry, "_items") is not null) references++;
                if (ReadField(entry, "Error") is not null) references++;
                references += CountReferenceFields(RequiredField(entry, "_work"));
                references += CountReferenceFields(RequiredField(entry, "_access"));
                // _count and _epochClaims are reusable scalars, not reference leaks.
            }
        }
        if (references != 0) errors.Add("returned-payload-references:" + references);
        return new(type.ToString(), scope, true, disposed, Environment.CurrentManagedThreadId,
            count, capacity, itemCapacity, references, errors.ToArray());
    }

    private static long CountReferenceFields(object value)
    {
        long references = 0;
        foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Type type = field.FieldType;
            if (type.IsPointer || type.IsByRef || type.IsPrimitive || type.IsEnum) continue;
            object? part = field.GetValue(value);
            if (!type.IsValueType) { if (part is not null) references++; }
            else if (part is not null) references += CountReferenceFields(part);
        }
        return references;
    }

    private static WorkerState SnapshotWorkers(Array workers)
    {
        Type type = typeof(Scheduler).Assembly.GetType("SomeEngine.Job.WorkQueueThread", true)!;
        return UnderOriginalLock<WorkerState>(RequiredStatic(type, "FreeLock"), () =>
        {
            var free = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (object worker in (IEnumerable)RequiredStatic(type, "Free")) free.Add(worker);
            var result = new WorkerObservation[workers.Length];
            for (int i = 0; i < workers.Length; i++)
            {
                object worker = workers.GetValue(i) ?? throw new InvalidOperationException("Missing owned worker.");
                Thread thread = (Thread)RequiredField(worker, "_thread");
                bool retained = free.Contains(worker), alive = thread.IsAlive;
                bool stopped = (bool)RequiredField(worker, "_stop");
                bool queueCleared = ReadField(worker, "_queue") is null;
                // Overflow workers have disposed wait handles; do not inspect them.
                bool? finished = retained ? ((ManualResetEventSlim)RequiredField(worker, "_finished")).IsSet : null;
                bool expected = queueCleared && (retained ? alive && !stopped && finished == true : !alive && stopped);
                result[i] = new(i, thread.ManagedThreadId, retained, alive, queueCleared,
                    stopped, finished, false, expected);
            }
            int limit = (int)(typeof(JobConfiguration).GetProperty("DefaultWorkerCount",
                BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                ?? throw new MissingMemberException("DefaultWorkerCount"));
            return new(result, limit, free.Count);
        });
    }

    private static T UnderOriginalLock<T>(object gate, Func<T> read)
    {
        // Monitor on a boxed Lock is not the original System.Threading.Lock.
        if (gate is System.Threading.Lock typed)
        {
            using (typed.EnterScope()) return read();
        }
        lock (gate) return read();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectOnce()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakObservation[] ObserveWeakTargets(WeakTarget[] targets)
        => targets.Select(value => new WeakObservation(value.Label, value.Reference.IsAlive)).ToArray();

    private static Type Nested(Type owner, string name)
        => owner.GetNestedType(name, BindingFlags.NonPublic) ?? throw new MissingMemberException(owner.FullName, name);

    private static FieldInfo Field(Type owner, string name)
    {
        for (Type? current = owner; current is not null; current = current.BaseType)
            if (current.GetField(name, Members) is { } field) return field;
        throw new MissingFieldException(owner.FullName, name);
    }

    private static object? ReadField(object owner, string name) => Field(owner.GetType(), name).GetValue(owner);
    private static object RequiredField(object owner, string name)
        => ReadField(owner, name) ?? throw new InvalidOperationException("Required field is null: " + name);
    private static object? ReadStatic(Type owner, string name) => Field(owner, name).GetValue(null);
    private static object RequiredStatic(Type owner, string name)
        => ReadStatic(owner, name) ?? throw new InvalidOperationException("Required static field is null: " + name);
    private static object RequiredMember(object owner, string name)
    {
        for (Type? type = owner.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, Members) is { } field)
                return field.GetValue(owner) ?? throw new InvalidOperationException("Required member is null: " + name);
            if (type.GetProperty(name, Members) is { } property)
                return property.GetValue(owner) ?? throw new InvalidOperationException("Required member is null: " + name);
        }
        throw new MissingMemberException(owner.GetType().FullName, name);
    }

    // Separate holding probe: the original resource diagnostic above is unchanged.
    internal static int RunHolding(string[] args)
    {
        if (args.Length != 4 || args[1] != "schedule"
            || !int.TryParse(args[2], out int count) || count < 2)
            throw new ArgumentException("Usage: check-tracked-pool-holding schedule <count >= 2> <new-report.json>");
        string path = Path.GetFullPath(args[3]), artifacts = Path.GetFullPath("artifacts");
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Directory.Exists(artifacts)
            || !string.Equals(Path.GetDirectoryName(path), artifacts, comparison) || File.Exists(path))
            throw new ArgumentException("The report must be a new file directly in the existing artifacts directory.");

        var points = new HoldingPoint[3];
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        long probeStart = GC.GetTotalAllocatedBytes(false);
        _ = ReadHoldingPoint(process, "probe-warmup"); // No engine owner, payload or pool is rented.
        long approximateProbeAllocation = GC.GetTotalAllocatedBytes(false) - probeStart;
        CollectOnce();
        points[0] = ReadHoldingPoint(process, "B-before-business-owners");
        HoldingContext context = ExerciseHolding(count, process, points);
        CollectOnce();
        points[2] = ReadHoldingPoint(process, "R-after-owner-helper-returned");
        ObserveHoldingTargets(context, held: false);

        // Allocate final census/report objects only after R's numeric sample.
        PoolObservation[] finalPools = SnapshotPools(context.Types, "schedule", true);
        HoldingArrayPayload[] finalArrays = CaptureHoldingArrayPayloads(context.Types);
        string[] requiredOwners = ["archive-wrapper", "world", "original-entities", "tracked-jobs",
            "system-runner", "scheduler", "scheduler-work-queue", "owned-worker-array"];
        bool heldOwnersAlive = requiredOwners.All(label =>
            context.Observations.Any(value => value.Label == label && value.HeldAlive));
        bool releasedTargetsCollected = context.Observations.All(value => !value.ReleasedAlive);
        bool snapshotsValid = points.All(value => value.GcGeneration == GC.MaxGeneration
            && !value.GcConcurrent && value.GcHeapSizeBytes >= value.GcFragmentedBytes)
            && points[0].GcIndex > 0 && points[1].GcIndex > points[0].GcIndex
            && points[2].GcIndex > points[1].GcIndex;
        bool freeSlotsCleared = finalPools.All(value => value.Violations.Length == 0
            && value.PayloadReferences == 0);
        bool poolsObserved = finalPools[0].FreeCount > 0 && finalPools[1].FreeCount > 0;
        bool passed = heldOwnersAlive && releasedTargetsCollected && snapshotsValid
            && freeSlotsCleared && poolsObserved && context.WorldDisposedCheckPassed;

        var report = new
        {
            Workload = "tracked-job-schedule-holding", InitialEntities = count, GrowthEntities = 1000,
            VerifiedRuns = 2, WorkerCount = 3,
            Framework = RuntimeInformation.FrameworkDescription,
            RuntimeVersion = Environment.Version.ToString(), Environment.ProcessorCount,
            ServerGC = GCSettings.IsServerGC,
            ApproximateMemoryProbeWarmupAllocatedBytes = approximateProbeAllocation,
            MemoryProbeWarmupWindow = "ReadHoldingPoint warmup only; excludes the preallocated points/process and later weak/pool/reflection-cache initialization. B-relative differences are not pure engine costs; H/R remain whole-process observations.",
            ProbeAllocationIsAcceptanceThreshold = false,
            Collection = "B/H/R each use exactly GC/WaitForPendingFinalizers/GC, full blocking compacting requests. H keeps actual owners alive after sampling; R follows the NoInlining owner helper's return.",
            ObserverBoundary = "Weak targets and H census are prepared in a separate NoInlining helper. Its strong seen set and reflected boxes do not survive into H. H/R reuse the same numeric and weak-observation buffers; serialization occurs after R.",
            ManagedAccounting = "GetGCMemoryInfo(FullBlocking) is the latest full-blocking GC's accounting, copied to scalars. GetTotalMemory(false) is a managed-heap estimate. These are not sums of the named weak targets.",
            ProcessAccounting = "Self Process.Refresh/private bytes/working set are sampled after GC scalars. They are whole-process observations, include runtime/observer/native costs, and are not simultaneous with the last-GC accounting.",
            CapacityAccounting = "Deduplicated observed array data areas only: reference slots use IntPtr.Size, value elements use runtime Unsafe.SizeOf<T>. No array headers, wrapper bodies, transitive/shared object graph or unobserved worker TLS are included.",
            DifferenceAccounting = "Signed whole-process metric differences, not exclusive pool released/retained bytes. Do not add process, managed and capacity figures together.",
            Points = points,
            HMinusB = HoldingDifference(points[1], points[0]),
            RMinusB = HoldingDifference(points[2], points[0]),
            HMinusR = HoldingDifference(points[1], points[2]),
            context.HeldPools, context.HeldArrays, FinalPools = finalPools, FinalArrays = finalArrays,
            WeakTargets = context.Observations,
            HeldRequiredOwnersAlive = heldOwnersAlive, ReleasedBusinessTargetsCollected = releasedTargetsCollected,
            FullBlockingGcSnapshotsValid = snapshotsValid, FinalVisibleFreeSlotsCleared = freeSlotsCleared,
            ExpectedActualPoolsObserved = poolsObserved, context.WorldDisposedCheckPassed,
            SchedulerDisposeReturned = true,
            WorkerDispositionMeasuredInThisEntry = false,
            WorldReleasedWhileSchedulerAliveMeasured = false,
            OtherWorkerTlsHoldingBytes = (long?)null,
            PoolObjectHeadersAndWrapperBytes = (long?)null,
            ExclusivePoolRetainedBytes = (long?)null,
            HoldingDiagnosticChecksPassed = passed,
            OverallResourcePassed = (bool?)null, TimingAcceptanceClaimed = false,
        };
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Tracked holding diagnostics: schedule, verified runs=2, growth=1000, "
            + $"targets={context.Observations.Length}, H owners alive={heldOwnersAlive}, "
            + $"R targets collected={releasedTargetsCollected}, full-GC samples={snapshotsValid}, "
            + $"visible free slots cleared={freeSlotsCleared}, actual pools observed={poolsObserved}. "
            + "B/H/R accounting is not exclusive pool bytes; OverallResourcePassed=null; no timing acceptance.");
        if (!passed) throw new InvalidOperationException("Tracked holding diagnostic checks failed; see the report. No byte threshold was applied.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HoldingContext ExerciseHolding(int count, System.Diagnostics.Process process, HoldingPoint[] points)
    {
        Scheduler? scheduler = null;
        SourceFullArchiveWorld? world = null;
        TrackedArchiveJobs? jobs = null;
        HoldingContext? context = null;
        try
        {
            scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
            ArchiveComparison.Library library = ArchiveComparison.Libraries(count, true, scheduler)
                .Single(value => value.Name == "SomeEngine");
            world = (SourceFullArchiveWorld)library.Create();
            jobs = world.ConnectTrackedJobs(scheduler);
            if (world.World.Execution is not WorldJobs)
                throw new InvalidOperationException("The holding fixture requires connected WorldJobs.");
            jobs.Run("schedule"); jobs.Check();
            world.Grow(); jobs.RecordGrowth();
            if (world.GrowthEntities.Count != 1000)
                throw new InvalidOperationException("The canonical growth must add exactly 1000 entities.");
            jobs.Run("schedule"); jobs.Check();
            context = CaptureHoldingContext(world, jobs, scheduler);
            CollectOnce();
            points[1] = ReadHoldingPoint(process, "H-live-owners-after-growth");
            ObserveHoldingTargets(context, held: true);
            GC.KeepAlive(world);
            GC.KeepAlive(jobs);
            GC.KeepAlive(scheduler);
        }
        finally
        {
            try { jobs?.Dispose(); }
            finally
            {
                try
                {
                    if (world is not null)
                    {
                        world.Dispose();
                        world.CheckDisposed();
                        if (context is not null) context.WorldDisposedCheckPassed = true;
                    }
                }
                finally { scheduler?.Dispose(); }
            }
        }
        return context ?? throw new InvalidOperationException("No holding observation completed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HoldingContext CaptureHoldingContext(SourceFullArchiveWorld world,
        TrackedArchiveJobs jobs, Scheduler scheduler)
    {
        var targets = new List<WeakTarget>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CaptureTargets("held", world, jobs, scheduler, targets, seen);
        object queue = RequiredField(scheduler, "_workQueue");
        AddTarget(targets, seen, "scheduler-work-queue", queue);
        AddTarget(targets, seen, "owned-worker-array", RequiredField(queue, "_workers"));
        PoolTypes types = CreatePoolTypes();
        WeakTarget[] weak = targets.ToArray();
        var observations = new HoldingWeakState[weak.Length];
        for (int i = 0; i < weak.Length; i++) observations[i].Label = weak[i].Label;
        return new(weak, observations, types, SnapshotPools(types, "schedule", false),
            CaptureHoldingArrayPayloads(types));
        // Neither seen nor any actual target/array is returned as a strong reference.
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ObserveHoldingTargets(HoldingContext context, bool held)
    {
        for (int i = 0; i < context.Targets.Length; i++)
        {
            bool alive = context.Targets[i].Reference.IsAlive;
            if (held) context.Observations[i].HeldAlive = alive;
            else context.Observations[i].ReleasedAlive = alive;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HoldingPoint ReadHoldingPoint(System.Diagnostics.Process process, string phase)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        long managed = GC.GetTotalMemory(false);
        long index = info.Index, heap = info.HeapSizeBytes, fragmented = info.FragmentedBytes;
        long committed = info.TotalCommittedBytes;
        int generation = info.Generation;
        bool concurrent = info.Concurrent, compacted = info.Compacted;
        process.Refresh();
        long privateBytes = process.PrivateMemorySize64, workingSet = process.WorkingSet64;
        return new(phase, index, generation, concurrent, compacted, heap, fragmented,
            committed, managed, privateBytes, workingSet);
    }

    private static HoldingDelta HoldingDifference(HoldingPoint left, HoldingPoint right)
        => new(left.ManagedEstimatedBytes - right.ManagedEstimatedBytes,
            left.GcHeapSizeBytes - right.GcHeapSizeBytes,
            left.GcFragmentedBytes - right.GcFragmentedBytes,
            left.GcCommittedBytes - right.GcCommittedBytes,
            left.ProcessPrivateBytes - right.ProcessPrivateBytes,
            left.ProcessWorkingSetBytes - right.ProcessWorkingSetBytes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HoldingArrayPayload[] CaptureHoldingArrayPayloads(PoolTypes types)
    {
        var seen = new List<Array>();
        return [
            UnderOriginalLock<HoldingArrayPayload>(RequiredStatic(types.QueryRanges, "FreeLock"),
                () => HoldingArrayPayloadFor(types.QueryRanges, "global; original FreeLock", true, seen)),
            UnderOriginalLock<HoldingArrayPayload>(RequiredStatic(types.Staged, "FreeLock"),
                () => HoldingArrayPayloadFor(types.Staged, "global; original FreeLock", false, seen)),
            HoldingArrayPayloadFor(types.MaskRanges, "calling-thread TLS only", false, seen),
            HoldingArrayPayloadFor(types.SequentialRanges, "calling-thread TLS only", false, seen)];
    }

    private static HoldingArrayPayload HoldingArrayPayloadFor(Type type, string scope, bool queryRanges, List<Array> seen)
    {
        int arrays = 0, duplicates = 0;
        long references = 0, values = 0;
        Array? free = ReadStatic(type, "s_free") as Array;
        if (free is not null)
        {
            AddArray(free);
            if (queryRanges)
                for (int i = 0; i < free.Length; i++)
                    if (free.GetValue(i) is { } entry) AddArray((Array)RequiredField(entry, "Items"));
        }
        return new(type.ToString(), scope, Environment.CurrentManagedThreadId, arrays,
            duplicates, references, values, checked(references + values));

        void AddArray(Array array)
        {
            if (seen.Any(previous => ReferenceEquals(previous, array))) { duplicates++; return; }
            seen.Add(array);
            arrays++;
            Type element = array.GetType().GetElementType()!;
            if (!element.IsValueType) references += checked((long)array.Length * IntPtr.Size);
            else
            {
                MethodInfo sizeMethod = typeof(TrackedPoolResourceChecks).GetMethod(nameof(HoldingElementSize),
                    BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(element);
                int stride = (int)sizeMethod.Invoke(null, null)!;
                values += checked((long)array.Length * stride);
            }
        }
    }

    private static int HoldingElementSize<T>() => Unsafe.SizeOf<T>();

    private readonly record struct HoldingPoint(string Phase, long GcIndex, int GcGeneration,
        bool GcConcurrent, bool GcCompacted, long GcHeapSizeBytes, long GcFragmentedBytes,
        long GcCommittedBytes, long ManagedEstimatedBytes, long ProcessPrivateBytes, long ProcessWorkingSetBytes);
    private readonly record struct HoldingDelta(long ManagedEstimatedBytes, long GcHeapSizeBytes,
        long GcFragmentedBytes, long GcCommittedBytes, long ProcessPrivateBytes, long ProcessWorkingSetBytes);
    private sealed record HoldingArrayPayload(string ClosedType, string Scope, int ObservingManagedThreadId,
        int UniqueArrays, int DuplicateArrayReferencesExcluded, long ReferenceArraySlotBytes,
        long ValueArrayElementBytes, long ObservedArrayPayloadBytes);
    private struct HoldingWeakState
    {
        public string Label { get; set; }
        public bool HeldAlive { get; set; }
        public bool ReleasedAlive { get; set; }
    }
    private sealed class HoldingContext(WeakTarget[] targets, HoldingWeakState[] observations,
        PoolTypes types, PoolObservation[] heldPools, HoldingArrayPayload[] heldArrays)
    {
        internal readonly WeakTarget[] Targets = targets;
        internal readonly HoldingWeakState[] Observations = observations;
        internal readonly PoolTypes Types = types;
        internal readonly PoolObservation[] HeldPools = heldPools;
        internal readonly HoldingArrayPayload[] HeldArrays = heldArrays;
        internal bool WorldDisposedCheckPassed;
    }

    private sealed record PoolTypes(Type QueryRanges, Type Staged, Type MaskRanges, Type SequentialRanges);
    private sealed record WeakTarget(string Label, WeakReference Reference);
    private sealed record WeakObservation(string Label, bool Alive);
    private sealed record PhaseObservation(string Phase, long ApproximateProcessAllocatedBytes, PoolObservation[] Pools);
    private sealed record PoolObservation(string ClosedType, string Scope, bool Applicable,
        bool SchedulerDisposeReturned, int ObservingManagedThreadId, int? FreeCount,
        int? BackingArrayCapacity, long? RetainedItemCapacity, long? PayloadReferences, string[] Violations);
    private sealed record WorkerObservation(int OwnedIndex, int ManagedThreadId, bool InStaticFree,
        bool ThreadAlive, bool QueueCleared, bool StopRequested, bool? FinishedForRetainedWorker,
        bool JoinCallInstrumented, bool ExpectedReturnedState);
    private sealed record WorkerState(WorkerObservation[] Workers, int CacheLimit, int FreeCount);
    private sealed record LifecycleResult(WeakTarget[] Targets, PhaseObservation[] Phases,
        PoolObservation[] FinalPools, WorkerObservation[] Workers, int WorkerCacheLimit,
        int WorkerFreeCount, bool WorldDisposedCheckPassed);
}
