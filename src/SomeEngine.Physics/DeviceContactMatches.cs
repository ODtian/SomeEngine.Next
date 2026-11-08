using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceContactMatchKernels(ComputePipelineDesc Clear, ComputePipelineDesc Index,
    ComputePipelineDesc Match, ComputePipelineDesc Claim, ComputePipelineDesc NewInputs, ComputePipelineDesc RemovedRows);

/// <summary>Matches current device manifolds to the original point entities and their selected parameter rows.</summary>
public sealed partial class DeviceContactMatches : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Scheduler _scheduler;
    private readonly World _world;
    private readonly QueryHandle _pointRows;
    private readonly QueryKey _sourceKey;
    private readonly JobResourceAccess[] _pointAccesses;
    private readonly DeviceConstraintData<DeviceContactConstraint> _parameters;
    private readonly DeviceCollisionDetection _detection;
    private readonly DeviceBodies _bodies;
    private readonly DeviceContactBodies _anchors;
    private readonly Buffer _inputBodyCounts, _inputBodies;
    private readonly ContactRefreshSettings _settings;
    private readonly int _bucketCount, _threadsPerGroup;
    private readonly DeviceConstraintSelection<DeviceConstraintBinding<DeviceContactConstraint>, DeviceContactConstraint> _selection;
    private readonly bool _ownsSelection;
    private readonly Buffer _heads, _next, _matches, _rows, _settingsBuffer;
    private readonly Buffer _claims, _newInputRows, _newReferences, _removedRows, _changeCounts, _matchingEntities;
    private readonly DeviceInputPages _rowInputs;
    private readonly List<BufferRange> _rowRanges = [];
    private readonly DeviceInputPages _identityInputs;
    private readonly List<BufferRange> _identityRanges = [];
    private Buffer _readback;
    private Buffer _changesReadback;
    private readonly BufferUav _headsView, _nextView, _matchesView, _rowsView;
    private readonly BufferUav _claimsView, _newInputRowsView, _newReferencesView, _removedRowsView, _matchingEntitiesView;
    private bool _identityReferences;
    private readonly BufferSrv _contactsView, _countsView, _offsetsView, _bodyCountsView, _bodiesView;
    private readonly BufferCbv _settingsView;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly JobResourceToken _resource;
    private MatchedContact[] _values = [];
    private readonly List<Entity> _removedPoints = [];
    private bool _inputsAreChanges, _hasLifecycleVersions;
    private uint _lifecycleMembership;
    private uint _lifecycleBufferVersion;
    private (uint Point, uint Material, uint Binding, uint Endpoints, uint Bodies) _lifecycleVersions;
    private bool _initialized;

    public DeviceContactMatches(IGraphicsBackend backend, Device device, World world, QueryHandle points,
        DeviceConstraintData<DeviceContactConstraint> parameters, DeviceCollisionDetection detection,
        DeviceBodies bodies, DeviceContactBodies anchors,
        DeviceContactMatchKernels kernels, ContactRefreshSettings settings, int bucketCount, int threadsPerGroup,
        DeviceConstraintSelection<DeviceConstraintBinding<DeviceContactConstraint>, DeviceContactConstraint>? selection = null)
    {
        _backend = backend; _device = device; _world = world; _scheduler = WorldJobs.GetScheduler(world);
        _sourceKey = world.GetQueryDefinition(points).AsFilter().Key;
        QueryDefinition pointReads = world.GetQueryDefinition(points).AsFilter().WithAccess(new QueryDefinitionBuilder()
            .Optional<ContactPoint>(QueryAccess.Read).Optional<UndirectedRelationEndpoints<ContactPoint>>(QueryAccess.Read)
            .OptionalBuffer<ContactBodyLink>(QueryAccess.Read).Build());
        _pointRows = world.Query(pointReads);
        _pointAccesses = new JobResourceAccess[QueryJobAccess.Count(pointReads)];
        QueryJobAccess.CopyAccesses(world, pointReads, _pointAccesses);
        _parameters = parameters; _detection = detection; _settings = settings; _bodies = bodies; _anchors = anchors;
        _inputBodyCounts = detection.ContactBodyCounts ?? detection.ContactCounts;
        _inputBodies = detection.ContactBodies ?? anchors.Values;
        _bucketCount = bucketCount; _threadsPerGroup = threadsPerGroup;
        _ownsSelection = selection is null;
        _selection = selection ?? new(backend, device, world, points, parameters, "DeviceContactMatchingSelection");
        _resource = _scheduler.CreateResourceToken("Matched contact identities and parameter indices");
        _selection.ReadContactChanges(this);
        _heads = backend.CreateBuffer(device, new((ulong)bucketCount * 4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite), MemoryType.DeviceLocal);
        var nextDescription = new BufferDesc((ulong)parameters.Capacity * 4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite);
        _next = parameters.Memory is { } memory ? memory.CreateBuffer(nextDescription, 4) : backend.CreateBuffer(device, nextDescription);
        var matchDescription = new BufferDesc((ulong)detection.Capacity.Contacts * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
        DeviceContactMemory? inputMemory = detection.Capacity.Memory ?? parameters.Memory;
        _matches = inputMemory is { } contactMemory ? contactMemory.CreateBuffer(matchDescription, 16) : backend.CreateBuffer(device, matchDescription);
        var rowDescription = new BufferDesc((ulong)detection.Capacity.Contacts * 4,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Matched contact parameter rows");
        _rows = inputMemory is { } rowMemory ? rowMemory.CreateBuffer(rowDescription, 4) : backend.CreateBuffer(device, rowDescription);
        var claimsDescription = new BufferDesc((ulong)parameters.Capacity * 8, BufferUsages.ShaderRead | BufferUsages.ShaderWrite);
        _claims = parameters.Memory is { } claimsMemory ? claimsMemory.CreateBuffer(claimsDescription, 8) : backend.CreateBuffer(device, claimsDescription);
        var identityDescription = new BufferDesc((ulong)parameters.Capacity * 8,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination);
        _matchingEntities = parameters.Memory is { } identityMemory ? identityMemory.CreateBuffer(identityDescription, 8) : backend.CreateBuffer(device, identityDescription);
        _identityInputs = new(backend, device, parameters.Capacity, 8, "Matching contact entities");
        var newInputsDescription = new BufferDesc((ulong)(detection.Capacity.Contacts + 1) * 4,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
        _newInputRows = inputMemory is { } newMemory ? newMemory.CreateBuffer(newInputsDescription, 4, 1) : backend.CreateBuffer(device, newInputsDescription);
        var referencesDescription = new BufferDesc((ulong)detection.Capacity.Contacts * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
        _newReferences = inputMemory is { } referencesMemory ? referencesMemory.CreateBuffer(referencesDescription, 16) : backend.CreateBuffer(device, referencesDescription);
        var removedRowsDescription = new BufferDesc((ulong)(parameters.Capacity + 1) * 4,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource);
        _removedRows = parameters.Memory is { } removedMemory ? removedMemory.CreateBuffer(removedRowsDescription, 4, 1) : backend.CreateBuffer(device, removedRowsDescription);
        _changeCounts = backend.CreateBuffer(device, new(8, BufferUsages.CopyDestination), MemoryType.Readback);
        _changesReadback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        _rowInputs = new(backend, device, detection.Capacity.Contacts, 4, "Matched contact row inputs");
        _readback = backend.CreateBuffer(device, new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        _settingsBuffer = backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload);
        _headsView = backend.CreateBufferUav(device, new(_heads, new(0, _heads.Info.Size), StructureStride: 4));
        _nextView = backend.CreateBufferUav(device, new(_next, new(0, _next.Info.Size), StructureStride: 4));
        _matchesView = backend.CreateBufferUav(device, new(_matches, new(0, _matches.Info.Size), StructureStride: 16));
        _rowsView = backend.CreateBufferUav(device, new(_rows, new(0, _rows.Info.Size), StructureStride: 4));
        _claimsView = backend.CreateBufferUav(device, new(_claims, new(0, _claims.Info.Size), StructureStride: 8));
        _matchingEntitiesView = backend.CreateBufferUav(device, new(_matchingEntities, new(0, _matchingEntities.Info.Size), StructureStride: 8));
        _newInputRowsView = backend.CreateBufferUav(device, new(_newInputRows, new(0, _newInputRows.Info.Size), StructureStride: 4));
        _newReferencesView = backend.CreateBufferUav(device, new(_newReferences, new(0, _newReferences.Info.Size), StructureStride: 16));
        _removedRowsView = backend.CreateBufferUav(device, new(_removedRows, new(0, _removedRows.Info.Size), StructureStride: 4));
        _contactsView = backend.CreateBufferSrv(device, new(detection.Contacts, new(0, detection.Contacts.Info.Size), StructureStride: DeviceCollisionDetection.ContactStride));
        _countsView = backend.CreateBufferSrv(device, new(detection.ContactCounts, new(0, detection.ContactCounts.Info.Size), StructureStride: 4));
        _offsetsView = backend.CreateBufferSrv(device, new(detection.ContactOffsets, new(0, detection.ContactOffsets.Info.Size), StructureStride: 4));
        _bodyCountsView = backend.CreateBufferSrv(device, new(_inputBodyCounts, new(0, _inputBodyCounts.Info.Size), StructureStride: 4));
        _bodiesView = backend.CreateBufferSrv(device, new(_inputBodies, new(0, _inputBodies.Info.Size), StructureStride: 32));
        _settingsView = backend.CreateBufferCbv(device, new(_settingsBuffer, new(0, 256)));
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["ContactMatchHeads"] = [ResourceBinding.WritableBuffer(_headsView)],
            ["ContactMatchNext"] = [ResourceBinding.WritableBuffer(_nextView)],
            ["ContactMatches"] = [ResourceBinding.WritableBuffer(_matchesView)],
            ["DeviceContactGeometryRows"] = [ResourceBinding.WritableBuffer(_rowsView)],
            ["ContactMatchClaims"] = [ResourceBinding.WritableBuffer(_claimsView)],
            ["ContactMatchingEntities"] = [ResourceBinding.WritableBuffer(_matchingEntitiesView)],
            ["ContactNewInputs"] = [ResourceBinding.WritableBuffer(_newInputRowsView)],
            ["ContactNewReferences"] = [ResourceBinding.WritableBuffer(_newReferencesView)],
            ["ContactRemovedRows"] = [ResourceBinding.WritableBuffer(_removedRowsView)],
            ["DeviceMatchInputContacts"] = [ResourceBinding.ReadOnlyBuffer(_contactsView)],
            ["DeviceMatchInputCounts"] = [ResourceBinding.ReadOnlyBuffer(_countsView)],
            ["DeviceMatchInputOffsets"] = [ResourceBinding.ReadOnlyBuffer(_offsetsView)],
            ["DeviceMatchInputBodyCounts"] = [ResourceBinding.ReadOnlyBuffer(_bodyCountsView)],
            ["DeviceMatchInputBodies"] = [ResourceBinding.ReadOnlyBuffer(_bodiesView)],
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
            ["ContactMatchSettings"] = [ResourceBinding.ConstantBuffer(_settingsView)],
        };
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); _selection.AddBindings(bindings, "DeviceContactMatchingSelection");
        ComputePipelineDesc[] descriptions = [kernels.Clear, kernels.Index, kernels.Match, kernels.Claim, kernels.NewInputs, kernels.RemovedRows];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device contact matching"));
    }
    public MatchDeviceContacts Match() => new(this);
    public DeviceContactMatchResults Results => new(this);
    public DeviceMatchedContactComponents<TComponents> Components<TComponents>(TComponents components)
        where TComponents : struct, IContactPointComponents => new(this, components);
    public DeviceContactGeometry CreateGeometryUpdate(DeviceBodies bodies, DeviceContactBodies anchors, ComputePipelineDesc kernel) =>
        new(_backend, _device, this, _parameters, _detection, bodies, anchors, kernel);
    internal Buffer RowBuffer => _rows;
    internal BufferUav RowView => _rowsView;
    internal bool RetainsBodyReferences => _detection.ReadsContactChanges;
    internal bool InputsAreChanges => _inputsAreChanges;
    internal ReadOnlySpan<Entity> RemovedPoints => CollectionsMarshal.AsSpan(_removedPoints);
    internal ReadOnlySpan<int> RemovedParameterRows => CollectionsMarshal.AsSpan(_removedParameterRows);
    internal long ChangesNumber { get; private set; }
    internal bool ProvidesLifecycleChanges(World world, QueryKey sourceKey, object parameters) =>
        _inputsAreChanges && ReferenceEquals(world, _world) && ReferenceEquals(parameters, _parameters)
        && sourceKey.Equals(_sourceKey);
    internal JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);
    internal void AddSourceAccesses(List<JobResourceAccess> accesses) => accesses.AddRange(_pointAccesses);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    SomeEngine.Graphics.Queue IDevicePhysicsSubmission.Queue => _backend.GetQueue(_device, QueueType.Compute);

    internal DeviceConstraintBinding<DeviceContactConstraint> Allocate(Entity point)
    {
        int index = FindRow(point);
        if (index >= 0) return new(_parameters, index);
        index = _parameters.ReserveRow();
        StoreIdentity(index, point);
        return new(_parameters, index);
    }
    internal bool Read(int input, out Entity point, out float distanceSquared)
    {
        if (_inputsAreChanges) { point = Entity.Null; distanceSquared = 0; return true; }
        point = _values[input].Point; distanceSquared = _values[input].DistanceSquared; return true;
    }
    internal void Record(int input, Entity point)
    {
        if (_inputsAreChanges)
        {
            WriteNewRow(_newInputs[input], Allocate(point).Index);
            return;
        }
        MatchedContact value = _values[input];
        if (value.Point == point) return;
        if (value.Point != Entity.Null) ReleaseRow((int)value.ParameterIndex);
        int row = Allocate(point).Index;
        _values[input] = new(point, (uint)row, value.DistanceSquared);
        WriteNewRow(_detection.ReadsContactChanges ? input : _inputRows[input], row);
    }
    private void WriteNewRow(int input, int row)
    {
        uint value = (uint)row | 0x80000000u;
        MemoryMarshal.Write(_rowInputs.Row(input), in value);
        _rowRanges.Add(new((ulong)input * 4, 4));
    }

    private (uint Point, uint Material, uint Binding, uint Endpoints, uint Bodies) ReadLifecycleVersions() =>
        (_world.ComponentWriteVersion<ContactPoint>(), _world.ComponentWriteVersion<ContactMaterial>(),
            _world.ComponentWriteVersion<DeviceConstraintBinding<DeviceContactConstraint>>(),
            _world.ComponentWriteVersion<UndirectedRelationEndpoints<ContactPoint>>(),
            _world.ComponentWriteVersion<DeviceBodyBinding>());

    private void AcceptLifecycleVersions()
    {
        _lifecycleMembership = _selection.SourceMembershipVersion;
        _lifecycleVersions = ReadLifecycleVersions();
        _world.ExecuteReadQuery(_pointRows, ref _lifecycleBufferVersion,
            static (scoped QueryCursor rows, ref uint version) => version = rows.CurrentSystemVersion);
        _hasLifecycleVersions = true;
    }

    private bool HaveBodyReferencesChanged()
    {
        bool changed = false;
        _world.ExecuteReadQuery(_pointRows, _lifecycleBufferVersion, ref changed,
            static (scoped QueryCursor rows, ref bool changed) =>
            {
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.HasBuffer<ContactBodyLink>() && chunk.HasBufferChangedSinceLastSystemVersion<ContactBodyLink>())
                    { changed = true; break; }
            });
        return changed;
    }

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var accesses = new List<JobResourceAccess>(_parameters.GetReadAccesses(_scheduler)) { WriteAccess, _parameters.GetRowAllocationAccess(_scheduler) };
        AddSourceAccesses(accesses);
        accesses.AddRange(_detection.GetReadbackAccesses(_scheduler));
        if (_detection.ReadsContactChanges) accesses.Add(_detection.GetChangesWriteAccess(_scheduler));
        _selection.AddAccesses(_scheduler, accesses);
        accesses.AddRange([
            _bodies.GetReadAccess(_scheduler), _anchors.GetReadAccess(_scheduler),
            JobResourceAccess.Read(_scheduler.GetResourceToken(_inputBodyCounts), 0, (long)_inputBodyCounts.Info.Size),
            JobResourceAccess.Read(_scheduler.GetResourceToken(_inputBodies), 0, (long)_inputBodies.Info.Size),
            JobResourceAccess.Read(_scheduler.GetResourceToken(_detection.Contacts), 0, (long)_detection.Contacts.Info.Size),
            JobResourceAccess.Read(_scheduler.GetResourceToken(_detection.ContactOffsets), 0, (long)_detection.ContactOffsets.Info.Size),
            JobResourceAccess.Read(_scheduler.GetResourceToken(_detection.ContactCounts), 0, (long)_detection.ContactCounts.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_heads), 0, (long)_heads.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_next), 0, (long)_next.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_matches), 0, (long)_matches.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_rows), 0, (long)_rows.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_claims), 0, (long)_claims.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_matchingEntities), 0, (long)_matchingEntities.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_newInputRows), 0, (long)_newInputRows.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_newReferences), 0, (long)_newReferences.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_removedRows), 0, (long)_removedRows.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_changeCounts), 0, 8),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_changesReadback), 0, (long)_changesReadback.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_settingsBuffer), 0, 256),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_commands))]);
        JobHandle matched = DevicePhysicsJobs.Schedule(_scheduler, new Submission(this, context), accesses.ToArray(), dependency);
        var reads = new List<JobResourceAccess>(_detection.GetReadbackAccesses(_scheduler))
        { JobResourceAccess.Read(_scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size), WriteAccess };
        return _scheduler.Schedule(new Accept(this), reads.ToArray(), matched);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit() => Submit(default);
    private QueueCompletion Submit(in PhysicsStepContext context)
    {
        _parameters.Memory?.EnsureRows(Math.Max(_rowCount, _detection.ContactCount));
        bool unchanged = _hasLifecycleVersions && _lifecycleMembership == _selection.SourceMembershipVersion
            && _lifecycleVersions == ReadLifecycleVersions() && !HaveBodyReferencesChanged();
        _inputsAreChanges &= unchanged;
        _selection.Capture(in context);
        _inputsAreChanges = _detection.ReadsContactChanges && (_identityReferences || !_selection.UsesVersionFilters
            && (unchanged || !_hasLifecycleVersions && _selection.Count == 0));
        var settings = new MatchSettings((uint)_parameters.RowCount, (uint)_detection.Count, (uint)_bucketCount,
            _settings.MatchDistance * _settings.MatchDistance, _settings.MinimumNormalDot,
            _detection.ContactBodies is null ? 0u : (uint)_detection.Capacity.BodiesPerContact, (uint)_detection.ContactCount);
        using (MappedBuffer mapping = _backend.Map(_settingsBuffer, MapType.Write, BufferRange.Whole))
        { MemoryMarshal.Write(mapping.Bytes, in settings); mapping.Flush(new(0, (ulong)Marshal.SizeOf<MatchSettings>())); }
        if (!_inputsAreChanges && _readback.Info.Size < (ulong)_detection.ContactCount * 16)
        {
            _readback.Dispose();
            _readback = _backend.CreateBuffer(_device, new((ulong)_detection.ContactCount * 16, BufferUsages.CopyDestination), MemoryType.Readback);
        }
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized && _parameters.Memory?.Contains(_matchingEntities) != true)
        {
            _backend.Barrier(_commands, new BufferBarrier(_matchingEntities, _matchingEntities.InitialSync, PipelineSync.Copy,
                _matchingEntities.InitialAccess, ResourceAccess.CopyDestination));
            _identityInputs.Zero(_commands, _matchingEntities);
            _backend.Barrier(_commands, new BufferBarrier(_matchingEntities, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        if (!_initialized)
            foreach (Buffer buffer in new[] { _heads, _next, _matches, _rows, _claims, _newInputRows, _newReferences, _removedRows })
                if (_parameters.Memory?.Contains(buffer) != true && _detection.Capacity.Memory?.Contains(buffer) != true)
                    _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        uint[] counts = [(uint)Math.Max(_bucketCount, _parameters.RowCount), (uint)_parameters.RowCount,
            (uint)_detection.Count, (uint)_detection.ContactCount, (uint)_detection.ContactCount, (uint)_parameters.RowCount];
        for (int kernel = 0; kernel < _pipelines.Length; kernel++)
        {
            _backend.SetPipeline(_commands, _pipelines[kernel]);
            _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
            if (counts[kernel] != 0)
                _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups(
                    (int)((counts[kernel] + (uint)_threadsPerGroup - 1) / (uint)_threadsPerGroup)));
            _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        }
        if (!_inputsAreChanges && _detection.ContactCount != 0)
        {
            ulong bytes = (ulong)_detection.ContactCount * 16;
            _backend.Barrier(_commands, new BufferBarrier(_matches, PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            _backend.CopyBuffer(_commands, new(_matches, 0, _readback, 0, bytes));
            _backend.Barrier(_commands, new BufferBarrier(_matches, PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        }
        if (_inputsAreChanges)
        {
            foreach (Buffer buffer in new[] { _newInputRows, _removedRows })
                _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            _backend.CopyBuffer(_commands, new(_newInputRows, 0, _changeCounts, 0, 4));
            _backend.CopyBuffer(_commands, new(_removedRows, 0, _changeCounts, 4, 4));
            foreach (Buffer buffer in new[] { _newInputRows, _removedRows })
                _backend.Barrier(_commands, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                    ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        }
        using RecordedCommands recorded = _backend.End(_commands); _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct Submission(DeviceContactMatches matches, PhysicsStepContext context) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => matches._backend;
        public Device Device => matches._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => matches.SubmitAsync(context).AsTask().GetAwaiter().GetResult();
        public ValueTask<QueueCompletion> SubmitAsync() => matches.SubmitAsync(context);
    }
    private readonly struct Accept(DeviceContactMatches matches) : IJob
    {
        public void Execute()
        {
            if (!matches._detection.ReadsContactChanges) matches.AcceptMatches();
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly struct MatchedContact
    {
        public readonly Entity Point; public readonly uint ParameterIndex; public readonly float DistanceSquared;
        public MatchedContact(Entity point, uint parameterIndex, float distanceSquared) =>
            (Point, ParameterIndex, DistanceSquared) = (point, parameterIndex, distanceSquared);
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct MatchSettings(uint Points, uint Pairs, uint Buckets, float SquaredDistance, float NormalDot,
        uint InputBodiesPerContact, uint Inputs);
    public void Dispose()
    {
        _world.ReleaseQuery(_pointRows);
        if (_ownsSelection) _selection.Dispose();
        _scheduler.ReleaseResourceToken(_resource); _commands.Dispose(); _rowInputs.Dispose();
        _identityInputs.Dispose();
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _headsView.Dispose(); _nextView.Dispose(); _matchesView.Dispose(); _rowsView.Dispose(); _contactsView.Dispose(); _countsView.Dispose(); _offsetsView.Dispose(); _bodyCountsView.Dispose(); _bodiesView.Dispose(); _settingsView.Dispose();
        _claimsView.Dispose(); _newInputRowsView.Dispose(); _newReferencesView.Dispose(); _removedRowsView.Dispose();
        _matchingEntitiesView.Dispose();
        _heads.Dispose(); _next.Dispose(); _matches.Dispose(); _rows.Dispose(); _readback.Dispose(); _settingsBuffer.Dispose();
        _claims.Dispose(); _newInputRows.Dispose(); _newReferences.Dispose(); _removedRows.Dispose(); _changeCounts.Dispose(); _changesReadback.Dispose();
        _matchingEntities.Dispose();
    }
}
public readonly struct MatchDeviceContacts(DeviceContactMatches matches) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => matches.Schedule(in context, dependency);
}
public readonly struct DeviceContactMatchResults(DeviceContactMatches matches) : IContactPointMatches
{
    public bool InputsAreChanges => matches.InputsAreChanges;
    public ReadOnlySpan<Entity> RemovedPoints => matches.RemovedPoints;
    public void AddAccesses(List<JobResourceAccess> accesses) => accesses.Add(matches.WriteAccess);
    public bool TryGetMatch(int input, out Entity point, out float distanceSquared) => matches.Read(input, out point, out distanceSquared);
    public void RecordPoint(int input, Entity point) => matches.Record(input, point);
}
public readonly struct DeviceMatchedContactComponents<TComponents>(DeviceContactMatches matches, TComponents components) : IContactPointComponents
    where TComponents : struct, IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses)
    { components.AddReads(world, accesses); accesses.Add(matches.WriteAccess); }
    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
    {
        components.Add(ref commands, point, in geometry, first, second, in context);
        commands.Add(point, matches.Allocate(point));
    }
}
