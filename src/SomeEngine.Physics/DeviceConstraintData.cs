using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceConstraintBinding<TData>(DeviceConstraintData<TData> Data, int Index) : IComponent
    where TData : unmanaged;

[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct DeviceConstraintMetadata
{
    public uint EntityIndex, EntityGeneration, Active;
}

/// <summary>Reads original parameters and entity references into a concrete device constraint representation.</summary>
public interface IDeviceConstraintSource<TComponent, TData> : IJobComponentResources<TComponent>
    where TComponent : struct, IComponent where TData : unmanaged
{
    QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceBodyBinding>();
    void AddReadResources(QueryReadResources resources) => resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
    TData Read(in TComponent component, scoped QueryRow row, scoped QueryCursor access);
}

/// <summary>Physical parameters and entity identities, shared by the selected device solvers.</summary>
public sealed class DeviceConstraintData<TData> : IDisposable, IDevicePhysicsSubmission where TData : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly string _bindingName;
    private readonly Buffer _upload, _metadataUpload;
    private readonly BufferUav _view, _metadataView;
    private readonly CommandContext _commands;
    private MappedBuffer _mapping, _metadataMapping;
    private bool _initialized;
    internal static readonly int Stride = Unsafe.SizeOf<TData>();

    public DeviceConstraintData(IGraphicsBackend backend, Device device, int capacity, string bindingName)
    {
        _backend = backend; _device = device; Capacity = capacity; _bindingName = bindingName;
        Data = backend.CreateBuffer(device, new((ulong)capacity * (ulong)Stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, bindingName), MemoryType.DeviceLocal);
        Metadata = backend.CreateBuffer(device, new((ulong)capacity * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, bindingName + " metadata"), MemoryType.DeviceLocal);
        _view = backend.CreateBufferUav(device, new(Data, new(0, Data.Info.Size), StructureStride: (uint)Stride));
        _metadataView = backend.CreateBufferUav(device, new(Metadata, new(0, Metadata.Info.Size), StructureStride: 16));
        _upload = backend.CreateBuffer(device, new(Data.Info.Size, BufferUsages.CopySource, bindingName + " upload"), MemoryType.Upload);
        _metadataUpload = backend.CreateBuffer(device, new(Metadata.Info.Size, BufferUsages.CopySource, bindingName + " metadata upload"), MemoryType.Upload);
        _mapping = backend.Map(_upload, MapType.Write, BufferRange.Whole);
        _metadataMapping = backend.Map(_metadataUpload, MapType.Write, BufferRange.Whole);
        _mapping.Bytes.Clear(); _metadataMapping.Bytes.Clear();
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: bindingName + " transfer"));
    }

    public int Capacity { get; }
    public Buffer Data { get; }
    public Buffer Metadata { get; }
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add(_bindingName, [ResourceBinding.WritableBuffer(_view)]);
        bindings.Add(_bindingName + "Metadata", [ResourceBinding.WritableBuffer(_metadataView)]);
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(Data), 0, (long)Data.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(Metadata), 0, (long)Metadata.Info.Size)];
    public BeginDeviceConstraintInputs<TData> BeginInputs() => new(this);
    public UploadDeviceConstraintInputs<TData> UploadInputs() => new(this);
    public WriteDeviceConstraintInputs<TComponent, TData, TSource> WriteInputs<TComponent, TSource>(
        World world, QueryHandle rows, TSource source, int rowsPerPacket)
        where TComponent : struct, IComponent where TSource : struct, IDeviceConstraintSource<TComponent, TData> =>
        new(world, rows, this, source, rowsPerPacket);

    internal void BeginWrite() => _metadataMapping.Bytes.Clear();
    internal void WriteInput(int index, Entity entity, in TData data)
    {
        MemoryMarshal.Write(_mapping.Bytes.Slice(index * Stride, Stride), in data);
        var metadata = new DeviceConstraintMetadata { EntityIndex = (uint)entity.Index, EntityGeneration = (uint)entity.Generation, Active = 1 };
        MemoryMarshal.Write(_metadataMapping.Bytes.Slice(index * 16, 16), in metadata);
    }
    internal void CollectUploadWrite(int index, ref JobResourceAccessBuilder resources)
    {
        resources.Write(_upload, (long)index * Stride, Stride);
        resources.Write(_metadataUpload, (long)index * 16, 16);
    }
    internal JobHandle ScheduleBegin(Scheduler scheduler, JobHandle dependency) => scheduler.Schedule(new Begin(this),
        JobResourceAccess.Write(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size), dependency);
    internal JobHandle ScheduleUpload(Scheduler scheduler, JobHandle dependency)
    {
        JobResourceAccess[] accesses = [JobResourceAccess.Write(scheduler.GetResourceToken(Data), 0, (long)Data.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(Metadata), 0, (long)Metadata.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_upload), 0, (long)_upload.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, dependency);
    }

    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _mapping.Flush(_mapping.Range); _metadataMapping.Flush(_metadataMapping.Range);
        _backend.Begin(_commands, new(0, 0, 8));
        Copy(_upload, Data); Copy(_metadataUpload, Metadata);
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Copy(Buffer upload, Buffer destination)
    {
        _backend.Barrier(_commands, new BufferBarrier(destination, _initialized ? PipelineSync.ComputeShading : destination.InitialSync,
            PipelineSync.Copy, _initialized ? ResourceAccess.UnorderedAccess : destination.InitialAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(upload, 0, destination, 0, destination.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(destination, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }
    private readonly struct Begin(DeviceConstraintData<TData> data) : IJob { public void Execute() => data.BeginWrite(); }
    public void Dispose()
    {
        _mapping.Dispose(); _metadataMapping.Dispose(); _commands.Dispose(); _upload.Dispose(); _metadataUpload.Dispose();
        _view.Dispose(); _metadataView.Dispose(); Data.Dispose(); Metadata.Dispose();
    }
}

public readonly struct BeginDeviceConstraintInputs<TData>(DeviceConstraintData<TData> data) : IPhysicsStage where TData : unmanaged
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        data.ScheduleBegin(WorldJobs.GetScheduler(context.World), dependency);
}
public readonly struct UploadDeviceConstraintInputs<TData>(DeviceConstraintData<TData> data) : IPhysicsStage where TData : unmanaged
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        data.ScheduleUpload(WorldJobs.GetScheduler(context.World), dependency);
}
public readonly struct DeviceConstraintUploadResources<TData> : IJobComponentResources<DeviceConstraintBinding<TData>> where TData : unmanaged
{
    public static void Collect(in DeviceConstraintBinding<TData> component, ref JobResourceAccessBuilder resources) =>
        component.Data.CollectUploadWrite(component.Index, ref resources);
}
public readonly struct DeviceConstraintReferenceResources<TData> : IJobComponentResources<DeviceConstraintBinding<TData>> where TData : unmanaged
{
    public static void Collect(in DeviceConstraintBinding<TData> component, ref JobResourceAccessBuilder resources) { }
}
