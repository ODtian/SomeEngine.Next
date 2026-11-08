using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Completed geometric and force observations from the selected resident contact rows.</summary>
public sealed class DeviceContactSamples : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly Buffer _samples;
    private Buffer _readback;
    private readonly IDeviceConstraintSelection? _contactRows;
    private int _sampleCount;
    private readonly BufferUav _view;
    private readonly CommandContext _commands;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[] _bindings;
    private readonly JobResourceAccess[] _inputAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly int _threadsPerGroup;
    private bool _initialized;
    internal readonly DeviceConstraintData<DeviceContactConstraint> Parameters;
    internal Sample[] Values = [];

    public DeviceContactSamples(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceConstraintData<DeviceContactConstraint> parameters, ComputePipelineDesc kernel,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses,
        int threadsPerGroup, ReadOnlyMemory<IDeviceConstraintSelection> selections)
    {
        _backend = backend; _device = device; _bodies = bodies; Parameters = parameters;
        _threadsPerGroup = threadsPerGroup; _inputAccesses = inputAccesses.ToArray(); _selections = selections.ToArray();
        var description = new BufferDesc((ulong)parameters.Capacity * 80,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Current contact observations");
        _samples = parameters.Memory is { } memory ? memory.CreateBuffer(description, 80) : backend.CreateBuffer(device, description);
        _readback = backend.CreateBuffer(device, new(80, BufferUsages.CopyDestination), MemoryType.Readback);
        _view = backend.CreateBufferUav(device, new(_samples, new(0, _samples.Info.Size), StructureStride: 80));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact observations"));
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
            ["DeviceContactEventSamples"] = [ResourceBinding.WritableBuffer(_view)],
        };
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        ResourceBinding rowsBinding = bindings["DeviceContactsSelectionRows"][0];
        bindings.Add("DeviceContactSampleRows", [rowsBinding]);
        _contactRows = _selections.FirstOrDefault(selection => selection.RowsBinding == rowsBinding);
        _bindings = DeviceCollisionGrid.OrderBindings(_layout, bindings);
    }

    public ReadDeviceContactSamples Readback() => new(this);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    SomeEngine.Graphics.Queue IDevicePhysicsSubmission.Queue => _backend.GetQueue(_device, QueueType.Compute);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(_inputAccesses) { _bodies.GetReadAccess(scheduler), JobResourceAccess.Write(scheduler.GetResourceToken(this)) };
        accesses.AddRange(Parameters.GetReadAccesses(scheduler));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_samples), 0, (long)_samples.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context), accesses.ToArray(), dependency);
        return scheduler.Schedule(new Map(this), new[]
        {
            JobResourceAccess.Read(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
        }, copied);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit() => Record(default);
    private QueueCompletion Record(in PhysicsStepContext context)
    {
        foreach (IDeviceConstraintSelection selection in _selections) selection.Capture(in context);
        _sampleCount = _contactRows?.Count ?? Parameters.RowCount;
        if (_readback.Info.Size < (ulong)_sampleCount * 80)
        {
            _readback.Dispose(); _readback = _backend.CreateBuffer(_device,
                new((ulong)_sampleCount * 80, BufferUsages.CopyDestination), MemoryType.Readback);
        }
        var step = new SampleStep((uint)_sampleCount, _contactRows is null ? 0u : 1u);
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized && Parameters.Memory?.Contains(_samples) != true)
            _backend.Barrier(_commands, new BufferBarrier(_samples, _samples.InitialSync, PipelineSync.ComputeShading,
                _samples.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
        if (_sampleCount != 0) _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_sampleCount + _threadsPerGroup - 1) / _threadsPerGroup));
        _backend.Barrier(_commands, new BufferBarrier(_samples, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        if (_sampleCount != 0) _backend.CopyBuffer(_commands, new(_samples, 0, _readback, 0, (ulong)_sampleCount * 80));
        _backend.Barrier(_commands, new BufferBarrier(_samples, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct Submission(DeviceContactSamples samples, PhysicsStepContext context) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => samples._backend;
        public Device Device => samples._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => samples.Record(in context);
    }
    private readonly struct Map(DeviceContactSamples samples) : IJob
    {
        public void Execute()
        {
            if (samples.Values.Length < samples.Parameters.RowCount) Array.Resize(ref samples.Values, samples.Parameters.RowCount);
            Array.Clear(samples.Values);
            if (samples._sampleCount == 0) return;
            using MappedBuffer mapping = samples._backend.Map(samples._readback, MapType.Read, new(0, (ulong)samples._sampleCount * 80));
            mapping.Invalidate(mapping.Range);
            foreach (ref readonly Sample value in MemoryMarshal.Cast<byte, Sample>(mapping.Bytes))
                if (value.Active != 0) samples.Values[value.Row] = value;
        }
    }
    private readonly record struct SampleStep(uint Count, uint Selected);
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct Sample
    {
        [FieldOffset(0)] public uint EntityIndex;
        [FieldOffset(4)] public uint EntityGeneration;
        [FieldOffset(8)] public uint Active;
        [FieldOffset(12)] public uint Row;
        [FieldOffset(16)] public Vector3 WorldPoint;
        [FieldOffset(28)] public float Separation;
        [FieldOffset(32)] public Vector3 Normal;
        [FieldOffset(48)] public Vector3 Force;
        [FieldOffset(64)] public SomeEngine.ECS.Entities.Entity First;
        [FieldOffset(72)] public SomeEngine.ECS.Entities.Entity Second;
    }
    public void Dispose() { _pipeline.Dispose(); _commands.Dispose(); _view.Dispose(); _samples.Dispose(); _readback.Dispose(); }
}

public readonly struct ReadDeviceContactSamples(DeviceContactSamples samples) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        samples.Schedule(in context, dependency);
}

/// <summary>Feeds the original contact event lifecycle from completed device observations.</summary>
public readonly struct DeviceContactEventSource(DeviceContactSamples samples) : IContactEventSource
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceConstraintBinding<DeviceContactConstraint>>();
    public void AddReadResources(QueryReadResources resources)
    {
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        resources.Add<DeviceConstraintBinding<DeviceContactConstraint>, DeviceConstraintReferenceResources<DeviceContactConstraint>>();
    }
    public void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses) => accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(samples)));
    public bool TryRead(scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out ContactEventSample sample)
    {
        sample = default;
        if (!row.TryRead<DeviceConstraintBinding<DeviceContactConstraint>>(out var binding) || !ReferenceEquals(binding.Data, samples.Parameters)) return false;
        ref readonly DeviceContactSamples.Sample value = ref samples.Values[binding.Index];
        if (value.Active == 0 || value.EntityIndex != (uint)row.Entity.Index || value.EntityGeneration != (uint)row.Entity.Generation) return false;
        sample = new(value.First, value.Second, value.WorldPoint, value.Normal, value.Separation, value.Force);
        return true;
    }
}
