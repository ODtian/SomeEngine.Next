using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal interface ISystemGroupJobs : IQueryJobs
{
    int Runs { get; }
    bool Check();
}

internal sealed class NativeSystemProgress(Func<ulong> version)
{
    internal readonly Func<ulong> Version = version;
    internal ulong Last, Current;
}

internal sealed class SystemGroupFrame<TEntity, THandle, TCommands, THierarchy> : IDisposable
    where TEntity : struct where THandle : struct where TCommands : struct, ICommandComparison<TEntity, THandle>
    where THierarchy : IHierarchyComparison<TEntity>
{
    internal readonly CommandScenario<TEntity, THandle, TCommands> Commands;
    private readonly THierarchy _hierarchy;
    private readonly TEntity[] _entities;
    private readonly HierarchyEdit<TEntity>[] _edits;
    private int _shift;
    internal ulong Version => _hierarchy.Version;
    internal SystemGroupFrame(TCommands commands, THierarchy hierarchy, TEntity[] entities)
    {
        _hierarchy = hierarchy; _entities = entities;
        Commands = new(commands, entities, 1);
        var initial = new HierarchyEdit<TEntity>[entities.Length - 2];
        for (int i = 2; i < entities.Length; i++) initial[i - 2] = new(entities[i], entities[0]);
        hierarchy.SetParents(initial, false); hierarchy.Maintain(); hierarchy.ClearFacts();
        _edits = new HierarchyEdit<TEntity>[(entities.Length - 1) / 4];
    }
    internal void Finish()
    {
        Commands.CompleteCommands();
        _shift ^= 1;
        for (int i = 0; i < _edits.Length; i++) _edits[i] = new(_entities[(i + 1) * 4], _entities[_shift]);
        _hierarchy.SetParents(_edits, true);
        _hierarchy.Maintain();
        _hierarchy.ClearFacts();
    }
    internal bool Check() => Commands.Check()
        && _hierarchy.ChildCount(_entities[1]) == (_shift == 0 ? 0 : _edits.Length)
        && _hierarchy.ChildCount(_entities[0]) == _entities.Length - 2 - (_shift == 0 ? 0 : _edits.Length);
    public void Dispose() { Commands.Dispose(); _hierarchy.Dispose(); }
}

internal sealed class NativeSystemGroupJobs<T, TQuery, TEntity, THandle, TCommands, THierarchy> : ISystemGroupJobs, ISystemVersions
    where T : struct, INativeQueryRange where TQuery : struct, INativeQueryRanges<T>
    where TEntity : struct where THandle : struct where TCommands : struct, ICommandComparison<TEntity, THandle>
    where THierarchy : IHierarchyComparison<TEntity>
{
    private readonly Scheduler _scheduler;
    private readonly NativeQueryJobs<T, TQuery> _rows;
    private readonly SystemGroupFrame<TEntity, THandle, TCommands, THierarchy> _frame;
    private readonly NativeSystemProgress[] _states;
    private readonly Action _playback, _finish, _clear;
    private readonly JobHandle[] _handles = new JobHandle[2];
    private int[][]? _predecessors;
    private int _graphBuilds;
    private JobHandle _last;
    public int Runs { get; private set; }
    public int VersionCount => 2;
    public void Complete() => _last.Complete();

    internal NativeSystemGroupJobs(Scheduler scheduler, TQuery query,
        SystemGroupFrame<TEntity, THandle, TCommands, THierarchy> frame)
    {
        _scheduler = scheduler; _rows = new(scheduler, query); _frame = frame;
        Func<ulong> version = () => frame.Version;
        _states = [new(version), new(version)];
        _playback = frame.Commands.PlaybackCommands; _finish = frame.Finish; _clear = frame.Commands.ClearCommands;
    }
    public void Run(bool scheduled, bool parallel, bool dependent)
    {
        _frame.Commands.RecordCommands();
        if (_predecessors is null) { _predecessors = [[], [0]]; _graphBuilds++; }
        for (int i = 0; i < 2; i++)
        {
            JobHandle dependency = _predecessors[i].Length == 0 ? default : _handles[_predecessors[i][0]];
            JobHandle rows = _rows.ScheduleSystem(_states[i], parallel, dependency);
            _handles[i] = _scheduler.ScheduleStages(new Completion(_rows.Structure, _states[i],
                i == 0 ? _playback : null, i == 0 ? _finish : null, i == 0 ? _clear : null), rows);
        }
        _last = _scheduler.CombineDependencies(_handles);
        _last.Complete(); Runs++;
    }
    public bool Check() => _graphBuilds == 1 && _last.IsCompleted && _frame.Check()
        && _states[0].Last == _states[0].Current && _states[1].Last == _states[1].Current
        && _states[1].Current >= _states[0].Current;
    public void CopyVersions(Span<ulong> versions)
    { for (int i = 0; i < 2; i++) { versions[i * 2] = _states[i].Last; versions[i * 2 + 1] = _states[i].Current; } }
    public void RestoreVersions(ReadOnlySpan<ulong> versions)
    { for (int i = 0; i < 2; i++) { _states[i].Last = versions[i * 2]; _states[i].Current = versions[i * 2 + 1]; } }
    private readonly struct Completion(JobResource structure, NativeSystemProgress state, Action? playback, Action? finish, Action? clear) : IStagedJob
    {
        public static int StageCount => 1;
        public void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses)
        { if (playback is not null || finish is not null) accesses.Add(JobResourceAccess.Write(structure)); }
        public void Execute() { playback?.Invoke(); finish?.Invoke(); state.Last = state.Current; }
        public void Release() => clear?.Invoke();
    }
    public void Dispose() { _last.Complete(); _frame.Dispose(); _rows.Dispose(); }
}

