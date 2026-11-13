using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DeviceContactSample
{
    public ContactPair Pair;
    public int FirstFeature, SecondFeature;
    public Vector3 WorldPoint;
    public float Separation;
    public Vector3 Normal;
    public ContactPointFlags Flags;
    public Vector3 Force;
    public uint Active;
}
/// <summary>Explicitly reads completed geometric and force observations for an actual CPU consumer.</summary>
public sealed class DeviceContactSamples : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceContactManifolds _contacts;
    private readonly DeviceContactColumns _first;
    private readonly Buffer _samples, _countReadback;
    private Buffer _readback;
    private readonly BufferUav _sampleView;
    private readonly CommandContext _commands;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly ResourceBinding[][] _bindings;
    private DeviceContactSample[] _values = [];
    private int _count;
    private bool _initialized;
    private const uint Stride = 88;
    public DeviceContactSamples(IGraphicsBackend backend, Device device, DeviceBodies bodies, DeviceContactManifolds contacts, ComputePipelineDesc kernel)
    {
        _backend = backend; _device = device; _bodies = bodies; _contacts = contacts; _first = contacts.Current;
        _samples = backend.CreateBuffer(device, new((ulong)contacts.Capacity.Points * Stride,
            BufferUsages.ShaderWrite | BufferUsages.CopySource, "Contact observations"));
        _sampleView = backend.CreateBufferUav(device, new(_samples, BufferRange.Whole, StructureStride: Stride));
        _countReadback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        _readback = backend.CreateBuffer(device, new(Stride, BufferUsages.CopyDestination), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact observation readback"));
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _bindings = [Bind(contacts.Current), Bind(contacts.Previous)];
        ResourceBinding[] Bind(DeviceContactColumns columns)
        {
            var bindings = new Dictionary<string, ResourceBinding[]>
            {
                ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
                ["DeviceContactEventSamples"] = [ResourceBinding.WritableBuffer(_sampleView)]
            };
            columns.AddBindings(bindings);
            return DeviceCollisionGrid.OrderBindings(_layout, bindings);
        }
    }
    public ReadOnlySpan<DeviceContactSample> Values => _values.AsSpan(0, _count);
    public JobResourceAccess ReadAccess(Scheduler scheduler) => JobResourceAccess.Read(scheduler.GetResourceToken(this));
    public ReadDeviceContactSamples Readback() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        DeviceContactColumns columns = _contacts.Current;
        JobResourceAccess[] accesses = [..columns.GetReadAccesses(scheduler),_bodies.GetReadAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_contacts.DispatchValues.Buffer))];
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new Transfer(this, columns), accesses, dependency);
        return scheduler.Schedule(new Map(this), JobResourceAccess.Write(scheduler.GetResourceToken(this)), copied);
    }
    private async ValueTask<QueueCompletion> SubmitAsync(DeviceContactColumns columns)
    {
        _backend.Begin(_commands, new(0, 0, 64));
        columns.BeginWrite(_backend, _commands);
        if (!_initialized) _backend.Barrier(_commands, new BufferBarrier(_samples, _samples.InitialSync, PipelineSync.ComputeShading,
            _samples.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipeline);
        _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings[ReferenceEquals(columns, _first) ? 0 : 1], []));
        _contacts.DispatchPoints(_backend, _commands);
        columns.CountValues.Transition(_backend, _commands, PipelineSync.Copy, ResourceAccess.CopySource);
        _backend.CopyBuffer(_commands, new(columns.Counts, 4, _countReadback, 0, 4));
        columns.CountValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        QueueCompletion completion = Submit();
        await _backend.WaitAsync(completion).ConfigureAwait(false);
        using (MappedBuffer mapping = _backend.Map(_countReadback, MapType.Read, BufferRange.Whole))
        { mapping.Invalidate(mapping.Range); _count = (int)MemoryMarshal.Read<uint>(mapping.Bytes); }
        _initialized = true;
        if (_count == 0) return completion;
        ulong bytes = (ulong)_count * Stride;
        if (_readback.Info.Size < bytes)
        { _readback.Dispose(); _readback = _backend.CreateBuffer(_device, new(bytes, BufferUsages.CopyDestination), MemoryType.Readback); }
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(_samples, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_samples, 0, _readback, 0, bytes));
        _backend.Barrier(_commands, new BufferBarrier(_samples, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        return Submit();
    }
    private QueueCompletion Submit()
    {
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void MapValues()
    {
        ContactColumns.Grow(ref _values, _count);
        if (_count == 0) return;
        using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, new(0, (ulong)_count * Stride));
        mapping.Invalidate(mapping.Range);
        MemoryMarshal.Cast<byte, DeviceContactSample>(mapping.Bytes).CopyTo(_values);
    }
    private readonly struct Transfer(DeviceContactSamples samples, DeviceContactColumns columns) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => samples._backend;
        public Device Device => samples._device;
        public QueueCompletion Submit() => throw new NotSupportedException();
        public ValueTask<QueueCompletion> SubmitAsync() => samples.SubmitAsync(columns);
    }
    private readonly struct Map(DeviceContactSamples samples) : IJob { public void Execute() => samples.MapValues(); }
    public void Dispose()
    { _commands.Dispose(); _pipeline.Dispose(); _sampleView.Dispose(); _samples.Dispose(); _countReadback.Dispose(); _readback.Dispose(); }
}
public readonly struct ReadDeviceContactSamples(DeviceContactSamples samples) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => samples.Schedule(in context, dependency);
}
