using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Storage;
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

public sealed partial class SystemGroup(World world, Scheduler scheduler, int rangeRows = 1024) : ISystemVersions
{
    private readonly SystemRunner _runner = new(world, scheduler, rangeRows);
    private readonly List<SystemEntry> _systems = [];
    private int[][] _predecessors = [];
    private JobHandle[] _handles = [];
    private JobHandle[] _dependencies = [];
    private int[] _order = [];
    private bool _changed;
    private JobHandle _previousRun;

    public int GraphBuildCount { get; private set; }
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

    public SystemState Add<T, TAction>(Query query, in TAction action,
        SystemAccesses? accesses = null, CommandBatch? commands = null, bool parallel = true)
        where T : struct where TAction : struct, IComponentAction<T>
        => Add<T, T, SystemRunner.SingleAction<T, TAction>>(query, new(action), accesses, commands, parallel);

    public SystemState Add<T1, T2, TAction>(Query query, in TAction action,
        SystemAccesses? accesses = null, CommandBatch? commands = null,
        bool parallel = true)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        var system = new ComponentSystem<T1, T2, TAction>(_runner, world, scheduler, query,
            action, accesses, commands, parallel);
        system.State.Index = _systems.Count;
        _systems.Add(system);
        _changed = true;
        return system.State;
    }

    public SystemState Add<T1, T2, T3, TAction>(Query query, in TAction action,
        SystemAccesses? accesses = null, CommandBatch? commands = null, bool parallel = true)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        var system = new ComponentSystem<T1, T2, T3, TAction>(_runner, world, scheduler, query,
            action, accesses, commands, parallel);
        system.State.Index = _systems.Count;
        _systems.Add(system);
        _changed = true;
        return system.State;
    }

    public void After(SystemState system, SystemState predecessor)
    {
        _systems[system.Index].ExplicitPredecessors.Add(predecessor.Index);
        _changed = true;
    }

    public SystemState AddJob<TJob>(in TJob job, SystemAccesses? accesses = null, bool structureWrite = false)
        where TJob : struct, IJob
    {
        var system = new ScheduledSystemJob<TJob>(world, scheduler, job, accesses, structureWrite);
        system.State.Index = _systems.Count;
        _systems.Add(system);
        _changed = true;
        return system.State;
    }

    public void Execute(JobHandle dependency = default) => Schedule(dependency).Complete();

    public JobHandle Schedule(JobHandle dependency = default)
    {
        foreach (SystemEntry system in _systems)
            if (system.DeclarationVersion != (system.Accesses?.Revision ?? 0)) _changed = true;
        if (_changed) BuildGraph();
        JobHandle previous = _previousRun.IsCompleted ? default
            : scheduler.ScheduleFinally(new GroupBoundary(), default, _previousRun);
        JobHandle root = scheduler.CombineDependencies([dependency, previous]);
        foreach (int i in _order)
        {
            int[] predecessors = _predecessors[i];
            JobHandle before = root;
            if (predecessors.Length != 0)
            {
                Span<JobHandle> dependencies = _dependencies.AsSpan(0, predecessors.Length + 1);
                dependencies[0] = root;
                for (int j = 0; j < predecessors.Length; j++) dependencies[j + 1] = _handles[predecessors[j]];
                before = scheduler.CombineDependencies(dependencies);
            }
            _handles[i] = _systems[i].Schedule(before);
        }
        return _previousRun = scheduler.CombineDependencies(_handles);
    }

    private void BuildGraph()
    {
        _predecessors = new int[_systems.Count][];
        _handles = new JobHandle[_systems.Count];
        _dependencies = new JobHandle[_systems.Count + 1];
        _order = new int[_systems.Count];
        foreach (SystemEntry system in _systems) system.RebuildDeclarations();
        for (int i = 0; i < _systems.Count; i++)
        {
            SystemEntry system = _systems[i];
            List<int> before = new(system.ExplicitPredecessors);
            for (int j = 0; j < i; j++)
                if (system.Conflicts(_systems[j]) && !_systems[j].ExplicitPredecessors.Contains(i) && !before.Contains(j)) before.Add(j);
            _predecessors[i] = before.ToArray();
        }
        int written = 0;
        bool[] emitted = new bool[_systems.Count];
        while (written < _systems.Count)
        {
            int start = written;
            for (int i = 0; i < _systems.Count; i++)
            {
                if (emitted[i]) continue;
                bool ready = true;
                foreach (int before in _predecessors[i]) if (!emitted[before]) { ready = false; break; }
                if (ready) { emitted[i] = true; _order[written++] = i; }
            }
            if (start == written) throw new InvalidOperationException("System dependencies contain a cycle.");
        }
        _changed = false;
        GraphBuildCount++;
    }

    private readonly struct GroupBoundary : IJob { public void Execute() { } }

    private sealed class ScheduledSystemJob<TJob>(World world, Scheduler scheduler, TJob job,
        SystemAccesses? accesses, bool structureWrite) : SystemEntry(accesses, structureWrite)
        where TJob : struct, IJob
    {
        protected override void AddComponents() { }

        internal override JobHandle Schedule(JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle work;
            try
            {
                JobResourceAccess[] declarations = Accesses is null
                    ? ChangesStructure ? jobs.StructureWrite : jobs.StructureRead
                    : jobs.CollectAccess(Accesses, ChangesStructure);
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

    private abstract class SystemEntry(SystemAccesses? accesses, bool changesStructure)
    {
        internal readonly SystemState State = new();
        internal readonly List<int> ExplicitPredecessors = [];
        internal readonly List<WorldValueAccess> Values = [];
        internal readonly SystemAccesses? Accesses = accesses;
        internal int DeclarationVersion;
        protected abstract void AddComponents();
        internal abstract JobHandle Schedule(JobHandle dependency);

        protected void AddQueryReads(Query query, int first, int second, int third = -1)
        {
            foreach (int type in query.Description.Definition.AccessTypes)
                if (type != first && type != second && type != third)
                {
                    ComponentType component = ComponentTypes.Get(type);
                    Values.Add(new(type, WorldValueKind.Table, false, false, component.UsesStructureWrites));
                }
        }

        internal void RebuildDeclarations()
        {
            Values.Clear();
            AddComponents();
            if (Accesses is not null) Values.AddRange(Accesses.Values);
            DeclarationVersion = Accesses?.Revision ?? 0;
        }

        internal bool Conflicts(SystemEntry other)
        {
            if (changesStructure || other.ChangesStructure) return true;
            foreach (WorldValueAccess first in Values)
                foreach (WorldValueAccess second in other.Values)
                    if (first.Kind == second.Kind && first.Type == second.Type && (first.Write || second.Write)) return true;
            return false;
        }

        protected bool ChangesStructure => changesStructure;
    }

    private sealed class ComponentSystem<T1, T2, TAction>(SystemRunner runner, World world, Scheduler scheduler,
        Query query, TAction action, SystemAccesses? accesses, CommandBatch? commands,
        bool parallel) : SystemEntry(accesses, commands is not null)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        protected override void AddComponents()
        {
            Values.Add(new(ComponentType<T1>.TypeId, WorldValueKind.Table, true, ComponentType<T1>.TracksWritesType));
            Values.Add(new(ComponentType<T2>.TypeId, WorldValueKind.Table, false, ComponentType<T2>.TracksWritesType));
            AddQueryReads(query, ComponentType<T1>.TypeId, ComponentType<T2>.TypeId);
            for (int type = 0; type < ComponentTypes.Count; type++)
                if (query.Description.Changed.Has(type)) Values.Add(new(type, WorldValueKind.Table, false, true));
        }

        internal override JobHandle Schedule(JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle rows;
            try { rows = runner.Schedule<T1, T2, TAction>(query, action, Accesses, dependency, parallel, State); }
            catch { commands?.Clear(); jobs.EndUse(); throw; }
            return scheduler.ScheduleStages(new SystemCompletion(world, jobs, State, commands), rows);
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

    private sealed class ComponentSystem<T1, T2, T3, TAction>(SystemRunner runner, World world, Scheduler scheduler,
        Query query, TAction action, SystemAccesses? accesses, CommandBatch? commands,
        bool parallel) : SystemEntry(accesses, commands is not null)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        protected override void AddComponents()
        {
            Values.Add(new(ComponentType<T1>.TypeId, WorldValueKind.Table, true, ComponentType<T1>.TracksWritesType));
            Values.Add(new(ComponentType<T2>.TypeId, WorldValueKind.Table, false, ComponentType<T2>.TracksWritesType));
            Values.Add(new(ComponentType<T3>.TypeId, WorldValueKind.Table, false, ComponentType<T3>.TracksWritesType));
            AddQueryReads(query, ComponentType<T1>.TypeId, ComponentType<T2>.TypeId, ComponentType<T3>.TypeId);
            for (int type = 0; type < ComponentTypes.Count; type++)
                if (query.Description.Changed.Has(type)) Values.Add(new(type, WorldValueKind.Table, false, true));
        }
        internal override JobHandle Schedule(JobHandle dependency)
        {
            var jobs = (WorldJobs)world.Execution!;
            jobs.BeginUse();
            JobHandle rows;
            try { rows = runner.Schedule<T1, T2, T3, TAction>(query, action, Accesses, dependency, parallel, State); }
            catch { commands?.Clear(); jobs.EndUse(); throw; }
            return scheduler.ScheduleStages(new SystemCompletion(world, jobs, State, commands), rows);
        }
    }
}
