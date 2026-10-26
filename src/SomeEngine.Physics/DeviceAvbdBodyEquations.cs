using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>The Body's selected equation resource and numerical row, independent of its body-state index.</summary>
public readonly record struct DeviceAvbdEquationBinding(DeviceAvbdBodyEquations Equations, int Index) : IComponent;

/// <summary>Component-owned local equations; body-to-equation addresses are derived from actual bindings.</summary>
public sealed class DeviceAvbdBodyEquations : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Buffer _metadata, _metadataUpload;
    private readonly BufferUav _valuesView, _metadataView, _indicesView;
    private readonly CommandContext _commands;
    private MappedBuffer _mapping;
    private bool _initialized;
    internal readonly DeviceBodies Bodies;
    internal readonly Buffer Indices;

    public DeviceAvbdBodyEquations(IGraphicsBackend backend, Device device, DeviceBodies bodies)
    {
        _backend = backend; _device = device; Bodies = bodies;
        Values = backend.CreateBuffer(device, new((ulong)bodies.Capacity * (ulong)Unsafe.SizeOf<Equation>(),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Body ALM equations"), MemoryType.DeviceLocal);
        _metadata = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Body equation entities"), MemoryType.DeviceLocal);
        Indices = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 4,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Body equation indices"), MemoryType.DeviceLocal);
        _metadataUpload = backend.CreateBuffer(device, new(_metadata.Info.Size, BufferUsages.CopySource,
            "Body equation entity inputs"), MemoryType.Upload);
        _mapping = backend.Map(_metadataUpload, MapType.Write, BufferRange.Whole);
        _mapping.Bytes.Clear();
        _valuesView = backend.CreateBufferUav(device, new(Values, new(0, Values.Info.Size), StructureStride: (uint)Unsafe.SizeOf<Equation>()));
        _metadataView = backend.CreateBufferUav(device, new(_metadata, new(0, _metadata.Info.Size), StructureStride: 16));
        _indicesView = backend.CreateBufferUav(device, new(Indices, new(0, Indices.Info.Size), StructureStride: 4));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Body equation binding transfer"));
    }
    public Buffer Values { get; }
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("DeviceAvbdBodyEquations", [ResourceBinding.WritableBuffer(_valuesView)]);
        bindings.Add("DeviceAvbdEquationMetadata", [ResourceBinding.WritableBuffer(_metadataView)]);
        bindings.Add("DeviceAvbdEquationIndices", [ResourceBinding.WritableBuffer(_indicesView)]);
    }
    internal void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(Values), 0, (long)Values.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(Indices), 0, (long)Indices.Info.Size));
        accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(_metadata), 0, (long)_metadata.Info.Size));
    }
    internal JobHandle Upload(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobHandle clear = scheduler.Schedule(new Begin(this),
            JobResourceAccess.Write(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size), dependency);
        JobHandle written = new WriteDeviceAvbdEquationInputs(this).ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), clear);
        JobResourceAccess[] accesses = [JobResourceAccess.Write(scheduler.GetResourceToken(_metadata), 0, (long)_metadata.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, written);
    }
    internal void CollectUpload(int index, ref JobResourceAccessBuilder resources) => resources.Write(_metadataUpload, (long)index * 16, 16);
    internal void Write(int index, int bodyIndex, Entity entity)
    {
        Span<uint> values = MemoryMarshal.Cast<byte, uint>(_mapping.Bytes.Slice(index * 16, 16));
        values[0] = (uint)bodyIndex; values[1] = (uint)entity.Index; values[2] = (uint)entity.Generation; values[3] = 1;
    }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _mapping.Flush(_mapping.Range);
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(_metadata, _initialized ? PipelineSync.ComputeShading : _metadata.InitialSync,
            PipelineSync.Copy, _initialized ? ResourceAccess.UnorderedAccess : _metadata.InitialAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(_metadataUpload, 0, _metadata, 0, _metadata.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(_metadata, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        QueueCompletion completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        _initialized = true;
        return completion;
    }
    private readonly struct Begin(DeviceAvbdBodyEquations equations) : IJob { public void Execute() => equations._mapping.Bytes.Clear(); }
    public readonly struct EquationUploadResources : IJobComponentResources<DeviceAvbdEquationBinding>
    {
        public static void Collect(in DeviceAvbdEquationBinding component, ref JobResourceAccessBuilder resources) =>
            component.Equations.CollectUpload(component.Index, ref resources);
    }
    [InlineArray(21)] private struct Matrix { private float _first; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Equation
    {
        public Matrix Values, Factors;
        public System.Numerics.Vector3 InertiaX, InertiaY, InertiaZ, LinearGradient, AngularGradient;
        public uint Dimensions;
    }
    public void Dispose()
    {
        _mapping.Dispose(); _commands.Dispose(); _metadataUpload.Dispose(); _valuesView.Dispose(); _metadataView.Dispose(); _indicesView.Dispose();
        Values.Dispose(); _metadata.Dispose(); Indices.Dispose();
    }
}

internal readonly partial struct WriteDeviceAvbdEquationInputs(DeviceAvbdBodyEquations destination) : IJobEntity
{
    public void Execute(Entity entity, [JobResources<DeviceBodyReferenceResources>] in DeviceBodyBinding body,
        [JobResources<DeviceAvbdBodyEquations.EquationUploadResources>] in DeviceAvbdEquationBinding binding, in AvbdBodyEquation equation)
    {
        if (ReferenceEquals(binding.Equations, destination) && ReferenceEquals(body.Bodies, destination.Bodies))
            destination.Write(binding.Index, body.Index, entity);
    }
}
