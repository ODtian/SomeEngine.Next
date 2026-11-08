using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Actual entity identities selected for resident constraint rows.</summary>
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
    QueryDefinition AddReads(QueryDefinition definition);
    void AddReadResources(QueryReadResources resources);
    bool Matches(scoped QueryRow row);
    /// <summary>Reports changes to the values used by Matches; the default evaluates those values on each capture.</summary>
    bool HasChangedInputs(scoped QueryChunkView chunk) => true;
}

public readonly struct DeviceConstraintAllRows : IDeviceConstraintRowSelection
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition;
    public void AddReadResources(QueryReadResources resources) { }
    public bool Matches(scoped QueryRow row) => true;
    public bool HasChangedInputs(scoped QueryChunkView chunk) => false;
}

public sealed class DeviceConstraintSelection<TComponent, TData> : DeviceConstraintSelection<TComponent, TData, DeviceConstraintAllRows>
    where TComponent : struct, IComponent where TData : unmanaged
{
    public DeviceConstraintSelection(IGraphicsBackend backend, Device device, World world, QueryHandle rows,
        DeviceConstraintData<TData> data, string bindingName, QueryReadResources? resources = null)
        : base(backend, device, world, rows, data, default, bindingName, resources) { }
}

/// <summary>Captures membership and generations; parameter and numerical values stay in their existing resources.</summary>
public partial class DeviceConstraintSelection<TComponent, TData, TSelection> : IDeviceConstraintSelection
    where TComponent : struct, IComponent where TData : unmanaged
    where TSelection : struct, IDeviceConstraintRowSelection
{
    private readonly IGraphicsBackend _backend;
    private readonly World _world;
    private readonly QueryHandle _rows;
    private readonly DeviceConstraintData<TData> _data;
    private readonly TSelection _selection;
    private readonly string _bindingName;
    private readonly Buffer _entities;
    private readonly Buffer? _denseRows;
    private readonly DeviceInputPages? _identities, _indices;
    private readonly CommandContext? _commands;
    private readonly Device _device;
    private readonly int _rowStart;
    private readonly BufferSrv _view, _rowsView;
    private readonly JobResourceAccess[] _queryAccesses;
    private bool _hasCapturedMembers;
    private uint _membershipVersion;
    private readonly PhysicsQueryWindow _window;

    public DeviceConstraintSelection(IGraphicsBackend backend, Device device, World world, QueryHandle rows,
        DeviceConstraintData<TData> data, TSelection selection, string bindingName, QueryReadResources? resources = null)
    {
        _backend = backend; _device = device; _world = world; _data = data; _selection = selection; _bindingName = bindingName;
        _sourceKey = world.GetQueryDefinition(rows).AsFilter().Key;
        QueryDefinition definition = selection.AddReads(world.GetQueryDefinition(rows).AsFilter()
            .WithOptionalRead<TComponent>().WithOptionalRead<DeviceConstraintBinding<TData>>());
        _window = new(definition);
        _rows = world.Query(definition);
        var descriptions = new QueryReadResources();
        if (resources is not null) descriptions.AddFrom(resources);
        descriptions.Add<DeviceConstraintBinding<TData>, DeviceConstraintReferenceResources<TData>>();
        selection.AddReadResources(descriptions);
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, descriptions);
        if (data.Memory?.UsesSparseBuffers == true)
        {
            _entities = data.Memory.CreateBuffer(new((ulong)data.Capacity * 8,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, bindingName), 8);
            _denseRows = data.Memory.CreateBuffer(new((ulong)(data.Capacity + 1) * 4,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, bindingName + " rows"), 4, 1);
            _view = backend.CreateBufferSrv(device, new(_entities, new(0, _entities.Info.Size), StructureStride: 8));
            _rowsView = backend.CreateBufferSrv(device, new(_denseRows, new(0, _denseRows.Info.Size), StructureStride: 4));
            _identities = new(backend, device, data.Capacity, 8, bindingName + " inputs");
            _indices = new(backend, device, data.Capacity + 1, 4, bindingName + " row inputs");
            _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: bindingName));
        }
        else
        {
            ulong alignment = device.Capabilities.Limits.ShaderBufferAlignment;
            _rowStart = (int)((((ulong)data.Capacity * 8 + alignment - 1) / alignment * alignment) / 4);
            _entities = backend.CreateBuffer(device, new((ulong)_rowStart * 4 + (ulong)data.Capacity * 4 + 4, BufferUsages.ShaderRead,
                bindingName), MemoryType.Upload);
            _rowsView = backend.CreateBufferSrv(device, new(_entities, new((ulong)_rowStart * 4, (ulong)data.Capacity * 4 + 4), StructureStride: 4));
            _view = backend.CreateBufferSrv(device, new(_entities, new(0, (ulong)data.Capacity * 8), StructureStride: 8));
        }

    }

    public int Count { get; private set; }
    public DeviceContactMemory? Memory => _data.Memory;
    internal bool UsesVersionFilters => _window.UsesVersions;
    internal uint SourceMembershipVersion => _world.MembershipVersion(_rows);
    public ResourceBinding RowsBinding => ResourceBinding.ReadOnlyBuffer(_rowsView);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings) => AddBindings(bindings, _bindingName);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings, string bindingName)
    {
        bindings.Add(bindingName, [ResourceBinding.ReadOnlyBuffer(_view)]);
        bindings.Add(bindingName + "Rows", [ResourceBinding.ReadOnlyBuffer(_rowsView)]);
    }
    public void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(_queryAccesses);
        if (_contactChanges is not null) accesses.Add(_contactChanges.ReadAccess);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_entities), 0, (long)_entities.Info.Size));
        if (_denseRows is not null) accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_denseRows), 0, (long)_denseRows.Info.Size));
        if (_commands is not null) accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
    }
    public void Capture() => CaptureCore(null);
    public void Capture(in PhysicsStepContext context) => CaptureCore(context);
    private void CaptureCore(PhysicsStepContext? context)
    {
        if (!PhysicsStageCommands.CaptureInput(this)) return;
        uint baseline = _window.Begin(context);
        uint membership = _world.MembershipVersion(_rows);
        if (CaptureContactChanges(baseline, membership)) return;
        var changes = new SourceChanges(_selection, !_hasCapturedMembers || membership != _membershipVersion || _window.UsesVersions);
        _world.ExecuteReadQuery(_rows, baseline, ref changes,
            static (scoped QueryCursor rows, ref SourceChanges changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (!chunk.Has<TComponent>() || chunk.HasChangedSinceLastSystemVersion<TComponent>()
                        || !chunk.Has<DeviceConstraintBinding<TData>>()
                        || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<TData>>()
                        || changes.Selection.HasChangedInputs(chunk))
                    { changes.Changed = true; break; }
            });
        if (!changes.Changed)
        {
            _window.Accept(changes.Version);
            _contactChangesNumber = _contactChanges?.ChangesNumber ?? 0;
            return;
        }
        if (_identities is not null)
        {
            _identities.Clear(); _indices!.Clear();
            _denseIndices.Clear(); Array.Clear(_rowPositions);
            var selected = new ReadPagedRows(_data, _selection, _identities, _indices, _contactChanges is null ? null : this);
            _world.ExecuteReadQuery(_rows, baseline, ref selected,
                static (scoped QueryCursor rows, ref ReadPagedRows state) => state.Read(rows));
            Count = selected.Count;
            _data.Memory!.EnsureRows(Math.Max(selected.MaximumRow + 1, Count));
            MemoryMarshal.Write(_indices.Row(0), (uint)Count);
            SubmitPagedInputs();
        }
        else
        {
            using MappedBuffer mapping = _backend.Map(_entities, MapType.Write, BufferRange.Whole);
            mapping.Bytes.Clear();
            var selected = new ReadRows(_data, _selection, mapping, _rowStart);
            _world.ExecuteReadQuery(_rows, baseline, ref selected,
                static (scoped QueryCursor rows, ref ReadRows state) => state.Read(rows));
            Count = selected.Count;
            MemoryMarshal.Cast<byte, uint>(mapping.Bytes)[_rowStart] = (uint)Count;
            mapping.Flush(mapping.Range);
        }
        _hasCapturedMembers = true; _membershipVersion = membership; _window.Accept(changes.Version);
        _contactChangesNumber = _contactChanges?.ChangesNumber ?? 0;
    }
    private struct SourceChanges(TSelection selection, bool changed)
    {
        internal TSelection Selection = selection;
        internal bool Changed = changed;
        internal uint Version;
    }
    private ref struct ReadRows(DeviceConstraintData<TData> data, TSelection selection, MappedBuffer mapping, int rowStart)
    {
        internal int Count;
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (!row.Has<TComponent>() || !selection.Matches(row) || !row.TryRead<DeviceConstraintBinding<TData>>(out var binding)
                    || !ReferenceEquals(binding.Data, data)) continue;
                Span<uint> entities = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
                entities[rowStart + 1 + Count++] = (uint)binding.Index;
                Entity entity = row.Entity;
                entities[binding.Index * 2] = (uint)entity.Index;
                entities[binding.Index * 2 + 1] = (uint)entity.Generation;
            }
        }
    }
    private ref struct ReadPagedRows(DeviceConstraintData<TData> data, TSelection selection, DeviceInputPages identities, DeviceInputPages indices,
        DeviceConstraintSelection<TComponent, TData, TSelection>? capture)
    {
        internal int Count, MaximumRow;
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (!row.Has<TComponent>() || !row.TryRead<DeviceConstraintBinding<TData>>(out var binding)
                    || !ReferenceEquals(binding.Data, data) || !selection.Matches(row)) continue;
                MemoryMarshal.Write(indices.Row(1 + Count++), (uint)binding.Index);
                Span<uint> identity = MemoryMarshal.Cast<byte, uint>(identities.Row(binding.Index));
                identity[0] = (uint)row.Entity.Index; identity[1] = (uint)row.Entity.Generation;
                capture?.RecordRow(binding.Index);
                MaximumRow = Math.Max(MaximumRow, binding.Index);
            }
        }
    }
    public void Dispose() { _world.ReleaseQuery(_rows); _view.Dispose(); _rowsView.Dispose(); _entities.Dispose(); _denseRows?.Dispose(); _identities?.Dispose(); _indices?.Dispose(); _commands?.Dispose(); }
}
