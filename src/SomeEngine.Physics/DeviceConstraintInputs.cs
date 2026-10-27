using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly struct WriteDeviceConstraintInputs<TComponent, TData, TSource> : IPhysicsStage, IDisposable
    where TComponent : struct, IComponent where TData : unmanaged where TSource : struct, IDeviceConstraintSource<TComponent, TData>
{
    private readonly World _world;
    private readonly DeviceConstraintData<TData> _data;
    private readonly TSource _source;
    private readonly QueryHandle _inputs, _access;
    private readonly JobResourceAccess[] _accesses, _captureAccesses;
    private readonly QueryReadResources _resources;
    private readonly List<Entity> _entities;
    private readonly int _rowsPerPacket;
    internal WriteDeviceConstraintInputs(World world, QueryHandle rows, DeviceConstraintData<TData> data, TSource source,
        int rowsPerPacket, ReadOnlySpan<JobResourceAccess> extraAccesses)
    {
        _world = world; _data = data; _source = source;
        _rowsPerPacket = new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket).RowsPerPacket;
        _entities = [];
        QueryDefinition inputs = world.GetQueryDefinition(rows).AsReadOnly();
        QueryDefinition definition = source.AddReads(QueryDefinition.Empty.WithAccessFrom(inputs)
            .WithOptionalRead<TComponent>().WithOptionalRead<DeviceConstraintBinding<TData>>());
        _access = world.Query(definition);
        _inputs = world.Query(inputs.WithAccess(definition));
        _resources = new QueryReadResources();
        source.AddReadResources(_resources);
        _resources.Add<TComponent, TSource>();
        _resources.Add<DeviceConstraintBinding<TData>, DeviceConstraintUploadResources<TData>>();
        int queryAccesses = QueryJobAccess.Count(definition);
        _accesses = new JobResourceAccess[queryAccesses + extraAccesses.Length + 1];
        QueryJobAccess.CopyAccesses(world, definition, _accesses.AsSpan(0, queryAccesses), _resources);
        extraAccesses.CopyTo(_accesses.AsSpan(queryAccesses));
        _accesses[^1] = JobResourceAccess.Read(WorldJobs.GetScheduler(world).GetResourceToken(_entities));
        _captureAccesses = [.. _accesses,
            JobResourceAccess.Write(WorldJobs.GetScheduler(world).GetResourceToken(_entities))];
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new Capture(this), _captureAccesses, dependency);

    private readonly struct Capture(WriteDeviceConstraintInputs<TComponent, TData, TSource> inputs) : IJob
    {
        public void Execute()
        {
            inputs._entities.Clear();
            var selected = new ReadMembers(inputs._data, inputs._entities);
            inputs._world.ExecuteReadQuery(inputs._inputs, ref selected,
                static (scoped QueryCursor rows, ref ReadMembers selected) => selected.Execute(rows));
            EntityQueryJobs.Schedule(inputs._world, new Packets(inputs._inputs, inputs._entities, inputs._rowsPerPacket),
                new Write(inputs), inputs._accesses, inputs._resources);
        }
    }
    private readonly struct ReadMembers(DeviceConstraintData<TData> data, List<Entity> entities)
    {
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
                if (row.Has<TComponent>() && row.TryRead<DeviceConstraintBinding<TData>>(out var binding)
                    && ReferenceEquals(binding.Data, data)) entities.Add(row.Entity);
        }
    }
    private readonly struct Packets(QueryHandle query, List<Entity> entities, int rowsPerPacket) : IEntityQueryWork
    {
        private int PacketRows => rowsPerPacket == 0 ? entities.Count : rowsPerPacket;
        public int Count => entities.Count == 0 ? 0 : (entities.Count + PacketRows - 1) / PacketRows;
        public int QueryCount => 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => query;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex)
        {
            int first = workIndex * PacketRows;
            return CollectionsMarshal.AsSpan(entities).Slice(first, Math.Min(PacketRows, entities.Count - first));
        }
    }
    private readonly struct Write(WriteDeviceConstraintInputs<TComponent, TData, TSource> inputs) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            var write = new WriteRows(queries.Get(0), inputs._data, inputs._source);
            inputs._world.ExecuteQuery(inputs._access, ref write,
                static (scoped QueryCursor access, ref WriteRows write) => write.Execute(access));
        }
    }
    private readonly ref struct WriteRows
    {
        private readonly EntityQuery _inputs;
        private readonly DeviceConstraintData<TData> _data;
        private readonly TSource _source;
        internal WriteRows(EntityQuery inputs, DeviceConstraintData<TData> data, TSource source)
        { _inputs = inputs; _data = data; _source = source; }
        public void Execute(scoped QueryCursor access)
        {
            foreach (QueryRow row in _inputs.Rows)
            {
                ref readonly var binding = ref row.ReadRef<DeviceConstraintBinding<TData>>();
                TData value = _source.Read(in row.ReadRef<TComponent>(), row, access);
                _data.WriteInput(binding.Index, row.Entity, in value, row.WriteVersion<TComponent>());
            }
        }
    }
    public void Dispose() { _world.ReleaseQuery(_inputs); _world.ReleaseQuery(_access); }
}
