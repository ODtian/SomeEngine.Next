using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Selects breakable constraints and supplies their current force and torque limits.</summary>
public sealed class DeviceConstraintForceLimits<TData> : IDisposable where TData : unmanaged
{
    private readonly World _world;
    private readonly Query _rows;
    private readonly DeviceConstraintData<TData> _data;
    private readonly DeviceConstraintSelection<ConstraintBreakSettings, TData> _selection;
    private readonly PhysicsQueryWindow _window;
    private readonly Buffer _values;
    private readonly BufferSrv _view;
    private readonly MappedBuffer _mapping;
    private ulong _membership;
    private bool _initialized;

    public DeviceConstraintForceLimits(IGraphicsBackend backend, Device device, World world, Query constraints,
        DeviceConstraintData<TData> data)
    {
        _world = world; _data = data;
        _rows = constraints.WithOptionalRead<ConstraintBreakSettings>().WithOptionalRead<DeviceConstraintBinding<TData>>();
        _window = new(_rows);
        _selection = new(backend, device, world, _rows, data, "DeviceForceLimitSelection");
        _values = backend.CreateBuffer(device, new((ulong)data.Capacity * 8, BufferUsages.ShaderRead), MemoryType.Upload);
        _view = backend.CreateBufferSrv(device, new(_values, new(0, _values.Info.Size), StructureStride: 8));
        _mapping = backend.Map(_values, MapType.Write, BufferRange.Whole);

    }
    internal int Count => _selection.Count;
    internal ResourceBinding RowsBinding => _selection.RowsBinding;
    internal ResourceBinding LimitsBinding => ResourceBinding.ReadOnlyBuffer(_view);
    internal void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        _selection.AddAccesses(scheduler, accesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_values), 0, (long)_values.Info.Size));
    }
    internal void Capture(in PhysicsStepContext context)
    {
        _selection.Capture(in context);
        ulong baseline = _window.Begin(context), membership = _rows.MembershipVersion;
        bool changed = !_initialized || membership != _membership || _window.UsesVersions;
        using QueryScope scope = _rows.Since(baseline).OpenRead();
        if (!changed)
            foreach (QueryChunk chunk in scope)
                if (chunk.WasWritten<ConstraintBreakSettings>(baseline) || chunk.WasWritten<DeviceConstraintBinding<TData>>(baseline))
                { changed = true; break; }
        if (changed)
        {
            foreach (QueryChunk chunk in scope)
            {
                if (!chunk.TryRead<DeviceConstraintBinding<TData>>(out var bindings) || !chunk.TryRead<ConstraintBreakSettings>(out var values)) continue;
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Has<ConstraintBreakSettings>(row) && chunk.Has<DeviceConstraintBinding<TData>>(row) && ReferenceEquals(bindings[row].Data, _data))
                        MemoryMarshal.Write(_mapping.Bytes.Slice(bindings[row].Index * 8, 8), in values[row]);
            }
            _mapping.Flush(_mapping.Range);
        }
        _window.Accept(_world.Version); _membership = membership; _initialized = true;
    }
    public void Dispose()
    {
        _mapping.Dispose(); _view.Dispose(); _values.Dispose(); _selection.Dispose();
    }
}
