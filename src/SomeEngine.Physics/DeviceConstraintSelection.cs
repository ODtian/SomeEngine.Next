using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public interface IDeviceConstraintSelection : IDisposable
{
    void AddBindings(Dictionary<string, ResourceBinding[]> bindings);
    void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses);
    void Capture();
    void Capture(in PhysicsStepContext context) => Capture();
    int Count { get; }
    DeviceContactMemory? Memory => null;
    ResourceBinding RowsBinding => default;
}
public interface IDeviceConstraintRowSelection
{
    static virtual ReadOnlySpan<ChunkAccess> Components => [];
    Query AddReads(Query query) => query;
    bool Matches(QueryChunk chunk, int row);
    bool HasChangedInputs(QueryChunk chunk, ulong since) => true;
}
public readonly struct DeviceConstraintAllRows : IDeviceConstraintRowSelection
{
    public bool Matches(QueryChunk chunk, int row) => true;
    public bool HasChangedInputs(QueryChunk chunk, ulong since) => false;
}
public sealed class DeviceConstraintSelection<TComponent, TData> : DeviceConstraintSelection<TComponent, TData, DeviceConstraintAllRows>
    where TComponent : struct, IComponent where TData : unmanaged
{
    public DeviceConstraintSelection(IGraphicsBackend backend, Device device, World world, Query rows,
        DeviceConstraintData<TData> data, string bindingName) : base(backend, device, world, rows, data, default, bindingName) { }
}

/// <summary>The original constraint identities and a dense list of selected parameter indices.</summary>
public class DeviceConstraintSelection<TComponent, TData, TSelection> : IDeviceConstraintSelection
    where TComponent : struct, IComponent where TData : unmanaged where TSelection : struct, IDeviceConstraintRowSelection
{
    private readonly IGraphicsBackend _backend;
    private readonly Query _rows;
    private readonly World _world;
    private readonly DeviceConstraintData<TData> _data;
    private readonly TSelection _selection;
    private readonly string _bindingName;
    private readonly Buffer _entities;
    private readonly BufferSrv _view, _rowsView;
    private readonly int _rowStart;
    private readonly PhysicsQueryWindow _window;
    private bool _captured;
    private ulong _membership;
    public DeviceConstraintSelection(IGraphicsBackend backend, Device device, World world, Query rows,
        DeviceConstraintData<TData> data, TSelection selection, string bindingName)
    {
        _backend = backend; _world = world; _data = data; _selection = selection; _bindingName = bindingName;
        _rows = selection.AddReads(rows.WithOptionalRead<TComponent>().WithOptionalRead<DeviceConstraintBinding<TData>>());
        _window = new(_rows);
        ulong alignment = device.Capabilities.Limits.ShaderBufferAlignment;
        _rowStart = (int)((((ulong)data.Capacity * 8 + alignment - 1) / alignment * alignment) / 4);
        _entities = backend.CreateBuffer(device, new((ulong)(_rowStart + data.Capacity + 1) * 4, BufferUsages.ShaderRead, bindingName), MemoryType.Upload);
        _view = backend.CreateBufferSrv(device, new(_entities, new(0, (ulong)data.Capacity * 8), StructureStride: 8));
        _rowsView = backend.CreateBufferSrv(device, new(_entities, new((ulong)_rowStart * 4, (ulong)(data.Capacity + 1) * 4), StructureStride: 4));
    }
    public int Count { get; private set; }
    public DeviceContactMemory? Memory => _data.Memory;
    internal bool UsesVersionFilters => _window.UsesVersions;
    internal ulong SourceMembershipVersion => _rows.MembershipVersion;
    public ResourceBinding RowsBinding => ResourceBinding.ReadOnlyBuffer(_rowsView);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings) => AddBindings(bindings, _bindingName);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings, string name)
    {
        bindings.Add(name, [ResourceBinding.ReadOnlyBuffer(_view)]);
        bindings.Add(name + "Rows", [ResourceBinding.ReadOnlyBuffer(_rowsView)]);
    }
    public void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(((WorldJobs)_world.Execution!).CollectAccess<CaptureAccess>(_rows));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_entities), 0, (long)_entities.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(this)));
    }
    private readonly struct CaptureAccess : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.ReadOptional<TComponent>(true),
            ChunkAccess.ReadOptional<DeviceConstraintBinding<TData>>(true),..TSelection.Components];
        public void Execute(SystemChunk chunk) { }
    }
    public void Capture() => CaptureCore(null);
    public void Capture(in PhysicsStepContext context) => CaptureCore(context);
    private void CaptureCore(PhysicsStepContext? context)
    {
        if (!PhysicsStageCommands.CaptureInput(this)) return;
        ulong since = _window.Begin(context), membership = _rows.MembershipVersion;
        bool changed = !_captured || membership != _membership || _window.UsesVersions;
        using QueryScope scope = _rows.Since(since).OpenRead();
        if (!changed)
            foreach (QueryChunk chunk in scope)
                if (chunk.WasWritten<TComponent>(since) || chunk.WasWritten<DeviceConstraintBinding<TData>>(since)
                    || _selection.HasChangedInputs(chunk, since)) { changed = true; break; }
        if (changed)
        {
            using MappedBuffer mapping = _backend.Map(_entities, MapType.Write, BufferRange.Whole);
            mapping.Bytes.Clear(); Span<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            Count = 0;
            foreach (QueryChunk chunk in scope)
            {
                if (!chunk.TryRead<DeviceConstraintBinding<TData>>(out var bindings)) continue;
                for (int row = 0; row < chunk.Count; row++)
                {
                    if (!chunk.Has<TComponent>(row) || !chunk.Has<DeviceConstraintBinding<TData>>(row)
                        || !ReferenceEquals(bindings[row].Data, _data) || !_selection.Matches(chunk, row)) continue;
                    int index = bindings[row].Index;
                    values[_rowStart + 1 + Count++] = (uint)index;
                    values[index * 2] = (uint)chunk.Entities[row].Id;
                    values[index * 2 + 1] = (uint)chunk.Entities[row].Generation;
                }
            }
            values[_rowStart] = (uint)Count; mapping.Flush(mapping.Range);
        }
        _captured = true; _membership = membership; _window.Accept(_world.Version);
    }
    public void Dispose() { _view.Dispose(); _rowsView.Dispose(); _entities.Dispose(); }
}
