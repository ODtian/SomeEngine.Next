using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Selects breakable constraints and supplies their current force and torque limits.</summary>
public sealed class DeviceConstraintForceLimits<TData> : IDisposable where TData : unmanaged
{
    private readonly World _world;
    private readonly QueryHandle _rows;
    private readonly DeviceConstraintData<TData> _data;
    private readonly DeviceConstraintSelection<ConstraintBreakSettings, TData> _selection;
    private readonly PhysicsQueryWindow _window;
    private readonly Buffer _values;
    private readonly BufferSrv _view;
    private readonly MappedBuffer _mapping;
    private uint _membership;
    private bool _initialized;
    private readonly JobResourceAccess[] _reads;

    public DeviceConstraintForceLimits(IGraphicsBackend backend, Device device, World world, QueryHandle constraints,
        DeviceConstraintData<TData> data)
    {
        _world = world; _data = data;
        QueryDefinition definition = world.GetQueryDefinition(constraints).AsFilter()
            .WithOptionalRead<ConstraintBreakSettings>().WithOptionalRead<DeviceConstraintBinding<TData>>();
        _rows = world.Query(definition); _window = new(definition);
        _selection = new(backend, device, world, _rows, data, "DeviceForceLimitSelection");
        _values = backend.CreateBuffer(device, new((ulong)data.Capacity * 8, BufferUsages.ShaderRead), MemoryType.Upload);
        _view = backend.CreateBufferSrv(device, new(_values, new(0, _values.Info.Size), StructureStride: 8));
        _mapping = backend.Map(_values, MapType.Write, BufferRange.Whole);
        var resources = new QueryReadResources();
        resources.Add<DeviceConstraintBinding<TData>, DeviceConstraintReferenceResources<TData>>();
        _reads = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _reads, resources);
    }
    internal int Count => _selection.Count;
    internal ResourceBinding RowsBinding => _selection.RowsBinding;
    internal ResourceBinding LimitsBinding => ResourceBinding.ReadOnlyBuffer(_view);
    internal void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(_reads); _selection.AddAccesses(scheduler, accesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_values), 0, (long)_values.Info.Size));
    }
    internal void Capture(in PhysicsStepContext context)
    {
        _selection.Capture(in context);
        uint baseline = _window.Begin(context), membership = _world.MembershipVersion(_rows);
        var changes = new Changes { Changed = !_initialized || membership != _membership || _window.UsesVersions };
        _world.ExecuteReadQuery(_rows, baseline, ref changes, static (scoped QueryCursor rows, ref Changes state) =>
        {
            state.Version = rows.CurrentSystemVersion;
            foreach (QueryChunkView chunk in rows.Chunks)
                if (chunk.Has<ConstraintBreakSettings>() && chunk.HasChangedSinceLastSystemVersion<ConstraintBreakSettings>()
                    || chunk.Has<DeviceConstraintBinding<TData>>() && chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<TData>>()) state.Changed = true;
        });
        if (changes.Changed)
        {
            var read = new ReadLimits(this);
            _world.ExecuteReadQuery(_rows, baseline, ref read,
                static (scoped QueryCursor rows, ref ReadLimits state) => state.Read(rows));
            _mapping.Flush(_mapping.Range);
        }
        _window.Accept(changes.Version); _membership = membership; _initialized = true;
    }
    private struct Changes { internal bool Changed; internal uint Version; }
    private readonly ref struct ReadLimits(DeviceConstraintForceLimits<TData> limits)
    {
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
                if (row.TryRead<DeviceConstraintBinding<TData>>(out var binding) && ReferenceEquals(binding.Data, limits._data)
                    && row.TryRead<ConstraintBreakSettings>(out var settings))
                    MemoryMarshal.Write(limits._mapping.Bytes.Slice(binding.Index * 8, 8), in settings);
        }
    }
    public void Dispose()
    {
        _mapping.Dispose(); _view.Dispose(); _values.Dispose(); _selection.Dispose(); _world.ReleaseQuery(_rows);
    }
}
