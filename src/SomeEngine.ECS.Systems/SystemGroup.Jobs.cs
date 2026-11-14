namespace SomeEngine.ECS.Systems;

using SomeEngine.Diagnostics;
using SomeEngine.ECS.Commands;
using SomeEngine.Job;

public sealed partial class SystemGroup<TContext>
{
    /// <summary>
    /// Adds a generated chunk job as a system. At its turn the group submits it without waiting; the scheduler orders it
    /// after earlier submissions whose accesses conflict with it. The command batch plays back and the system's versions
    /// are recorded when the job completes.
    /// </summary>
    public int AddJob<TAction>(in TAction job, Query? query = null, CommandBatch? commands = null, bool parallel = true)
        where TAction : struct, IChunkAction
    {
        World world = JobWorld();
        return AddNode(new ChunkJobNode<TAction>(world, _rangeRows, query ?? world.Query(ChunkActionTypes<TAction>.Query),
            job, commands, parallel));
    }

    /// <summary>Adds a job with declared World accesses as a system; it is submitted at its turn like a chunk job.</summary>
    public int AddJob<TJob>(in TJob job, SystemAccesses? accesses = null, bool structureWrite = false)
        where TJob : struct, IJob
        => AddNode(new WorldJobNode<TJob>(JobWorld(), job, accesses, structureWrite));

    private World JobWorld() => _driver.World is { Execution: WorldJobs } world ? world
        : throw new InvalidOperationException("Job systems need a driver World connected through WorldJobs.");

    private Scheduler JobScheduler => ((WorldJobs)_driver.World!.Execution!).Scheduler;

    /// <summary>
    /// Completes after the previous update's job work whatever its outcome. That update's handle and the systems' own
    /// work already report its failure, so Complete does not report it again.
    /// </summary>
    private JobHandle AfterPreviousUpdate() => _previousUpdate.IsCompleted ? default
        : JobScheduler.ScheduleFinally(new RunBoundary(), dependency: _previousUpdate, options: default);

    private readonly struct RunBoundary : IJob { public void Execute() { } }

    /// <summary>
    /// A system that submits a job at its turn. Its next run starts after the previous one, so the two never touch the
    /// system's versions together; the job's completion stage writes those versions.
    /// </summary>
    private abstract unsafe class JobSystemNode(TraceSite trace) : SystemNode<TContext>(&NoUpdate, trace)
    {
        private JobHandle _previous;
        private static void NoUpdate(SystemNode<TContext> node, ref TContext context) { }
        public override void OnCreate(ref TContext context) { }
        public override void OnDestroy(ref TContext context) { }

        internal JobHandle Schedule(SystemState state, Scheduler scheduler)
        {
            // A failed previous run is reported by its own handle; the next run does not inherit it.
            JobHandle previous = _previous.IsCompleted ? default
                : scheduler.ScheduleFinally(new RunBoundary(), dependency: _previous, options: default);
            return _previous = Schedule(state, previous);
        }

        protected abstract JobHandle Schedule(SystemState state, JobHandle previous);
    }

    private sealed class ChunkJobNode<TAction>(World world, int rangeRows, Query query, TAction job,
        CommandBatch? commands, bool parallel) : JobSystemNode(Trace) where TAction : struct, IChunkAction
    {
        private static readonly TraceSite Trace = new("system", typeof(TAction).ToString(), detail: true);
        private readonly SystemRunner _runner = new(world, rangeRows);
        public override Type SystemType => typeof(TAction);

        protected override JobHandle Schedule(SystemState state, JobHandle previous)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle rows;
            try { rows = _runner.Schedule(query, job, previous, parallel, state); }
            catch { commands?.Clear(); jobs.EndUse(); throw; }
            return jobs.Scheduler.ScheduleStages(new SystemCompletion(world, jobs, state, commands), rows);
        }
    }

    private sealed class WorldJobNode<TJob>(World world, TJob job, SystemAccesses? accesses, bool structureWrite)
        : JobSystemNode(Trace) where TJob : struct, IJob
    {
        private static readonly TraceSite Trace = new("system", typeof(TJob).ToString(), detail: true);
        public override Type SystemType => typeof(TJob);

        protected override JobHandle Schedule(SystemState state, JobHandle previous)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle work;
            try
            {
                JobResourceAccess[] declarations = accesses is null
                    ? structureWrite ? jobs.StructureWrite : jobs.StructureRead
                    : jobs.CollectAccess(accesses, structureWrite);
                work = jobs.Scheduler.Schedule(new RunSystemJob(world, state, job), declarations, dependency: previous);
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
        // Playback is a structure write; reserving it at submission orders every later submission after it.
        public void Reserve(ref AccessBuilder<JobResourceAccess> accesses)
        {
            if (commands is not null) jobs.AddStructureAccess(true, ref accesses);
        }
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
