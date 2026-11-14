using SomeEngine.ECS.Commands;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class SystemState
{
    public ulong LastSystemVersion { get; internal set; }
    public ulong CurrentSystemVersion { get; internal set; }
    public int Index { get; internal set; }
    public bool Created { get; internal set; }
    public bool Enabled { get; internal set; } = true;
}

/// <summary>
/// Systems are jobs on the World's scheduler. Each run submits every system with its declared World accesses;
/// the scheduler orders systems whose accesses conflict in submission order. Submission follows registration
/// order, adjusted only by explicit <see cref="After"/> edges.
/// </summary>
public sealed partial class SystemGroup(World world, int rangeRows = 1024) : ISystemVersions
{
    private readonly SystemRunner _runner = new(world, rangeRows);
    private readonly Scheduler _scheduler = ((WorldJobs)world.Execution!).Scheduler;
    private readonly List<SystemEntry> _systems = [];
    private int[] _order = [];
    private JobHandle[] _handles = [];
    private JobHandle[] _dependencies = [];
    private bool _orderChanged;
    private JobHandle _previousRun;

    public int VersionCount => _systems.Count;

    public void Complete() => _previousRun.Complete();
    public void CopyVersions(Span<ulong> versions)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            SystemState state = _systems[i].State;
            versions[i * 2] = state.LastSystemVersion;
            versions[i * 2 + 1] = state.CurrentSystemVersion;
        }
    }
    public void RestoreVersions(ReadOnlySpan<ulong> versions)
    {
        for (int i = 0; i < _systems.Count; i++)
        {
            SystemState state = _systems[i].State;
            state.LastSystemVersion = versions[i * 2];
            state.CurrentSystemVersion = versions[i * 2 + 1];
        }
    }

    /// <summary>Runs <paramref name="system"/> after <paramref name="predecessor"/> completes, whatever their accesses.</summary>
    public void After(SystemState system, SystemState predecessor)
    {
        _systems[system.Index].ExplicitPredecessors.Add(predecessor.Index);
        _orderChanged = true;
    }

    public SystemState AddJob<TJob>(in TJob job, SystemAccesses? accesses = null, bool structureWrite = false)
        where TJob : struct, IJob
        => Register(new ScheduledSystemJob<TJob>(world, _scheduler, job, accesses, structureWrite));

    private SystemState Register(SystemEntry system)
    {
        system.State.Index = _systems.Count;
        _systems.Add(system);
        _orderChanged = true;
        return system.State;
    }

    public void Execute(JobHandle dependency = default) => Schedule(dependency).Complete();

    public JobHandle Schedule(JobHandle dependency = default)
    {
        if (_orderChanged) OrderSubmissions();
        JobHandle previous = _previousRun.IsCompleted ? default
            : _scheduler.ScheduleFinally(new GroupBoundary(), dependency: _previousRun, options: default);
        JobHandle root = _scheduler.CombineDependencies([dependency, previous]);
        foreach (int i in _order)
        {
            List<int> predecessors = _systems[i].ExplicitPredecessors;
            JobHandle before = root;
            if (predecessors.Count != 0)
            {
                Span<JobHandle> dependencies = _dependencies.AsSpan(0, predecessors.Count + 1);
                dependencies[0] = root;
                for (int j = 0; j < predecessors.Count; j++) dependencies[j + 1] = _handles[predecessors[j]];
                before = _scheduler.CombineDependencies(dependencies);
            }
            _handles[i] = _systems[i].Schedule(before);
        }
        return _previousRun = _scheduler.CombineDependencies(_handles);
    }

    // Registration order, moved only where an explicit After edge requires a later system first.
    private void OrderSubmissions()
    {
        _handles = new JobHandle[_systems.Count];
        _dependencies = new JobHandle[_systems.Count + 1];
        _order = new int[_systems.Count];
        int written = 0;
        bool[] submitted = new bool[_systems.Count];
        while (written < _systems.Count)
        {
            int start = written;
            for (int i = 0; i < _systems.Count; i++)
            {
                if (submitted[i]) continue;
                bool ready = true;
                foreach (int before in _systems[i].ExplicitPredecessors) if (!submitted[before]) { ready = false; break; }
                if (ready) { submitted[i] = true; _order[written++] = i; }
            }
            if (start == written) throw new InvalidOperationException("Explicit system order contains a cycle.");
        }
        _orderChanged = false;
    }

    private readonly struct GroupBoundary : IJob { public void Execute() { } }

    private sealed class ScheduledSystemJob<TJob>(World world, Scheduler scheduler, TJob job,
        SystemAccesses? accesses, bool structureWrite) : SystemEntry
        where TJob : struct, IJob
    {
        internal override JobHandle Schedule(JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle work;
            try
            {
                JobResourceAccess[] declarations = accesses is null
                    ? structureWrite ? jobs.StructureWrite : jobs.StructureRead
                    : jobs.CollectAccess(accesses, structureWrite);
                work = scheduler.Schedule(new RunSystemJob<TJob>(world, State, job), declarations, dependency: dependency);
            }
            catch { jobs.EndUse(); throw; }
            return scheduler.ScheduleStages(new SystemCompletion(world, jobs, State, null), work);
        }
    }

    private readonly struct RunSystemJob<TJob>(World world, SystemState state, TJob job) : IJob
        where TJob : struct, IJob
    {
        public void Execute()
        {
            state.CurrentSystemVersion = world.AdvanceVersion();
            using World.BatchVersionScope version = world.BeginBatchVersion(state.CurrentSystemVersion);
            job.Execute();
        }
    }

    private abstract class SystemEntry
    {
        internal readonly SystemState State = new();
        internal readonly List<int> ExplicitPredecessors = [];
        internal abstract JobHandle Schedule(JobHandle dependency);
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
