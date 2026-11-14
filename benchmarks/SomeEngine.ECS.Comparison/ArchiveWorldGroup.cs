using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal abstract class ArchiveWorldGroup : ISystemVersions, IDisposable
{
    public int VersionCount => 2;
    internal abstract JobHandle Schedule();
    public abstract void Complete();
    public abstract void CopyVersions(Span<ulong> versions);
    public abstract void RestoreVersions(ReadOnlySpan<ulong> versions);
    public abstract void Dispose();
}

internal sealed partial class SourceFullArchiveWorld
{
    internal override ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler)
    { var group = new SourceArchiveGroup(World, scheduler); Systems.Bind(group); return group; }
}
internal sealed partial class NativeFullArchiveWorld<TValues>
    where TValues : struct, INativeArchiveValues
{
    internal override ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler)
    { var group = Store.ConnectArchiveGroup(scheduler, () => Version); Systems.Bind(group); return group; }
}

internal sealed partial class SourceArchiveGroup : ArchiveWorldGroup
{
    private readonly SystemGroup _group;
    internal SourceArchiveGroup(World world, Scheduler scheduler)
    {
        _group = new(world, scheduler); Query query = world.Query<Position, Velocity>();
        _group.Add(new Move(), query);
        _group.Add(new Move(), query);
    }
    private readonly partial struct Move : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
            => ComponentMath.Move(positions, velocities);
    }
    internal override JobHandle Schedule() => _group.Schedule();
    public override void Complete() => _group.Complete();
    public override void CopyVersions(Span<ulong> versions) => _group.CopyVersions(versions);
    public override void RestoreVersions(ReadOnlySpan<ulong> versions) => _group.RestoreVersions(versions);
    public override void Dispose() => Complete();
}

internal sealed class NativeArchiveGroup<T, TQuery> : ArchiveWorldGroup
    where T : struct, INativeQueryRange where TQuery : struct, INativeQueryRanges<T>
{
    private readonly Scheduler _scheduler;
    private readonly NativeQueryJobs<T, TQuery> _rows;
    private readonly NativeSystemProgress[] _states;
    private JobHandle _last;
    internal NativeArchiveGroup(Scheduler scheduler, TQuery query, Func<ulong> version)
    { _scheduler = scheduler; _rows = new(scheduler, query); _states = [new(version), new(version)]; }
    internal override JobHandle Schedule()
    {
        foreach (NativeSystemProgress state in _states)
        {
            JobHandle rows = _rows.ScheduleSystem(state, true, _last);
            _last = _scheduler.Schedule(new Publish(state), dependency: rows, options: default(JobScheduleOptions));
        }
        return _last;
    }
    private readonly struct Publish(NativeSystemProgress state) : IJob
    { public void Execute() => state.Last = state.Current; }
    public override void Complete() => _last.Complete();
    public override void CopyVersions(Span<ulong> versions)
    { for (int i = 0; i < 2; i++) { versions[i * 2] = _states[i].Last; versions[i * 2 + 1] = _states[i].Current; } }
    public override void RestoreVersions(ReadOnlySpan<ulong> versions)
    { for (int i = 0; i < 2; i++) { _states[i].Last = versions[i * 2]; _states[i].Current = versions[i * 2 + 1]; } }
    public override void Dispose() { Complete(); _rows.Dispose(); }
}

internal sealed partial class EasyArchiveStore
{
    internal override ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler, Func<ulong> version)
        => new NativeArchiveGroup<EasyMotionRange, EasyMotionQuery>(scheduler, new(_world), version);
}
internal sealed partial class FrifloArchiveStore
{
    internal override ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler, Func<ulong> version)
        => new NativeArchiveGroup<FrifloMotionRange, FrifloMotionQuery>(scheduler, new(_world, _world.Query<Position, Velocity>()), version);
}
internal sealed partial class ArchArchiveStore
{
    internal override ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler, Func<ulong> version)
        => new NativeArchiveGroup<ArchMotionRange, ArchMotionQuery>(scheduler,
            new(_world, new Arch.Core.QueryDescription().WithAll<Position, Velocity>()), version);
}
internal sealed partial class DefaultArchiveStore
{
    internal override ArchiveWorldGroup ConnectArchiveGroup(Scheduler scheduler, Func<ulong> version)
        => new NativeArchiveGroup<DefaultMotionRange, DefaultMotionQuery>(scheduler,
            new(_world), version);
}
