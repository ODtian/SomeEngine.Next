using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceContactCountKernels(ComputePipelineDesc Clear, ComputePipelineDesc Index,
    ComputePipelineDesc Count, ComputePipelineDesc Removed);

/// <summary>Compares touching shape pairs on the device and reads six event totals for registered consumers.</summary>
public sealed class DeviceContactEventCounts : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceContactManifolds _contacts;
    private readonly DeviceContactColumns _first;
    private readonly PhysicsEventBuffer _events;
    private readonly List<IDisposable> _objects = [];
    private readonly List<Buffer> _work = [];
    private readonly Buffer _counts, _readback, _settings;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][][][] _bindings;
    private readonly DeviceContactColumn[] _eventPairs, _eventCounts;
    private int _eventBank;
    private readonly CommandContext _commands;
    private readonly int _buckets;
    private bool _initialized, _history;
    public DeviceContactEventCounts(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceContactManifolds contacts, DeviceContactCountKernels kernels, PhysicsEventBuffer events, float touchDistance = .001f)
    {
        _backend = backend; _device = device; _bodies = bodies; _contacts = contacts; _events = events; _first = contacts.Current;
        _buckets = contacts.Capacity.Pairs * 2 + 1;
        var bindings = new Dictionary<string, ResourceBinding[]>();
        _counts = Add("DeviceContactEventCounts", 6);
        Add("DeviceContactEventHeads", _buckets); Add("DeviceContactEventNext", contacts.Capacity.Pairs);
        Add("DeviceContactEventClaims", contacts.Capacity.Pairs);
        _readback = Keep(backend.CreateBuffer(device, new(24, BufferUsages.CopyDestination), MemoryType.Readback));
        _settings = Keep(backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload));
        BufferCbv settings = Keep(backend.CreateBufferCbv(device, new(_settings, BufferRange.Whole)));
        bindings.Add("ContactCountSettings", [ResourceBinding.ConstantBuffer(settings)]);
        bindings.Add("DeviceBodyStates", [ResourceBinding.WritableBuffer(bodies.View)]);
        _touchDistance = touchDistance;
        ComputePipelineDesc[] descriptions = [kernels.Clear, kernels.Index, kernels.Count, kernels.Removed];
        _pipelines = descriptions.Select(value => Keep(backend.CreateComputePipeline(device, value))).ToArray();
        _layouts = descriptions.Select(value => value.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        _eventPairs = [Keep(new DeviceContactColumn(backend,device,"EventPairsA",contacts.Capacity.Pairs,28)),
            Keep(new DeviceContactColumn(backend,device,"EventPairsB",contacts.Capacity.Pairs,28))];
        _eventCounts = [Keep(new DeviceContactColumn(backend,device,"EventCountA",1,4)),
            Keep(new DeviceContactColumn(backend,device,"EventCountB",1,4))];
        _bindings = [[Bind(contacts.Current,0),Bind(contacts.Current,1)],
            [Bind(contacts.Previous,0),Bind(contacts.Previous,1)]];
        _commands = Keep(backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact pair events")));
        Buffer Add(string name, int count)
        {
            Buffer value = Keep(backend.CreateBuffer(device, new((ulong)count * 4, BufferUsages.ShaderWrite | BufferUsages.ShaderRead | BufferUsages.CopySource, name)));
            BufferUav view = Keep(backend.CreateBufferUav(device, new(value, BufferRange.Whole, StructureStride: 4)));
            _work.Add(value); bindings.Add(name, [ResourceBinding.WritableBuffer(view)]); return value;
        }
        ResourceBinding[][] Bind(DeviceContactColumns current, int bank)
        {
            var values = new Dictionary<string, ResourceBinding[]>(bindings);
            current.AddBindings(values);
            values["CurrentContactEventPairs"] = [ResourceBinding.WritableBuffer(_eventPairs[bank].WriteView)];
            values["CurrentContactEventCount"] = [ResourceBinding.WritableBuffer(_eventCounts[bank].WriteView)];
            values["PreviousContactEventPairs"] = [ResourceBinding.ReadOnlyBuffer(_eventPairs[1 - bank].ReadView)];
            values["PreviousContactEventCount"] = [ResourceBinding.ReadOnlyBuffer(_eventCounts[1 - bank].ReadView)];
            return _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, values)).ToArray();
        }
    }
    private readonly float _touchDistance;
    public CountDeviceContactEvents Count() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!_events.HasReaders) { _history = false; return dependency; }
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        DeviceContactColumns current = _contacts.Current;
        int bank = _eventBank; _eventBank = 1 - bank;
        JobResourceAccess[] accesses = [..current.GetReadAccesses(scheduler),
            _bodies.GetReadAccess(scheduler),JobResourceAccess.Write(scheduler.GetResourceToken(this))];
        bool reset = !_history; _history = true;
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new Transfer(this, current, bank, reset), accesses, dependency);
        return scheduler.Schedule(new Read(this, context), [JobResourceAccess.Write(scheduler.GetResourceToken(this)), _events.WriteAccess], copied);
    }
    private QueueCompletion Record(DeviceContactColumns current, int bank, bool reset)
    {
        using (MappedBuffer mapping = _backend.Map(_settings, MapType.Write, BufferRange.Whole))
        {
            var parameters = new Settings(reset ? 1u : 0u, (uint)_buckets, _touchDistance);
            MemoryMarshal.Write(mapping.Bytes, in parameters); mapping.Flush(new(0, 12));
        }
        _backend.Begin(_commands, new(0, 0, 128)); current.BeginWrite(_backend, _commands);
        _eventPairs[bank].Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        _eventCounts[bank].Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        _eventPairs[1 - bank].Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
        _eventCounts[1 - bank].Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
        if (!_initialized)
            foreach (Buffer value in _work) _backend.Barrier(_commands, new BufferBarrier(value, value.InitialSync,
                PipelineSync.ComputeShading, value.InitialAccess, ResourceAccess.UnorderedAccess));
        ResourceBinding[][] bindings = _bindings[ReferenceEquals(current, _first) ? 0 : 1][bank];
        int groups = (Math.Max(_buckets, _contacts.Capacity.Pairs) + 63) / 64;
        for (int kernel = 0; kernel < _pipelines.Length; kernel++)
        {
            _backend.SetPipeline(_commands, _pipelines[kernel]);
            _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], bindings[kernel], []));
            _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups(kernel == 0 ? groups : (_contacts.Capacity.Pairs + 63) / 64));
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
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Settings(uint Reset, uint Buckets, float Distance);
    private readonly struct Transfer(DeviceContactEventCounts counts, DeviceContactColumns current, int bank, bool reset) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => counts._backend;
        public Device Device => counts._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => counts.Record(current, bank, reset);
    }
    private readonly struct Read(DeviceContactEventCounts counts, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            using MappedBuffer mapping = counts._backend.Map(counts._readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range); ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            for (int index = 0; index < values.Length; index++)
                if (values[index] != 0) counts._events.Add(new PhysicsEvent
                {
                    Kind = index < 3 ? PhysicsEventKind.Contact : PhysicsEventKind.Trigger,
                    Phase = (PhysicsEventPhase)(index % 3),
                    Occurrences = (int)values[index],
                    Substep = context.Substep,
                    Time = context.EndTime
                });
        }
    }
    private T Keep<T>(T value) where T : IDisposable { _objects.Add(value); return value; }
    public void Dispose() { for (int index = _objects.Count - 1; index >= 0; index--) _objects[index].Dispose(); }
}
public readonly struct CountDeviceContactEvents(DeviceContactEventCounts counts) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => counts.Schedule(in context, dependency);
}
