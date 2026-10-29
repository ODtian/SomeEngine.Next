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
public class DeviceConstraintSelection<TComponent, TData, TSelection> : IDeviceConstraintSelection
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
    private readonly BufferSrv _view;
    private readonly JobResourceAccess[] _queryAccesses;
    private bool _hasCapturedMembers;
    private uint _membershipVersion, _lastSystemVersion;

    public DeviceConstraintSelection(IGraphicsBackend backend, Device device, World world, QueryHandle rows,
        DeviceConstraintData<TData> data, TSelection selection, string bindingName, QueryReadResources? resources = null)
    {
        _backend = backend; _world = world; _data = data; _selection = selection; _bindingName = bindingName;
        QueryDefinition definition = selection.AddReads(world.GetQueryDefinition(rows).AsFilter()
            .WithOptionalRead<DeviceConstraintBinding<TData>>());
        _rows = world.Query(definition);
        var descriptions = new QueryReadResources();
        if (resources is not null) descriptions.AddFrom(resources);
        descriptions.Add<DeviceConstraintBinding<TData>, DeviceConstraintReferenceResources<TData>>();
        selection.AddReadResources(descriptions);
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, descriptions);
        _entities = backend.CreateBuffer(device, new((ulong)data.Capacity * 8, BufferUsages.ShaderRead,
            bindingName), MemoryType.Upload);
        _view = backend.CreateBufferSrv(device, new(_entities, new(0, _entities.Info.Size), StructureStride: 8));
    }

    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings) =>
        bindings.Add(_bindingName, [ResourceBinding.ReadOnlyBuffer(_view)]);
    public void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.AddRange(_queryAccesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_entities), 0, (long)_entities.Info.Size));
    }
    public void Capture()
    {
        uint membership = _world.MembershipVersion(_rows);
        var changes = new SourceChanges(_selection, !_hasCapturedMembers || membership != _membershipVersion);
        _world.ExecuteReadQuery(_rows, _lastSystemVersion, ref changes,
            static (scoped QueryCursor rows, ref SourceChanges changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (!chunk.Has<DeviceConstraintBinding<TData>>()
                        || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<TData>>()
                        || changes.Selection.HasChangedInputs(chunk))
                    { changes.Changed = true; break; }
            });
        if (!changes.Changed)
        {
            _lastSystemVersion = changes.Version;
            return;
        }
        using MappedBuffer mapping = _backend.Map(_entities, MapType.Write, BufferRange.Whole);
        mapping.Bytes.Clear();
        var selected = new ReadRows(_data, _selection, mapping);
        _world.ExecuteReadQuery(_rows, ref selected,
            static (scoped QueryCursor rows, ref ReadRows state) => state.Read(rows));
        mapping.Flush(mapping.Range);
        _hasCapturedMembers = true; _membershipVersion = membership; _lastSystemVersion = changes.Version;
    }
    private struct SourceChanges(TSelection selection, bool changed)
    {
        internal TSelection Selection = selection;
        internal bool Changed = changed;
        internal uint Version;
    }
    private readonly ref struct ReadRows(DeviceConstraintData<TData> data, TSelection selection, MappedBuffer mapping)
    {
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (!row.Has<TComponent>() || !selection.Matches(row) || !row.TryRead<DeviceConstraintBinding<TData>>(out var binding)
                    || !ReferenceEquals(binding.Data, data)) continue;
                Span<uint> entities = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
                Entity entity = row.Entity;
                entities[binding.Index * 2] = (uint)entity.Index;
                entities[binding.Index * 2 + 1] = (uint)entity.Generation;
            }
        }
    }
    public void Dispose() { _world.ReleaseQuery(_rows); _view.Dispose(); _entities.Dispose(); }
}