internal sealed partial class SomeEngineWorld
{
    private void PrepareSystemGroupJobs()
    {
        var commands = new SomeCommandComparison(_world, 1);
        var frame = new SystemGroupFrame<Entity, DeferredEntity, SomeCommandComparison, SomeHierarchyComparison>(
            commands, new(_world, Count), _entities);
        QueryJobs = new SourceSystemGroupJobs(_world, GetJobScheduler(), commands.Batch, frame);
    }
    private sealed class SourceSystemGroupJobs : ISystemGroupJobs
    {
        private readonly SystemGroup _group;
        private readonly SystemState _first, _second;
        private readonly SystemGroupFrame<Entity, DeferredEntity, SomeCommandComparison, SomeHierarchyComparison> _frame;
        private JobHandle _last;
        public int Runs { get; private set; }
        internal SourceSystemGroupJobs(World world, Scheduler scheduler, CommandBatch commands,
            SystemGroupFrame<Entity, DeferredEntity, SomeCommandComparison, SomeHierarchyComparison> frame)
        {
            _frame = frame; _group = new(world, scheduler);
            Query query = world.Query<Position, Velocity>();
            _first = _group.Add<Position, Velocity, MovePositions>(query, new(), commands: commands);
            SystemState finish = _group.AddJob(new FinishGroup(frame), structureWrite: true);
            _group.After(finish, _first);
            _second = _group.Add<Position, Velocity, MovePositions>(query, new());
            _group.After(_second, finish);
        }
        private readonly struct FinishGroup(SystemGroupFrame<Entity, DeferredEntity, SomeCommandComparison, SomeHierarchyComparison> frame) : IJob
        {
            public void Execute() => frame.Finish();
        }
        public void Run(bool scheduled, bool parallel, bool dependent)
        { _frame.Commands.RecordCommands(); _last = _group.Schedule(); _last.Complete(); Runs++; }
        public bool Check() => _group.GraphBuildCount == 1 && _last.IsCompleted && _frame.Check()
            && _first.LastSystemVersion == _first.CurrentSystemVersion && _second.LastSystemVersion == _second.CurrentSystemVersion
            && _second.CurrentSystemVersion >= _first.CurrentSystemVersion;
        public void Dispose() { _group.Complete(); _frame.Dispose(); }
    }
}

