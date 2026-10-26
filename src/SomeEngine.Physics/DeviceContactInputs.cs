using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Transfers actual component edits and membership changes into continuing device contact parameters.</summary>
public sealed class DeviceContactInputs : IDisposable, IDevicePhysicsSubmission
{
    private readonly World _world;
    private readonly QueryHandle _rows, _access;
    private readonly DeviceConstraintData<DeviceContactConstraint> _data;
    private readonly DeviceContactBodies _anchors;
    private readonly DeviceContactSource _source;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly Dictionary<Entity, ContactSourceVersions> _versions = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<Entity> _removed = [];
    private readonly bool[] _activeIndices;
    private readonly List<int> _retiredIndices = [];
    private readonly List<BufferRange> _dataRanges = [], _metadataRanges = [], _anchorRanges = [];

    public DeviceContactInputs(World world, QueryHandle points, DeviceConstraintData<DeviceContactConstraint> data, DeviceContactBodies anchors)
    {
        _world = world; _data = data; _anchors = anchors; _source = new(anchors); _activeIndices = new bool[data.Capacity];
        QueryDefinition fields = new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Read<UndirectedRelationEndpoints<ContactPoint>>().Optional<DeviceConstraintBinding<DeviceContactConstraint>>(QueryAccess.Read).Build();
        QueryDefinition rows = world.GetQueryDefinition(points).AsReadOnly().WithAccess(fields);
        QueryDefinition access = _source.AddReads(QueryDefinition.Empty.WithAccessFrom(rows));
        _rows = world.Query(rows); _access = world.Query(access);
        var resources = new QueryReadResources();
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        resources.Add<DeviceConstraintBinding<DeviceContactConstraint>, DeviceConstraintReferenceResources<DeviceContactConstraint>>();
        var accesses = new List<JobResourceAccess>();
        foreach (QueryDefinition definition in new[] { rows, access })
        {
            var values = new JobResourceAccess[QueryJobAccess.Count(definition)];
            QueryJobAccess.CopyAccesses(world, definition, values, resources); accesses.AddRange(values);
        }
        _queryAccesses = accesses.ToArray();
    }
    public UploadEditedDeviceContacts Upload() => new(this);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => ((IDevicePhysicsSubmission)_data).Backend;
    Device IDevicePhysicsSubmission.Device => ((IDevicePhysicsSubmission)_data).Device;
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, .. _data.GetInputAccesses(scheduler, writeInputs: true),
            .. _anchors.GetInputAccesses(scheduler, writeInputs: true), JobResourceAccess.Write(scheduler.GetResourceToken(this))];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, dependency);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _seen.Clear(); _removed.Clear(); _retiredIndices.Clear(); Array.Clear(_activeIndices);
        _dataRanges.Clear(); _metadataRanges.Clear(); _anchorRanges.Clear();
        var read = new ReadAccess(this);
        _world.ExecuteReadQuery(_access, ref read, static (scoped QueryCursor rows, ref ReadAccess state) => state.Execute(rows));
        foreach (var previous in _versions)
            if (!_seen.Contains(previous.Key)) { _removed.Add(previous.Key); _retiredIndices.Add(previous.Value.Index); }
        foreach (Entity entity in _removed) _versions.Remove(entity);
        foreach (int index in _retiredIndices)
        {
            if (_activeIndices[index]) continue;
            _data.ClearInputMetadata(index); _metadataRanges.Add(new((ulong)index * 16, 16));
        }
        Merge(_dataRanges); Merge(_metadataRanges); Merge(_anchorRanges);
        _data.SubmitInputRanges(CollectionsMarshal.AsSpan(_dataRanges), CollectionsMarshal.AsSpan(_metadataRanges));
        return _anchors.SubmitInputRanges(CollectionsMarshal.AsSpan(_anchorRanges));
    }
    private void Read(scoped QueryRow row, scoped QueryCursor access)
    {
        if (!row.TryRead<DeviceConstraintBinding<DeviceContactConstraint>>(out var binding) || !ReferenceEquals(binding.Data, _data)) return;
        Entity entity = row.Entity; int index = binding.Index;
        _seen.Add(entity); _activeIndices[index] = true;
        var version = new ContactSourceVersions(index, row.WriteVersion<ContactPoint>(), row.WriteVersion<ContactMaterial>(),
            row.ReadBuffer<ContactBodyLink>().WriteVersions, row.WriteVersion<UndirectedRelationEndpoints<ContactPoint>>());
        bool existed = _versions.TryGetValue(entity, out ContactSourceVersions previous);
        bool created = !existed || previous.Index != index;
        if (existed && previous.Index != index) _retiredIndices.Add(previous.Index);
        bool geometry = created || previous.Point != version.Point || previous.Endpoints != version.Endpoints;
        bool anchors = created || previous.Links != version.Links;
        bool materialChanged = created || previous.Material != version.Material;
        bool changed = geometry || anchors || materialChanged;
        version = version with { InputVersion = changed ? previous.InputVersion + 1 : previous.InputVersion };
        if (geometry || anchors)
        {
            ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
            DeviceContactConstraint data = _source.Read(in point, row, access);
            _data.WriteInput(index, entity, in data);
            if (geometry) _dataRanges.Add(new((ulong)index * 128, 128));
            else _dataRanges.Add(new((ulong)index * 128, 12));
            if (anchors)
                _anchorRanges.Add(new((ulong)(index * _anchors.BodiesPerContact) * 32, (ulong)row.ReadBuffer<ContactBodyLink>().Count * 32));
        }
        if (!geometry && materialChanged)
        {
            ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
            _data.WriteInputField(index, 44, in material); _dataRanges.Add(new((ulong)index * 128 + 44, 24));
        }
        if (changed)
        {
            _data.WriteInputMetadata(index, entity, version.InputVersion);
            _metadataRanges.Add(new((ulong)index * 16, 16));
        }
        _versions[entity] = version;
    }
    private readonly struct ReadAccess(DeviceContactInputs inputs) : IQueryExecution
    {
        public void Execute(scoped QueryCursor access)
        {
            var read = new ReadRows(inputs, access);
            inputs._world.ExecuteReadQuery(inputs._rows, ref read, static (scoped QueryCursor rows, ref ReadRows state) => state.Execute(rows));
        }
    }
    private readonly ref struct ReadRows(DeviceContactInputs inputs, QueryCursor access) : IQueryExecution
    {
        private readonly QueryCursor _access = access;
        public void Execute(scoped QueryCursor rows)
        { foreach (QueryRow row in rows.Rows) inputs.Read(row, _access); }
    }
    private static void Merge(List<BufferRange> ranges)
    {
        ranges.Sort(static (first, second) => first.Offset.CompareTo(second.Offset));
        int count = 0;
        foreach (BufferRange range in CollectionsMarshal.AsSpan(ranges))
        {
            if (count != 0 && ranges[count - 1].Offset + ranges[count - 1].Size >= range.Offset)
            {
                BufferRange previous = ranges[count - 1];
                ranges[count - 1] = new(previous.Offset, Math.Max(previous.Offset + previous.Size, range.Offset + range.Size) - previous.Offset);
            }
            else ranges[count++] = range;
        }
        if (count < ranges.Count) ranges.RemoveRange(count, ranges.Count - count);
    }
    private readonly record struct ContactSourceVersions(int Index, uint Point, uint Material,
        (uint Header, uint Inline) Links, uint Endpoints, uint InputVersion = 0);
    public void Dispose() { _world.ReleaseQuery(_rows); _world.ReleaseQuery(_access); }
}
public readonly struct UploadEditedDeviceContacts(DeviceContactInputs inputs) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
