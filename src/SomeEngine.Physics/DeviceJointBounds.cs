using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct DeviceJointBoundSet
{
    public uint JointRow, BoundOffset, Count;
}
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct DeviceJointBoundConstraint
{
    public uint OtherJointRow, FirstEnd, OtherEnd, OtherEntityIndex, OtherEntityGeneration;
    public float Minimum, Maximum;
}
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct DeviceJointBoundState
{
    [FieldOffset(0)] public Vector2 Multiplier;
    [FieldOffset(8)] public float Penalty;
    [FieldOffset(16)] public Vector2 InitialError;
}
public readonly record struct DeviceJointBoundsBinding(DeviceJointBounds Bounds) : IComponent;
public readonly struct DeviceJointBoundsResources : IJobComponentResources<DeviceJointBoundsBinding>
{
    public static void Collect(in DeviceJointBoundsBinding component, ref JobResourceAccessBuilder resources) { }
}
public readonly struct DeviceJointBoundsRows(DeviceJointBounds bounds) : IDeviceConstraintRowSelection
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceJointBoundsBinding>()
        .WithOptionalRead<DeviceConstraintBinding<DeviceJointConstraint>>();
    public void AddReadResources(QueryReadResources resources)
    {
        resources.Add<DeviceJointBoundsBinding, DeviceJointBoundsResources>();
        resources.Add<DeviceConstraintBinding<DeviceJointConstraint>, DeviceConstraintReferenceResources<DeviceJointConstraint>>();
    }
    public bool Matches(scoped QueryRow row) => row.TryRead<DeviceJointBoundsBinding>(out var binding) && ReferenceEquals(binding.Bounds, bounds)
        && row.TryRead<DeviceConstraintBinding<DeviceJointConstraint>>(out var frame) && ReferenceEquals(frame.Data, bounds.Frames);
    public bool HasChangedInputs(scoped QueryChunkView chunk) => !chunk.Has<DeviceJointBoundsBinding>()
        || chunk.HasChangedSinceLastSystemVersion<DeviceJointBoundsBinding>()
        || !chunk.Has<DeviceConstraintBinding<DeviceJointConstraint>>()
        || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<DeviceJointConstraint>>();
}

