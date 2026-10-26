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
    private readonly Buffer _samples, _readback;
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
    internal readonly Sample[] Values;

    public DeviceContactSamples(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceConstraintData<DeviceContactConstraint> parameters, ComputePipelineDesc kernel,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses,
        int threadsPerGroup, ReadOnlyMemory<IDeviceConstraintSelection> selections)
    {
        _backend = backend; _device = device; _bodies = bodies; Parameters = parameters;
        _threadsPerGroup = threadsPerGroup; _inputAccesses = inputAccesses.ToArray(); _selections = selections.ToArray();
        Values = new Sample[parameters.Capacity];
        _samples = backend.CreateBuffer(device, new((ulong)parameters.Capacity * 80,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Current contact observations"), MemoryType.DeviceLocal);
        _readback = backend.CreateBuffer(device, new(_samples.Info.Size, BufferUsages.CopyDestination,
            "Completed contact observations"), MemoryType.Readback);
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
        _bindings = DeviceCollisionGrid.OrderBindings(_layout, bindings);
    }

    public ReadDeviceContactSamples Readback() => new(this);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    internal JobHandle Schedule(Scheduler scheduler, JobHandle dependency)
    {
        var accesses = new List<JobResourceAccess>(_inputAccesses) { _bodies.GetReadAccess(scheduler) };
        accesses.AddRange(Parameters.GetReadAccesses(scheduler));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_samples), 0, (long)_samples.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, this, accesses.ToArray(), dependency);
        return scheduler.Schedule(new Map(this), new[]
        {
            JobResourceAccess.Read(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(scheduler, Values),
        }, copied);
    }
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        foreach (IDeviceConstraintSelection selection in _selections) selection.Capture();
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized)
            _backend.Barrier(_commands, new BufferBarrier(_samples, _samples.InitialSync, PipelineSync.ComputeShading,
                _samples.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings, []));
        _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((Parameters.Capacity + _threadsPerGroup - 1) / _threadsPerGroup));
        _backend.Barrier(_commands, new BufferBarrier(_samples, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_samples, 0, _readback, 0, _samples.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(_samples, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct Map(DeviceContactSamples samples) : IJob
    {
        public void Execute()
        {
            using MappedBuffer mapping = samples._backend.Map(samples._readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            MemoryMarshal.Cast<byte, Sample>(mapping.Bytes).CopyTo(samples.Values);
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct Sample
    {
        [FieldOffset(0)] public uint EntityIndex;
        [FieldOffset(4)] public uint EntityGeneration;
        [FieldOffset(8)] public uint Active;
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
        samples.Schedule(WorldJobs.GetScheduler(context.World), dependency);
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
    public void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses) => accesses.Add(JobResourceAccess.Read(scheduler, samples.Values));
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
