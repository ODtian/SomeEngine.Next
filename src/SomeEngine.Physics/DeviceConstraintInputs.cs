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
    private readonly QueryDefinition _filter;
    private readonly QueryHandle _access;
    private readonly JobResourceAccess[] _accesses;
    private readonly int _rowsPerPacket;
    internal WriteDeviceConstraintInputs(World world, QueryHandle rows, DeviceConstraintData<TData> data, TSource source,
        int rowsPerPacket, ReadOnlySpan<JobResourceAccess> extraAccesses)
    {
        _world = world; _data = data; _source = source; _rowsPerPacket = rowsPerPacket;
        QueryDefinition inputs = world.GetQueryDefinition(rows).AsReadOnly();
        _filter = inputs.AsFilter();
        QueryDefinition definition = source.AddReads(QueryDefinition.Empty.WithAccessFrom(inputs).WithOptionalRead<TComponent>());
        _access = world.Query(definition);
        var resources = new QueryReadResources();
        source.AddReadResources(resources);
        resources.Add<TComponent, TSource>();
        resources.Add<DeviceConstraintBinding<TData>, DeviceConstraintReferenceResources<TData>>();
        int queryAccesses = QueryJobAccess.Count(definition);
        _accesses = new JobResourceAccess[queryAccesses + extraAccesses.Length];
        QueryJobAccess.CopyAccesses(world, definition, _accesses.AsSpan(0, queryAccesses), resources);
        extraAccesses.CopyTo(_accesses.AsSpan(queryAccesses));
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new WriteDeviceConstraintInputJob<TComponent, TData, TSource, DeviceConstraintUploadResources<TData>>(_world, _access, _data, _source)
            .ScheduleParallel(context.World, _accesses, new JobEntityScheduleOptions(rowsPerPacket: _rowsPerPacket, filter: _filter), dependency);
    public void Dispose() => _world.ReleaseQuery(_access);
}

internal readonly partial struct WriteDeviceConstraintInputJob<TComponent, TData, TSource, TUpload>(World world, QueryHandle access,
    DeviceConstraintData<TData> destination, TSource source) : IJobEntity
    where TComponent : struct, IComponent where TData : unmanaged where TSource : struct, IDeviceConstraintSource<TComponent, TData>
    where TUpload : struct, IJobComponentResources<DeviceConstraintBinding<TData>>
{
    public void Execute(Entity entity, [JobResources(nameof(TUpload))] in DeviceConstraintBinding<TData> binding,
        [JobResources(nameof(TSource))] in TComponent component)
    {
        if (!ReferenceEquals(binding.Data, destination)) return;
        var read = new ReadSource(entity, component, source);
        world.ExecuteQuery(access, ref read);
        destination.WriteInput(binding.Index, entity, in read.Data, read.Version);
    }
    private struct ReadSource(Entity entity, TComponent component, TSource source) : IQueryExecution
    {
        internal TData Data;
        internal uint Version;
        public void Execute(scoped QueryCursor rows)
        {
            rows.TryGetRow(entity, out QueryRow row);
            Data = source.Read(in component, row, rows);
            Version = row.WriteVersion<TComponent>();
        }
    }
}