internal sealed partial class EasyEcsWorld
{
    private void PrepareSystemGroupJobs()
    {
        var frame = new SystemGroupFrame<EasyEcs.Entity, ReferenceDeferredEntity<EasyEcs.Entity>,
            NativeCommandComparison<EasyEcs.Entity, EasyEcs.EntityId, EasyCommandValues>, ReferenceHierarchy<EasyEcs.Entity, EasyHierarchyValues>>(
            new(new(_world), 1), new(new(_world), _entities), _entities);
        QueryJobs = new NativeSystemGroupJobs<EasyMotionRange, EasyMotionQuery, EasyEcs.Entity, ReferenceDeferredEntity<EasyEcs.Entity>,
            NativeCommandComparison<EasyEcs.Entity, EasyEcs.EntityId, EasyCommandValues>, ReferenceHierarchy<EasyEcs.Entity, EasyHierarchyValues>>(
            GetJobScheduler(), new(_world), frame);
    }
}
internal sealed partial class FrifloWorld
{
    private void PrepareSystemGroupJobs()
    {
        var frame = new SystemGroupFrame<Friflo.Engine.ECS.Entity, ReferenceDeferredEntity<Friflo.Engine.ECS.Entity>,
            NativeCommandComparison<Friflo.Engine.ECS.Entity, Friflo.Engine.ECS.RawEntity, FrifloCommandValues>, ReferenceHierarchy<Friflo.Engine.ECS.Entity, FrifloHierarchyValues>>(
            new(new(_world), 1), new(new(_world), _entities), _entities);
        QueryJobs = new NativeSystemGroupJobs<FrifloMotionRange, FrifloMotionQuery, Friflo.Engine.ECS.Entity, ReferenceDeferredEntity<Friflo.Engine.ECS.Entity>,
            NativeCommandComparison<Friflo.Engine.ECS.Entity, Friflo.Engine.ECS.RawEntity, FrifloCommandValues>, ReferenceHierarchy<Friflo.Engine.ECS.Entity, FrifloHierarchyValues>>(
            GetJobScheduler(), new(_world, _world.Query<Position, Velocity>()), frame);
    }
}
internal sealed partial class ArchWorld
{
    private void PrepareSystemGroupJobs()
    {
        var frame = new SystemGroupFrame<Arch.Core.Entity, ReferenceDeferredEntity<Arch.Core.Entity>,
            NativeCommandComparison<Arch.Core.Entity, Arch.Core.Entity, ArchCommandValues>, ReferenceHierarchy<Arch.Core.Entity, ArchHierarchyValues>>(
            new(new(_world), 1), new(new(_world), _entities), _entities);
        QueryJobs = new NativeSystemGroupJobs<ArchMotionRange, ArchMotionQuery, Arch.Core.Entity, ReferenceDeferredEntity<Arch.Core.Entity>,
            NativeCommandComparison<Arch.Core.Entity, Arch.Core.Entity, ArchCommandValues>, ReferenceHierarchy<Arch.Core.Entity, ArchHierarchyValues>>(
            GetJobScheduler(), new(_world, _two), frame);
    }
}
internal sealed partial class DefaultEcsWorld
{
    private void PrepareSystemGroupJobs()
    {
        var frame = new SystemGroupFrame<DefaultEcs.Entity, ReferenceDeferredEntity<DefaultEcs.Entity>,
            NativeCommandComparison<DefaultEcs.Entity, DefaultEcs.Entity, DefaultCommandValues>, ReferenceHierarchy<DefaultEcs.Entity, DefaultHierarchyValues>>(
            new(new(_world), 1), new(new(_world), _entities), _entities);
        QueryJobs = new NativeSystemGroupJobs<DefaultMotionRange, DefaultMotionQuery, DefaultEcs.Entity, ReferenceDeferredEntity<DefaultEcs.Entity>,
            NativeCommandComparison<DefaultEcs.Entity, DefaultEcs.Entity, DefaultCommandValues>, ReferenceHierarchy<DefaultEcs.Entity, DefaultHierarchyValues>>(
            GetJobScheduler(), new(_world), frame);
    }
}
