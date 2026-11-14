namespace SomeEngine.ECS.Systems;

using SomeEngine.Diagnostics;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

public sealed partial class SystemGroup<TContext>
{
    /// <summary>
    /// Adds a generated chunk job as a system. At its turn the group schedules it without waiting, after earlier job
    /// systems whose declared accesses conflict with it. The command batch plays back and the system's versions are
    /// recorded when the job completes.
    /// </summary>
    public int AddJob<TAction>(in TAction job, Query? query = null, CommandBatch? commands = null, bool parallel = true)
        where TAction : struct, IChunkAction
    {
        World world = JobWorld();
        return AddNode(new ChunkJobNode<TAction>(world, _rangeRows, query ?? world.Query(ChunkActionTypes<TAction>.Query),
            job, commands, parallel));
    }

    /// <summary>Adds a job with declared World accesses as a system; it is scheduled at its turn like a chunk job.</summary>
    public int AddJob<TJob>(in TJob job, SystemAccesses? accesses = null, bool structureWrite = false)
        where TJob : struct, IJob
        => AddNode(new WorldJobNode<TJob>(JobWorld(), job, accesses, structureWrite));

    private World JobWorld() => _driver.World is { Execution: WorldJobs } world ? world
        : throw new InvalidOperationException("Job systems need a driver World connected through WorldJobs.");

    private Scheduler JobScheduler => ((WorldJobs)_driver.World!.Execution!).Scheduler;

    /// <summary>
    /// Completes after the previous update's job work whatever its outcome. Its failure was already reported by that
    /// update's handle and the systems' own work, so the next update and Complete do not inherit it.
    /// </summary>
    private JobHandle AfterPreviousUpdate() => _previousUpdate.IsCompleted ? default
        : JobScheduler.ScheduleFinally(new UpdateBoundary(), dependency: _previousUpdate, options: default);

    private readonly struct UpdateBoundary : IJob { public void Execute() { } }

    // A staged job registers its exact accesses only when its stage runs, so submission order alone does not order
    // conflicting systems. The group orders job systems by their declared accesses instead.
    private static bool Conflicts(JobSystemNode first, JobSystemNode second)
    {
        if (first.ChangesStructure || second.ChangesStructure) return true;
        foreach (WorldValueAccess a in first.Values)
            foreach (WorldValueAccess b in second.Values)
                if (a.Type == b.Type && (a.Write || b.Write) && (a.Kind == b.Kind || ReadsChangesOf(a, b) || ReadsChangesOf(b, a)))
                    return true;
        return false;
    }

    private static bool ReadsChangesOf(in WorldValueAccess changes, in WorldValueAccess value)
        => changes.Kind == WorldValueKind.Changes && value.Kind == WorldValueKind.Table && value.Write && value.Tracked;

    /// <summary>A system that schedules a job at its turn; the job's completion stage writes the system's versions.</summary>
    private abstract unsafe class JobSystemNode(TraceSite trace) : SystemNode<TContext>(&NoUpdate, trace)
    {
        internal readonly List<WorldValueAccess> Values = [];
        internal bool ChangesStructure;
        private static void NoUpdate(SystemNode<TContext> node, ref TContext context) { }
        public override void OnCreate(ref TContext context) { }
        public override void OnDestroy(ref TContext context) { }
        /// <summary>Collects the World values the next run declares.</summary>
        internal abstract void CollectValues();
        internal abstract JobHandle Schedule(SystemState state, JobHandle dependency);
    }

    private sealed class ChunkJobNode<TAction>(World world, int rangeRows, Query query, TAction job,
        CommandBatch? commands, bool parallel) : JobSystemNode(Trace) where TAction : struct, IChunkAction
    {
        private static readonly TraceSite Trace = new("system", typeof(TAction).ToString(), detail: true);
        private readonly SystemRunner _runner = new(world, rangeRows);
        public override Type SystemType => typeof(TAction);

        internal override void CollectValues()
        {
            Values.Clear();
            ChangesStructure = commands is not null;
            foreach (ChunkAccess component in ChunkActionTypes<TAction>.Components)
                Values.Add(new(component.Type, WorldValueKind.Table, component.WritesValue, component.Tracked, component.Structural));
            foreach (int type in query.Description.Definition.AccessTypes)
                Values.Add(new(type, WorldValueKind.Table, false, false, ComponentTypes.Get(type).UsesStructureWrites));
            for (int type = 0; type < ComponentTypes.Count; type++)
                if (query.Description.Changed.Has(type)) Values.Add(new(type, WorldValueKind.Changes, false, false));
            if (TAction.AdditionalAccesses is { } additional) Values.AddRange(additional.Values);
        }

        internal override JobHandle Schedule(SystemState state, JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle rows;
            try { rows = _runner.Schedule(query, job, dependency, parallel, state); }
            catch { commands?.Clear(); jobs.EndUse(); throw; }
            return jobs.Scheduler.ScheduleStages(new SystemCompletion(world, jobs, state, commands), rows);
        }
    }

    private sealed class WorldJobNode<TJob>(World world, TJob job, SystemAccesses? accesses, bool structureWrite)
        : JobSystemNode(Trace) where TJob : struct, IJob
    {
        private static readonly TraceSite Trace = new("system", typeof(TJob).ToString(), detail: true);
        public override Type SystemType => typeof(TJob);

        internal override void CollectValues()
        {
            Values.Clear();
            ChangesStructure = structureWrite;
            if (accesses is not null) Values.AddRange(accesses.Values);
        }

        internal override JobHandle Schedule(SystemState state, JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle work;
            try
            {
                JobResourceAccess[] declarations = accesses is null
                    ? structureWrite ? jobs.StructureWrite : jobs.StructureRead
                    : jobs.CollectAccess(accesses, structureWrite);
                work = jobs.Scheduler.Schedule(new RunSystemJob(world, state, job), declarations, dependency: dependency);
            }
            catch { jobs.EndUse(); throw; }
            return jobs.Scheduler.ScheduleStages(new SystemCompletion(world, jobs, state, null), work);
        }

        private readonly struct RunSystemJob(World world, SystemState state, TJob job) : IJob
        {
            public void Execute()
            {
                state.CurrentSystemVersion = world.AdvanceVersion();
                using World.BatchVersionScope version = world.BeginBatchVersion(state.CurrentSystemVersion);
                job.Execute();
            }
        }
    }

    private readonly struct SystemCompletion(World world, WorldJobs jobs, SystemState state, CommandBatch? commands) : IStagedJob
    {
        public static int StageCount => 1;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (commands is not null) jobs.AddStructureAccess(true, ref accesses);
        }
        public void Execute()
        {
            using WorldAccess access = commands is not null ? world.OpenStructure() : default;
            commands?.Playback();
            state.LastSystemVersion = state.CurrentSystemVersion;
        }
        public void Release()
        {
            try { commands?.Clear(); }
            finally { jobs.EndUse(); }
        }
    }
}
