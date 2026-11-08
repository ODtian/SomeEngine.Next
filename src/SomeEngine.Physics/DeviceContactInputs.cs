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
    private readonly QueryKey _sourceKey;
    private readonly DeviceConstraintData<DeviceContactConstraint> _data;
    private readonly DeviceContactBodies _anchors;
    private readonly DeviceContactSource _source;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly Dictionary<Entity, ContactSourceVersions> _versions = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<Entity> _removed = [];
    private readonly HashSet<int> _activeIndices = [];
    private readonly PhysicsQueryWindow _window;
    private uint _baseline;
    private bool _hasSourceVersions;
    private bool _refreshBindings;
    private bool _refreshTriggers;
    private bool _useLifecycleChanges;
    private uint _membershipVersion;
    private (uint Point, uint Material, uint Endpoint, uint Binding, uint Trigger, uint Body) _sourceVersions;
    private readonly List<int> _retiredIndices = [];
    private readonly List<BufferRange> _dataRanges = [], _metadataRanges = [], _anchorRanges = [];

    public DeviceContactInputs(World world, QueryHandle points, DeviceConstraintData<DeviceContactConstraint> data, DeviceContactBodies anchors)
    {
        _world = world; _data = data; _anchors = anchors; _source = new(anchors);
        _sourceKey = world.GetQueryDefinition(points).AsFilter().Key;
        QueryDefinition fields = new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Read<UndirectedRelationEndpoints<ContactPoint>>().Optional<TriggerContact>().Optional<DeviceConstraintBinding<DeviceContactConstraint>>(QueryAccess.Read).Build();
        QueryDefinition rows = world.GetQueryDefinition(points).AsReadOnly().WithAccess(fields);
        QueryDefinition access = _source.AddReads(QueryDefinition.Empty.WithAccessFrom(rows));
        _window = new(rows);
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
    internal object InputKey => _data;
    internal object SharedInputKey => _anchors;
    public UploadEditedDeviceContacts Upload(DeviceContactMatches? changes = null) => new(this, changes);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => ((IDevicePhysicsSubmission)_data).Backend;
    Device IDevicePhysicsSubmission.Device => ((IDevicePhysicsSubmission)_data).Device;
    SomeEngine.Graphics.Queue IDevicePhysicsSubmission.Queue => (((IDevicePhysicsSubmission)_data).Backend).GetQueue(((IDevicePhysicsSubmission)_data).Device, QueueType.Compute);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency, DeviceContactMatches? changes = null)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, .. _data.GetInputAccesses(scheduler, writeInputs: true),
            .. _anchors.GetInputAccesses(scheduler, writeInputs: true), JobResourceAccess.Write(scheduler.GetResourceToken(this))];
        if (changes is not null) accesses = [.. accesses, changes.ReadAccess];
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context, changes), accesses, dependency);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit() => Submit(default);
    private QueueCompletion Submit(in PhysicsStepContext context, DeviceContactMatches? changes = null)
    {
        _baseline = _window.Begin(context);
        _dataRanges.Clear(); _metadataRanges.Clear(); _anchorRanges.Clear();
        _useLifecycleChanges = _hasSourceVersions && !_window.UsesVersions
            && changes?.ProvidesLifecycleChanges(_world, _sourceKey, _data) == true;
        uint membership = _world.MembershipVersion(_rows);
        var sourceVersions = (_world.ComponentWriteVersion<ContactPoint>(), _world.ComponentWriteVersion<ContactMaterial>(),
            _world.ComponentWriteVersion<UndirectedRelationEndpoints<ContactPoint>>(),
            _world.ComponentWriteVersion<DeviceConstraintBinding<DeviceContactConstraint>>(), _world.ComponentWriteVersion<TriggerContact>(),
            _world.ComponentWriteVersion<DeviceBodyBinding>());
        bool changed = !_hasSourceVersions || membership != _membershipVersion || sourceVersions != _sourceVersions || _window.UsesVersions;
        _refreshBindings = !_hasSourceVersions || sourceVersions.Item6 != _sourceVersions.Body;
        _refreshTriggers = !_hasSourceVersions || sourceVersions.Item5 != _sourceVersions.Trigger;
        if (!changed)
            _world.ExecuteReadQuery(_rows, _baseline, ref changed, static (scoped QueryCursor rows, ref bool changed) =>
            {
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.HasBufferChangedSinceLastSystemVersion<ContactBodyLink>()) { changed = true; break; }
            });
        if (!changed)
        {
            _data.SubmitInputRanges([], []);
            return _anchors.SubmitInputRanges([]);
        }
        _seen.Clear(); _removed.Clear(); _retiredIndices.Clear(); _activeIndices.Clear();
        if (_useLifecycleChanges)
            foreach (Entity entity in changes!.RemovedPoints)
                if (_versions.Remove(entity, out ContactSourceVersions previous))
                {
                    _data.ClearInputMetadata(previous.Index);
                    _metadataRanges.Add(new((ulong)previous.Index * 16, 16));
                }
        var read = new ReadAccess(this);
        _world.ExecuteReadQuery(_access, ref read, static (scoped QueryCursor rows, ref ReadAccess state) => state.Execute(rows));
        if (!_useLifecycleChanges)
            foreach (var previous in _versions)
                if (_window.UsesVersions ? !_world.IsAlive(previous.Key) : !_seen.Contains(previous.Key)) { _removed.Add(previous.Key); _retiredIndices.Add(previous.Value.Index); }
        foreach (Entity entity in _removed) _versions.Remove(entity);
        foreach (int index in _retiredIndices)
        {
            if (_activeIndices.Contains(index)) continue;
            _data.ClearInputMetadata(index); _metadataRanges.Add(new((ulong)index * 16, 16));
        }
        DeviceInputPages.MergeRanges(_dataRanges); DeviceInputPages.MergeRanges(_metadataRanges); DeviceInputPages.MergeRanges(_anchorRanges);
        _data.SubmitInputRanges(CollectionsMarshal.AsSpan(_dataRanges), CollectionsMarshal.AsSpan(_metadataRanges));
        _hasSourceVersions = true; _membershipVersion = membership; _sourceVersions = sourceVersions;
        return _anchors.SubmitInputRanges(CollectionsMarshal.AsSpan(_anchorRanges));
    }
    private void Read(scoped QueryRow row, scoped QueryCursor access)
    {
        if (!row.TryRead<DeviceConstraintBinding<DeviceContactConstraint>>(out var binding) || !ReferenceEquals(binding.Data, _data)) return;
        Entity entity = row.Entity; int index = binding.Index;
        if (!_useLifecycleChanges) _seen.Add(entity);
        _activeIndices.Add(index);
        var version = new ContactSourceVersions(index, row.WriteVersion<ContactPoint>(), row.WriteVersion<ContactMaterial>(),
            row.ReadBuffer<ContactBodyLink>().WriteVersions, row.WriteVersion<UndirectedRelationEndpoints<ContactPoint>>(), row.Has<TriggerContact>());
        bool existed = _versions.TryGetValue(entity, out ContactSourceVersions previous);
        bool created = !existed || previous.Index != index;
        if (existed && previous.Index != index) _retiredIndices.Add(previous.Index);
        bool geometry = created || previous.Point != version.Point || previous.Endpoints != version.Endpoints || previous.Trigger != version.Trigger;
        bool anchors = created || previous.Links != version.Links
            || _anchors.HaveBodyBindingsChanged(index, row.ReadBuffer<ContactBodyLink>().AsSpan(), access);
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
    private readonly struct Submission(DeviceContactInputs inputs, PhysicsStepContext context, DeviceContactMatches? changes) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => ((IDevicePhysicsSubmission)inputs._data).Backend;
        public Device Device => ((IDevicePhysicsSubmission)inputs._data).Device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => inputs.Submit(in context, changes);
    }
    private readonly struct ReadAccess(DeviceContactInputs inputs) : IQueryExecution
    {
        public void Execute(scoped QueryCursor access)
        {
            var read = new ReadRows(inputs, access);
            inputs._world.ExecuteReadQuery(inputs._rows, inputs._baseline, ref read, static (scoped QueryCursor rows, ref ReadRows state) => state.Execute(rows));
        }
    }
    private readonly ref struct ReadRows(DeviceContactInputs inputs, QueryCursor access) : IQueryExecution
    {
        private readonly QueryCursor _access = access;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                if (!chunk.Has<DeviceConstraintBinding<DeviceContactConstraint>>()) continue;
                bool changed = inputs._refreshBindings || inputs._refreshTriggers || inputs._window.UsesVersions
                    || chunk.HasChangedSinceLastSystemVersion<ContactPoint>() || chunk.HasChangedSinceLastSystemVersion<ContactMaterial>()
                    || chunk.HasChangedSinceLastSystemVersion<UndirectedRelationEndpoints<ContactPoint>>()
                    || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<DeviceContactConstraint>>()
                    || chunk.HasBufferChangedSinceLastSystemVersion<ContactBodyLink>();
                if (!changed)
                {
                    if (inputs._useLifecycleChanges) continue;
                    ReadOnlySpan<Entity> entities = chunk.Entities;
                    bool trigger = chunk.Has<TriggerContact>();
                    foreach (int index in chunk.RowIndices)
                    {
                        Entity entity = entities[index];
                        if (!inputs._versions.TryGetValue(entity, out ContactSourceVersions version) || version.Trigger != trigger)
                        { changed = true; break; }
                        inputs._seen.Add(entity); inputs._activeIndices.Add(version.Index);
                    }
                }
                if (changed) foreach (QueryRow row in chunk.Rows) inputs.Read(row, _access);
            }
            inputs._window.Accept(rows.CurrentSystemVersion);
        }
    }
    private readonly record struct ContactSourceVersions(int Index, uint Point, uint Material,
        (uint Header, uint Inline) Links, uint Endpoints, bool Trigger, uint InputVersion = 0);
    public void Dispose() { _world.ReleaseQuery(_rows); _world.ReleaseQuery(_access); }
}
public readonly struct UploadEditedDeviceContacts(DeviceContactInputs inputs, DeviceContactMatches? changes = null) : IDevicePhysicsStage
{
    public object InputKey => inputs.InputKey;
    public object SharedInputKey => inputs.SharedInputKey;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency, changes);
}