/// <summary>Device bounds corresponding to the original joint's LongRangeJointBound elements.</summary>
public sealed class DeviceJointBounds : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly QueryHandle _bodyRows, _jointRows;
    private readonly DeviceConstraintData<DeviceJointBoundSet> _sets;
    private readonly JointLongRangeSettings _settings;
    private readonly JointLongRangeGraph _graph = new();
    private readonly Buffer _valueUpload, _stateUpload, _parameters;
    private readonly BufferUav _view;
    private readonly BufferCbv _parametersView;
    private readonly CommandContext _commands;
    private readonly JobResourceAccess[] _topologyAccesses;
    private MappedBuffer _valueMapping, _stateMapping;
    private bool _topologyChanged, _initialized, _statesInitialized;

    public DeviceJointBounds(IGraphicsBackend backend, Device device, World world, QueryHandle bodies, QueryHandle joints,
        DeviceConstraintData<DeviceJointConstraint> frames, DeviceConstraintData<DeviceJointBoundSet> sets,
        int boundsPerJoint, JointLongRangeSettings settings)
    {
        _backend = backend; _device = device; _world = world; _sets = sets; _settings = settings; BoundsPerJoint = boundsPerJoint;
        Frames = frames; Capacity = sets.Capacity * boundsPerJoint;
        _bodyRows = world.Query(world.GetQueryDefinition(bodies).AsFilter().WithAccess(new QueryDefinitionBuilder().Read<BodyMass>().Build()));
        _jointRows = world.Query(world.GetQueryDefinition(joints).AsFilter().WithAccess(new QueryDefinitionBuilder()
            .Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>().Buffer<LongRangeJointBound>(QueryAccess.ReadWrite).Build()));
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(scheduler.GetResourceToken(this)) };
        PhysicsQueryAccess.AddAccesses(world, _bodyRows, accesses); PhysicsQueryAccess.AddAccesses(world, _jointRows, accesses);
        _topologyAccesses = accesses.ToArray();
        Values = backend.CreateBuffer(device, new((ulong)Capacity * 32,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Original joint distance bounds"), MemoryType.DeviceLocal);
        _view = backend.CreateBufferUav(device, new(Values, new(0, Values.Info.Size), StructureStride: 32));
        States = new(backend, device, Capacity, "DeviceJointBoundStates");
        _valueUpload = backend.CreateBuffer(device, new(Values.Info.Size, BufferUsages.CopySource, "Joint distance bound inputs"), MemoryType.Upload);
        _stateUpload = backend.CreateBuffer(device, new(States.States.Info.Size, BufferUsages.CopySource, "Joint bound history inputs"), MemoryType.Upload);
        _valueMapping = backend.Map(_valueUpload, MapType.Write, BufferRange.Whole);
        _stateMapping = backend.Map(_stateUpload, MapType.Write, BufferRange.Whole);
        _stateMapping.Bytes.Clear();
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Joint bound numerical settings"), MemoryType.Upload);
        _parametersView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        using (MappedBuffer mapping = backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            var values = new NumericalSettings(settings.PenaltyScale, settings.MultiplierRetention, settings.InitialErrorFraction, (uint)boundsPerJoint);
            MemoryMarshal.Write(mapping.Bytes, in values); mapping.Flush(new(0, 16));
        }
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Joint bound transfer"));
    }
    public int Capacity { get; }
    public int BoundsPerJoint { get; }
    public Buffer Values { get; }
    public DeviceConstraintState<DeviceJointBoundState> States { get; }
    internal DeviceConstraintData<DeviceJointConstraint> Frames { get; }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public BuildDeviceJointBounds BuildInputs() => new(this);
    public UploadDeviceJointBounds UploadInputs() => new(this);
    public WriteDeviceConstraintInputs<JointConstraint, DeviceJointBoundSet, DeviceJointBoundSource> WriteInputs(int rowsPerPacket)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        JobResourceAccess[] outputs = [JobResourceAccess.Read(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_valueUpload), 0, (long)_valueUpload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_stateUpload), 0, (long)_stateUpload.Info.Size)];
        return _sets.WriteInputs<JointConstraint, DeviceJointBoundSource>(_world, _jointRows, new(this), rowsPerPacket, outputs);
    }
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("DeviceJointBoundValues", [ResourceBinding.WritableBuffer(_view)]);
        bindings.Add("JointLongRangeSettings", [ResourceBinding.ConstantBuffer(_parametersView)]);
        States.AddBindings(bindings);
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(Values), 0, (long)Values.Info.Size), States.GetReadAccess(scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(_parameters), 0, 256)];
    public JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(Values), 0, (long)Values.Info.Size), States.GetWriteAccess(scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(_parameters), 0, 256)];
    internal int Write(int setIndex, ReadOnlySpan<LongRangeJointBound> bounds, scoped QueryCursor access)
    {
        int offset = setIndex * BoundsPerJoint;
        Span<DeviceJointBoundConstraint> values = MemoryMarshal.Cast<byte, DeviceJointBoundConstraint>(_valueMapping.Bytes);
        Span<DeviceJointBoundState> states = MemoryMarshal.Cast<byte, DeviceJointBoundState>(_stateMapping.Bytes);
        for (int index = 0; index < bounds.Length; index++)
        {
            ref readonly LongRangeJointBound bound = ref bounds[index];
            access.TryGetRow(bound.OtherJoint, out QueryRow other);
            values[offset + index] = new()
            {
                OtherJointRow = (uint)other.Read<DeviceConstraintBinding<DeviceJointConstraint>>().Index,
                FirstEnd = (uint)bound.FirstEnd, OtherEnd = (uint)bound.OtherEnd,
                OtherEntityIndex = (uint)bound.OtherJoint.Index, OtherEntityGeneration = (uint)bound.OtherJoint.Generation,
                Minimum = bound.Minimum, Maximum = bound.Maximum,
            };
            if (_topologyChanged) states[offset + index] = new()
            { Multiplier = bound.Multiplier, Penalty = bound.Penalty, InitialError = bound.InitialError };
        }
        return offset;
    }
    internal JobHandle ScheduleBuild(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new BuildTopology(this, context), _topologyAccesses, dependency);
    internal JobHandle ScheduleUpload(Scheduler scheduler, JobHandle dependency) => DevicePhysicsJobs.Schedule(scheduler, this,
        [JobResourceAccess.Write(scheduler.GetResourceToken(this)), JobResourceAccess.Read(scheduler.GetResourceToken(_valueUpload), 0, (long)_valueUpload.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_stateUpload), 0, (long)_stateUpload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(Values), 0, (long)Values.Info.Size), States.GetWriteAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))], dependency);
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _valueMapping.Flush(_valueMapping.Range);
        _backend.Begin(_commands, new(0, 0, 8));
        Copy(_valueUpload, Values, _initialized);
        if (_topologyChanged || !_statesInitialized)
        {
            _stateMapping.Flush(_stateMapping.Range);
            Copy(_stateUpload, States.States, _statesInitialized);
            _statesInitialized = true;
        }
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Copy(Buffer upload, Buffer destination, bool initialized)
    {
        _backend.Barrier(_commands, new BufferBarrier(destination, initialized ? PipelineSync.ComputeShading : destination.InitialSync,
            PipelineSync.Copy, initialized ? ResourceAccess.UnorderedAccess : destination.InitialAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(upload, 0, destination, 0, destination.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(destination, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }
    private readonly struct BuildTopology(DeviceJointBounds bounds, PhysicsStepContext context) : IJob, IQueryExecution
    {
        public void Execute() { var work = this; bounds._world.ExecuteQuery(bounds._bodyRows, ref work); }
        public void Execute(scoped QueryCursor bodies)
        {
            var work = new ReadJoints(bounds, new PhysicsBodies(bodies), context);
            bounds._world.ExecuteQuery(bounds._jointRows, ref work);
        }
    }
    private readonly ref struct ReadJoints(DeviceJointBounds bounds, PhysicsBodies bodies, PhysicsStepContext context) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        public void Execute(scoped QueryCursor joints) => bounds._topologyChanged = bounds._graph.Refresh(new(joints), _bodies, in context, bounds._settings.Levels);
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NumericalSettings(float PenaltyScale, float Retention, float InitialErrorFraction, uint BoundsPerJoint);
    public void Dispose()
    {
        _world.ReleaseQuery(_bodyRows); _world.ReleaseQuery(_jointRows);
        _valueMapping.Dispose(); _stateMapping.Dispose(); _commands.Dispose(); _valueUpload.Dispose(); _stateUpload.Dispose();
        _view.Dispose(); _parametersView.Dispose(); _parameters.Dispose(); Values.Dispose(); States.Dispose();
    }
}
public readonly struct DeviceJointBoundSource(DeviceJointBounds bounds) : IDeviceConstraintSource<JointConstraint, DeviceJointBoundSet>
{
    public static void Collect(in JointConstraint component, ref JobResourceAccessBuilder resources) { }
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceConstraintBinding<DeviceJointConstraint>>()
        .WithOptionalRead<DeviceConstraintBinding<DeviceJointBoundSet>>().WithAccess(new QueryDefinitionBuilder().ReadBuffer<LongRangeJointBound>().Build());
    public void AddReadResources(QueryReadResources resources)
    {
        resources.Add<DeviceConstraintBinding<DeviceJointConstraint>, DeviceConstraintReferenceResources<DeviceJointConstraint>>();
        resources.Add<DeviceConstraintBinding<DeviceJointBoundSet>, DeviceConstraintReferenceResources<DeviceJointBoundSet>>();
    }
    public DeviceJointBoundSet Read(in JointConstraint component, scoped QueryRow row, scoped QueryCursor access)
    {
        ReadOnlySpan<LongRangeJointBound> values = row.ReadBuffer<LongRangeJointBound>().AsSpan();
        int offset = bounds.Write(row.Read<DeviceConstraintBinding<DeviceJointBoundSet>>().Index, values, access);
        return new() { JointRow = (uint)row.Read<DeviceConstraintBinding<DeviceJointConstraint>>().Index,
            BoundOffset = (uint)offset, Count = (uint)values.Length };
    }
}
public readonly struct BuildDeviceJointBounds(DeviceJointBounds bounds) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => bounds.ScheduleBuild(in context, dependency);
}
public readonly struct UploadDeviceJointBounds(DeviceJointBounds bounds) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => bounds.ScheduleUpload(WorldJobs.GetScheduler(context.World), dependency);
}
