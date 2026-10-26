using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceContactMatchKernels(ComputePipelineDesc Clear, ComputePipelineDesc Index, ComputePipelineDesc Match);

/// <summary>Matches current device manifolds to the original point entities and their selected parameter rows.</summary>
public sealed class DeviceContactMatches : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Scheduler _scheduler;
    private readonly DeviceConstraintData<DeviceContactConstraint> _parameters;
    private readonly DeviceCollisionDetection _detection;
    private readonly ContactRefreshSettings _settings;
    private readonly int _bucketCount, _threadsPerGroup;
    private readonly DeviceConstraintSelection<ContactPoint, DeviceContactConstraint> _selection;
    private readonly Buffer _heads, _next, _matches, _readback, _settingsBuffer;
    private readonly BufferUav _headsView, _nextView, _matchesView;
    private readonly BufferSrv _contactsView, _countsView;
    private readonly BufferCbv _settingsView;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly JobResourceToken _resource;
    private readonly MatchedContact[] _values;
    private readonly bool[] _used;
    private readonly Dictionary<Entity, uint> _pointIndices = [];
    private int _nextIndex;
    private bool _initialized;

    public DeviceContactMatches(IGraphicsBackend backend, Device device, World world, QueryHandle points,
        DeviceConstraintData<DeviceContactConstraint> parameters, DeviceCollisionDetection detection,
        DeviceContactMatchKernels kernels, ContactRefreshSettings settings, int bucketCount, int threadsPerGroup)
    {
        _backend = backend; _device = device; _scheduler = WorldJobs.GetScheduler(world);
        _parameters = parameters; _detection = detection; _settings = settings;
        _bucketCount = bucketCount; _threadsPerGroup = threadsPerGroup;
        _selection = new(backend, device, world, points, parameters, "DeviceContactMatchingSelection");
        _resource = _scheduler.CreateResourceToken("Matched contact identities and parameter indices");
        _values = new MatchedContact[detection.Capacity.Pairs * 4]; _used = new bool[parameters.Capacity];
        _heads = backend.CreateBuffer(device, new((ulong)bucketCount * 4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite), MemoryType.DeviceLocal);
        _next = backend.CreateBuffer(device, new((ulong)parameters.Capacity * 4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite), MemoryType.DeviceLocal);
        _matches = backend.CreateBuffer(device, new((ulong)_values.Length * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
        _readback = backend.CreateBuffer(device, new(_matches.Info.Size, BufferUsages.CopyDestination), MemoryType.Readback);
        _settingsBuffer = backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload);
        _headsView = backend.CreateBufferUav(device, new(_heads, new(0, _heads.Info.Size), StructureStride: 4));
        _nextView = backend.CreateBufferUav(device, new(_next, new(0, _next.Info.Size), StructureStride: 4));
        _matchesView = backend.CreateBufferUav(device, new(_matches, new(0, _matches.Info.Size), StructureStride: 16));
        _contactsView = backend.CreateBufferSrv(device, new(detection.Contacts, new(0, detection.Contacts.Info.Size), StructureStride: DeviceCollisionDetection.ContactStride));
        _countsView = backend.CreateBufferSrv(device, new(detection.ContactCounts, new(0, detection.ContactCounts.Info.Size), StructureStride: 4));
        _settingsView = backend.CreateBufferCbv(device, new(_settingsBuffer, new(0, 256)));
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["ContactMatchHeads"] = [ResourceBinding.WritableBuffer(_headsView)],
            ["ContactMatchNext"] = [ResourceBinding.WritableBuffer(_nextView)],
            ["ContactMatches"] = [ResourceBinding.WritableBuffer(_matchesView)],
            ["DeviceMatchInputContacts"] = [ResourceBinding.ReadOnlyBuffer(_contactsView)],
            ["DeviceMatchInputCounts"] = [ResourceBinding.ReadOnlyBuffer(_countsView)],
            ["ContactMatchSettings"] = [ResourceBinding.ConstantBuffer(_settingsView)],
        };
        parameters.AddBindings(bindings); _selection.AddBindings(bindings);
        ComputePipelineDesc[] descriptions = [kernels.Clear, kernels.Index, kernels.Match];
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
    internal JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;

    internal DeviceConstraintBinding<DeviceContactConstraint> Allocate(Entity point)
    {
        while (_used[_nextIndex]) _nextIndex++;
        int index = _nextIndex++; _used[index] = true;
        _pointIndices[point] = (uint)index;
        return new(_parameters, index);
    }
    internal bool Read(int input, out Entity point, out float distanceSquared)
    { point = _values[input].Point; distanceSquared = _values[input].DistanceSquared; return true; }
    internal void Record(int input, Entity point)
    {
        MatchedContact value = _values[input];
        if (value.Point != Entity.Null && value.Point != point)
        { _used[value.ParameterIndex] = false; _pointIndices.Remove(value.Point); }
        _values[input] = new(point, value.ParameterIndex, value.DistanceSquared);
    }
    internal void WriteParameterIndices(Span<uint> indices)
    {
        int input = 0;
        for (int pair = 0; pair < _detection.Count; pair++)
            for (int point = 0; point < _detection.ReadContacts(pair).Length; point++)
                indices[pair * 4 + point] = _pointIndices[_values[input++].Point];
    }

    internal JobHandle Schedule(JobHandle dependency)
    {
        var accesses = new List<JobResourceAccess>(_parameters.GetReadAccesses(_scheduler));
        accesses.AddRange(_detection.GetReadbackAccesses(_scheduler));
        _selection.AddAccesses(_scheduler, accesses);
        accesses.AddRange([
            JobResourceAccess.Read(_scheduler.GetResourceToken(_detection.Contacts), 0, (long)_detection.Contacts.Info.Size),
            JobResourceAccess.Read(_scheduler.GetResourceToken(_detection.ContactCounts), 0, (long)_detection.ContactCounts.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_heads), 0, (long)_heads.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_next), 0, (long)_next.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_matches), 0, (long)_matches.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_settingsBuffer), 0, 256),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(_scheduler.GetResourceToken(_commands))]);
        JobHandle matched = DevicePhysicsJobs.Schedule(_scheduler, this, accesses.ToArray(), dependency);
        var reads = new List<JobResourceAccess>(_detection.GetReadbackAccesses(_scheduler))
        { JobResourceAccess.Read(_scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size), WriteAccess };
        return _scheduler.Schedule(new Accept(this), reads.ToArray(), matched);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _selection.Capture();
        var settings = new MatchSettings((uint)_parameters.Capacity, (uint)_detection.Count, (uint)_bucketCount,
            _settings.MatchDistance * _settings.MatchDistance, _settings.MinimumNormalDot);
        using (MappedBuffer mapping = _backend.Map(_settingsBuffer, MapType.Write, BufferRange.Whole))
        { MemoryMarshal.Write(mapping.Bytes, in settings); mapping.Flush(new(0, (ulong)Marshal.SizeOf<MatchSettings>())); }
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized)
            foreach (Buffer buffer in new[] { _heads, _next, _matches })
                _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        uint[] counts = [(uint)_bucketCount, (uint)_parameters.Capacity, (uint)_detection.Count];
        for (int kernel = 0; kernel < 3; kernel++)
        {
            _backend.SetPipeline(_commands, _pipelines[kernel]);
            _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
            if (counts[kernel] != 0) _backend.Dispatch(_commands, new((counts[kernel] + (uint)_threadsPerGroup - 1) / (uint)_threadsPerGroup, 1, 1));
            _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        }
        _backend.Barrier(_commands, new BufferBarrier(_matches, PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_matches, 0, _readback, 0, _matches.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(_matches, PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands); _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct Accept(DeviceContactMatches matches) : IJob
    {
        public void Execute()
        {
            Array.Clear(matches._used); matches._pointIndices.Clear(); matches._nextIndex = 0;
            using MappedBuffer mapping = matches._backend.Map(matches._readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<MatchedContact> values = MemoryMarshal.Cast<byte, MatchedContact>(mapping.Bytes);
            int output = 0;
            for (int pair = 0; pair < matches._detection.Count; pair++)
                for (int point = 0; point < matches._detection.ReadContacts(pair).Length; point++)
                {
                    MatchedContact value = values[pair * 4 + point];
                    matches._values[output++] = value;
                    if (value.Point != Entity.Null)
                    { matches._used[value.ParameterIndex] = true; matches._pointIndices[value.Point] = value.ParameterIndex; }
                }
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
    private readonly record struct MatchSettings(uint Points, uint Pairs, uint Buckets, float SquaredDistance, float NormalDot);
    public void Dispose()
    {
        _selection.Dispose(); _scheduler.ReleaseResourceToken(_resource); _commands.Dispose();
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _headsView.Dispose(); _nextView.Dispose(); _matchesView.Dispose(); _contactsView.Dispose(); _countsView.Dispose(); _settingsView.Dispose();
        _heads.Dispose(); _next.Dispose(); _matches.Dispose(); _readback.Dispose(); _settingsBuffer.Dispose();
    }
}
public readonly struct MatchDeviceContacts(DeviceContactMatches matches) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => matches.Schedule(dependency);
}
public readonly struct DeviceContactMatchResults(DeviceContactMatches matches) : IContactPointMatches
{
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
