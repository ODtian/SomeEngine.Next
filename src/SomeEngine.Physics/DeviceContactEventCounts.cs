using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceContactCountKernels(ComputePipelineDesc Clear, ComputePipelineDesc Count);

/// <summary>Counts completed contact phases on the device and delivers six aggregate results through the event stream.</summary>
public sealed class DeviceContactEventCounts : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceConstraintData<DeviceContactConstraint> _contacts;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly JobResourceAccess[] _inputs;
    private readonly Buffer _counts, _history, _readback, _settings;
    private readonly BufferUav _countsView, _historyView;
    private readonly BufferCbv _settingsView;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly CommandContext _commands;
    private readonly PhysicsEventBuffer _events;
    private bool _initialized;

    public DeviceContactEventCounts(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceConstraintData<DeviceContactConstraint> contacts, DeviceContactCountKernels kernels,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlySpan<JobResourceAccess> accesses,
        ReadOnlyMemory<IDeviceConstraintSelection> selections, PhysicsEventBuffer events)
    {
        _backend = backend; _device = device; _bodies = bodies; _contacts = contacts; _events = events;
        _selections = selections.ToArray(); _inputs = accesses.ToArray();
        _counts = backend.CreateBuffer(device, new(24, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
        var historyDescription = new BufferDesc((ulong)contacts.Capacity * 16, BufferUsages.ShaderRead | BufferUsages.ShaderWrite);
        _history = contacts.Memory is { } memory ? memory.CreateBuffer(historyDescription, 16) : backend.CreateBuffer(device, historyDescription);
        _readback = backend.CreateBuffer(device, new(24, BufferUsages.CopyDestination), MemoryType.Readback);
        _settings = backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload);
        _countsView = backend.CreateBufferUav(device, new(_counts, new(0, 24), StructureStride: 4));
        _historyView = backend.CreateBufferUav(device, new(_history, new(0, _history.Info.Size), StructureStride: 16));
        _settingsView = backend.CreateBufferCbv(device, new(_settings, new(0, 256)));
        var bindings = new Dictionary<string, ResourceBinding[]>(inputs)
        {
            ["DeviceContactEventCounts"] = [ResourceBinding.WritableBuffer(_countsView)],
            ["DeviceContactEventHistory"] = [ResourceBinding.WritableBuffer(_historyView)],
            ["ContactCountSettings"] = [ResourceBinding.ConstantBuffer(_settingsView)],
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
        };
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        ComputePipelineDesc[] descriptions = [kernels.Clear, kernels.Count];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact phase counts"));
    }
    public CountDeviceContactEvents Count() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(_inputs) { _bodies.GetReadAccess(scheduler) };
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        foreach (Buffer buffer in new[] { _counts, _history, _readback, _settings })
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        JobHandle completed = DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context), accesses.ToArray(), dependency);
        return scheduler.Schedule(new ReadCounts(this, context), [_events.WriteAccess,
            JobResourceAccess.Read(scheduler.GetResourceToken(_readback), 0, 24)], completed);
    }
    private QueueCompletion Record(in PhysicsStepContext context)
    {
        foreach (IDeviceConstraintSelection selection in _selections) selection.Capture(in context);
        using (MappedBuffer mapping = _backend.Map(_settings, MapType.Write, BufferRange.Whole))
        {
            Span<uint> settings = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            settings[0] = (uint)_contacts.RowCount; settings[1] = _initialized ? 0u : 1u;
            settings[2] = BitConverter.SingleToUInt32Bits(.001f); mapping.Flush(new(0, 12));
        }
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized)
            foreach (Buffer buffer in new[] { _counts, _history })
                if (_contacts.Memory?.Contains(buffer) != true) _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        for (int kernel = 0; kernel < 2; kernel++)
        {
            _backend.SetPipeline(_commands, _pipelines[kernel]);
            _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
            _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups(kernel == 0 && _initialized ? 1 : (_contacts.RowCount + 63) / 64));
            _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        }
        _backend.Barrier(_commands, new BufferBarrier(_counts, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_counts, 0, _readback, 0, 24));
        _backend.Barrier(_commands, new BufferBarrier(_counts, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands); _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct Submission(DeviceContactEventCounts counts, PhysicsStepContext context) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => counts._backend;
        public Device Device => counts._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => counts.Record(in context);
    }
    private readonly struct ReadCounts(DeviceContactEventCounts counts, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            using MappedBuffer mapping = counts._backend.Map(counts._readback, MapType.Read, new(0, 24));
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            for (int index = 0; index < 6; index++)
                if (values[index] != 0) counts._events.Add(new PhysicsEvent
                {
                    Kind = index < 3 ? PhysicsEventKind.Contact : PhysicsEventKind.Trigger,
                    Phase = (PhysicsEventPhase)(index % 3), Occurrences = (int)values[index],
                    Substep = context.Substep, Time = context.EndTime,
                });
        }
    }
    public void Dispose()
    {
        _commands.Dispose(); foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _countsView.Dispose(); _historyView.Dispose(); _settingsView.Dispose();
        _counts.Dispose(); _history.Dispose(); _readback.Dispose(); _settings.Dispose();
    }
}
public readonly struct CountDeviceContactEvents(DeviceContactEventCounts counts) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => counts.Schedule(in context, dependency);
}
