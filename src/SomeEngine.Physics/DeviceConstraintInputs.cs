using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Converts changed contiguous component ranges directly into mapped device input pages.</summary>
public readonly struct WriteDeviceConstraintInputs<TComponent, TData, TSource> : IPhysicsStage, IDisposable
    where TComponent : struct, IComponent where TData : unmanaged where TSource : struct, IDeviceConstraintSource<TComponent, TData>
{
    private readonly World _world;
    private readonly Query _rows;
    private readonly DeviceConstraintData<TData> _data;
    private readonly TSource _source;
    private readonly SystemRunner _runner;
    private readonly InputChanges _changes;
    private readonly JobResourceAccess[] _extraAccesses;
    internal WriteDeviceConstraintInputs(World world, Query rows, DeviceConstraintData<TData> data, TSource source,
        int rowsPerChunk, ReadOnlySpan<JobResourceAccess> extraAccesses)
    {
        _world = world; _rows = SystemRunner.Bind<Write>(rows); _data = data; _source = source;
        _runner = new(world, rowsPerChunk == 0 ? 1024 : rowsPerChunk);
        _changes = new(world, _rows); _extraAccesses = extraAccesses.ToArray();
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        JobResourceAccess state = JobResourceAccess.Write(scheduler.GetResourceToken(_changes));
        JobHandle start = scheduler.Schedule(new Begin(_data, _changes, context, scheduler), state, dependency);
        JobHandle written = scheduler.Schedule(new Dispatch(this), JobResourceAccess.Read(scheduler.GetResourceToken(_changes)), start);
        return scheduler.Schedule(new Accept(_world, _changes), state, written);
    }
    private readonly struct Dispatch(WriteDeviceConstraintInputs<TComponent, TData, TSource> inputs) : IJob
    {
        public void Execute()
        {
            var write = new Write(inputs._world, inputs._data, inputs._source, inputs._changes, inputs._extraAccesses);
            inputs._runner.Schedule(inputs._rows.Since(inputs._changes.Since), write);
        }
    }
    private sealed class InputChanges(World world, Query query)
    {
        internal readonly PhysicsQueryWindow Window = new(query);
        internal readonly EventReader<MembershipChange> Members = world.WatchMembership(query);
        internal ulong Since;
        internal bool First = true;
    }
    private readonly struct Begin(DeviceConstraintData<TData> data, InputChanges changes, PhysicsStepContext context, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            changes.Since = changes.Window.Begin(context);
            EventReadRange range = changes.Members.Capture();
            scheduler.Schedule(new Retire(data, changes.Members, range), [range.Read, .. data.GetUploadWriteAccesses(scheduler)], range.Ready);
        }
    }
    private readonly struct Retire(DeviceConstraintData<TData> data, EventReader<MembershipChange> members, EventReadRange range) : IJob
    {
        public void Execute()
        {
            using EventRead<MembershipChange> read = members.Open(range);
            foreach (MembershipChange change in read.First) if (change.Kind == MembershipChangeKind.Removed) data.RetireInput(change.Entity);
            foreach (MembershipChange change in read.Second) if (change.Kind == MembershipChangeKind.Removed) data.RetireInput(change.Entity);
            members.Advance(read.Through);
        }
    }
    private readonly struct Accept(World world, InputChanges changes) : IJob
    { public void Execute() { changes.Window.Accept(world.Version); changes.First = false; } }
    private readonly struct Write(World world, DeviceConstraintData<TData> data, TSource source, InputChanges changes,
        JobResourceAccess[] extra) : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [
            ChunkAccess.Read<TComponent>(true),ChunkAccess.Read<DeviceConstraintBinding<TData>>(true),
            ChunkAccess.ReadOptional<DeviceBodyBinding>(true),..TSource.Components];
        public static SystemAccesses AdditionalAccesses { get; } = TSource.AdditionalAccesses;
        public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
        {
            ComponentResources.CollectRange<TComponent, TSource>(rows.Read<TComponent>(), ref resources);
            ComponentResources.CollectRange<DeviceConstraintBinding<TData>, DeviceConstraintUploadResources<TData>>(rows.Read<DeviceConstraintBinding<TData>>(), ref resources);
            TSource.CollectResources(rows, ref resources);
        }
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        { resources.Read(changes); foreach (JobResourceAccess access in extra) resources.Add(access); }
        public void Execute(SystemChunk rows)
        {
            if (!changes.First && !changes.Window.UsesVersions && !rows.WasWritten<TComponent>(changes.Since)
                && !rows.WasWritten<DeviceConstraintBinding<TData>>(changes.Since)
                && !TSource.HasChangedInputs(rows, world, changes.Since)) return;
            ReadOnlySpan<DeviceConstraintBinding<TData>> bindings = rows.Read<DeviceConstraintBinding<TData>>();
            using ReadLookup<DeviceBodyBinding> bodies = world.OpenRead<DeviceBodyBinding>();
            int row = 0;
            while (row < rows.Count)
            {
                if (!ReferenceEquals(bindings[row].Data, data)) { row++; continue; }
                int first = row, index = bindings[row].Index, max = Math.Min(rows.Count - row, data.InputRows(index));
                row++;
                while (row - first < max && ReferenceEquals(bindings[row].Data, data) && bindings[row].Index == index + row - first) row++;
                Span<TData> output = data.InputValues(index, row - first);
                source.Write(rows, world, first, bodies, output);
                for (int item = first; item < row; item++) data.WriteInputMetadata(bindings[item].Index, rows.Entities[item]);
            }
        }
    }
    public void Dispose() => _changes.Members.Dispose();
}
