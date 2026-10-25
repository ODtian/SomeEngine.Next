using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>The simulated entity's state buffer and derived execution index.</summary>
public readonly record struct DeviceBodyBinding(DeviceBodies Bodies, int Index) : IComponent;

/// <summary>The selected device representation of the original body components.</summary>
[StructLayout(LayoutKind.Explicit, Size = 208)]
public struct DeviceBodyState
{
    [FieldOffset(0)] public Vector3 Position;
    [FieldOffset(12)] public uint Motion;
    [FieldOffset(16)] public Quaternion Rotation;
    [FieldOffset(32)] public Vector3 LinearVelocity;
    [FieldOffset(44)] public float InverseMass;
    [FieldOffset(48)] public Vector3 AngularVelocity;
    [FieldOffset(60)] public uint EntityIndex;
    [FieldOffset(64)] public Vector3 CenterOfMass;
    [FieldOffset(76)] public uint EntityGeneration;
    [FieldOffset(80)] public Vector3 InverseInertiaX;
    [FieldOffset(96)] public Vector3 InverseInertiaY;
    [FieldOffset(112)] public Vector3 InverseInertiaZ;
    [FieldOffset(128)] public Vector3 StartCenterOfMass;
    [FieldOffset(140)] public int Substep;
    [FieldOffset(144)] public Quaternion StartRotation;
    [FieldOffset(160)] public Vector3 PredictedCenterOfMass;
    [FieldOffset(176)] public Quaternion PredictedRotation;
    [FieldOffset(192)] public long Number;
    [FieldOffset(200)] public double Time;
}

public sealed class DeviceBodies : IDisposable, IDevicePhysicsSubmission
{
    public const int StateStride = 208;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Buffer _upload;
    private readonly CommandContext _commands;
    private MappedBuffer _mapping;
    private bool _initialized;

    public DeviceBodies(IGraphicsBackend backend, Device device, int capacity)
    {
        _backend = backend; _device = device; Capacity = capacity;
        States = backend.CreateBuffer(device, new((ulong)capacity * StateStride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Device body states"), MemoryType.DeviceLocal);
        View = backend.CreateBufferUav(device, new(States, new(0, States.Info.Size), StructureStride: StateStride));
        _upload = backend.CreateBuffer(device, new(States.Info.Size, BufferUsages.CopySource, "Body state upload"), MemoryType.Upload);
        _mapping = backend.Map(_upload, MapType.Write, BufferRange.Whole);
        _mapping.Bytes.Clear();
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Body state transfer"));
    }

    public int Capacity { get; }
    public Buffer States { get; }
    internal BufferUav View { get; }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public WriteDeviceBodyInputs WriteInputs(int rowsPerPacket) => new(this, rowsPerPacket);
    public UploadDeviceBodyInputs UploadInputs() => new(this);
    public JobResourceAccess GetReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(States), 0, (long)States.Info.Size);
    public JobResourceAccess GetWriteAccess(Scheduler scheduler) =>
        JobResourceAccess.Write(scheduler.GetResourceToken(States), 0, (long)States.Info.Size);

    internal void WriteInput(int index, in DeviceBodyState state) =>
        MemoryMarshal.Write(_mapping.Bytes.Slice(index * StateStride, StateStride), in state);
    internal void CollectUploadWrite(int index, ref JobResourceAccessBuilder resources) =>
        resources.Write(_upload, (long)index * StateStride, StateStride);
    internal JobHandle ScheduleUpload(Scheduler scheduler, JobHandle dependency)
    {
        JobResourceAccess[] accesses = [GetWriteAccess(scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(_upload), 0, (long)_upload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, dependency);
    }

    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _mapping.Flush(_mapping.Range);
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(States,
            _initialized ? PipelineSync.ComputeShading : States.InitialSync, PipelineSync.Copy,
            _initialized ? ResourceAccess.UnorderedAccess : States.InitialAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(_upload, 0, States, 0, States.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(States, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    public void Dispose()
    {
        _mapping.Dispose(); _commands.Dispose(); _upload.Dispose(); View.Dispose(); States.Dispose();
    }
}

public readonly struct WriteDeviceBodyInputs(DeviceBodies bodies, int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new WriteDeviceBodyInputJob(bodies).ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), dependency);
}

public readonly struct UploadDeviceBodyInputs(DeviceBodies bodies) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        bodies.ScheduleUpload(WorldJobs.GetScheduler(context.World), dependency);
}

public readonly struct DeviceBodyUploadResources : IJobComponentResources<DeviceBodyBinding>
{
    public static void Collect(in DeviceBodyBinding component, ref JobResourceAccessBuilder resources) =>
        component.Bodies.CollectUploadWrite(component.Index, ref resources);
}

/// <summary>Reads only the binding's buffer reference and index; device work declares the state range separately.</summary>
public readonly struct DeviceBodyReferenceResources : IJobComponentResources<DeviceBodyBinding>
{
    public static void Collect(in DeviceBodyBinding component, ref JobResourceAccessBuilder resources) { }
}

internal readonly partial struct WriteDeviceBodyInputJob(DeviceBodies destination) : IJobEntity
{
    public void Execute(Entity entity, [JobResources<DeviceBodyUploadResources>] in DeviceBodyBinding binding,
        in BodyPose pose, in BodyMass mass, in BodyVelocity velocity, in BodyStepState step)
    {
        if (!ReferenceEquals(binding.Bodies, destination)) return;
        var state = new DeviceBodyState
        {
            Position = pose.Position, Rotation = pose.Rotation, Motion = (uint)mass.Motion,
            LinearVelocity = velocity.Linear, AngularVelocity = velocity.Angular, InverseMass = mass.InverseMass,
            CenterOfMass = mass.CenterOfMass, EntityIndex = (uint)entity.Index, EntityGeneration = (uint)entity.Generation,
            InverseInertiaX = new(mass.InverseInertia.M11, mass.InverseInertia.M12, mass.InverseInertia.M13),
            InverseInertiaY = new(mass.InverseInertia.M21, mass.InverseInertia.M22, mass.InverseInertia.M23),
            InverseInertiaZ = new(mass.InverseInertia.M31, mass.InverseInertia.M32, mass.InverseInertia.M33),
            StartCenterOfMass = step.StartCenterOfMass, StartRotation = step.StartRotation,
            PredictedCenterOfMass = step.PredictedCenterOfMass, PredictedRotation = step.PredictedRotation,
            Number = step.Number, Substep = step.Substep, Time = step.Time,
        };
        destination.WriteInput(binding.Index, in state);
    }
}
