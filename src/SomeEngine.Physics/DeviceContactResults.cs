using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Transfers completed contact forces and impulses to their original contact entities.</summary>
public sealed class DeviceContactResults : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private Buffer _readback;
    internal int _rowCount;
    private static readonly int ResponseStride = Marshal.SizeOf<DeviceContactResponseState>();
    private readonly CommandContext _commands;
    internal readonly DeviceConstraintData<DeviceContactConstraint> Parameters;
    internal readonly DeviceConstraintState<DeviceContactResponseState> Responses;
    internal DeviceContactResponseState[] Values = [];
    internal DeviceConstraintMetadata[] Metadata = [];

    public DeviceContactResults(IGraphicsBackend backend, Device device, DeviceConstraintData<DeviceContactConstraint> parameters,
        DeviceConstraintState<DeviceContactResponseState> responses)
    {
        _backend = backend; _device = device; Parameters = parameters; Responses = responses;
        _readback = backend.CreateBuffer(device, new((ulong)(ResponseStride + 16), BufferUsages.CopyDestination,
            "Completed contact responses"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact response readback"));
    }
    public ReadDeviceContactResults Readback(int rowsPerPacket) => new(this, rowsPerPacket);
    internal JobHandle Schedule(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [Responses.GetReadAccess(scheduler), .. Parameters.GetReadAccesses(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return scheduler.Schedule(new Copy(this, scheduler, context, rowsPerPacket, accesses), accesses, dependency);
    }
    private QueueCompletion Submit()
    {
        _rowCount = Parameters.RowCount;
        Responses.Memory?.EnsureRows(_rowCount);
        ulong bytes = (ulong)_rowCount * (ulong)(ResponseStride + 16);
        if (_readback.Info.Size < bytes)
        { _readback.Dispose(); _readback = _backend.CreateBuffer(_device, new(bytes, BufferUsages.CopyDestination), MemoryType.Readback); }
        _backend.Begin(_commands, new(0, 0, 8));
        CopyBuffer(Responses.States, 0, (ulong)_rowCount * (ulong)ResponseStride);
        CopyBuffer(Parameters.Metadata, (ulong)_rowCount * (ulong)ResponseStride, (ulong)_rowCount * 16);
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void CopyBuffer(Buffer source, ulong offset, ulong bytes)
    {
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(source, 0, _readback, offset, bytes));
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
    }
    private readonly struct Copy(DeviceContactResults results, Scheduler scheduler, PhysicsStepContext context,
        int rowsPerPacket, JobResourceAccess[] accesses) : IJob
    {
        public void Execute()
        {
            QueueCompletion completion = results.Submit();
            JobHandle ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, results._backend, completion), accesses);
            JobResourceAccess[] read = [JobResourceAccess.Read(scheduler.GetResourceToken(results._readback), 0, (long)results._readback.Info.Size),
                JobResourceAccess.Write(scheduler.GetResourceToken(results))];
            JobHandle mapped = scheduler.Schedule(new Map(results), read, ready);
            JobHandle applied = new ApplyDeviceContactResults(results).ScheduleParallel(context.World,
                new[] { JobResourceAccess.Read(scheduler.GetResourceToken(results)) },
                new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket), mapped);
            scheduler.ScheduleFinally(new Collect(results), default, applied);
        }
    }
    private readonly struct Map(DeviceContactResults results) : IJob
    {
        public void Execute()
        {
            if (results.Values.Length < results._rowCount) Array.Resize(ref results.Values, results._rowCount);
            if (results.Metadata.Length < results._rowCount) Array.Resize(ref results.Metadata, results._rowCount);
            using MappedBuffer mapping = results._backend.Map(results._readback, MapType.Read,
                new(0, (ulong)results._rowCount * (ulong)(ResponseStride + 16)));
            mapping.Invalidate(mapping.Range);
            int responseBytes = results._rowCount * ResponseStride;
            MemoryMarshal.Cast<byte, DeviceContactResponseState>(mapping.Bytes[..responseBytes]).CopyTo(results.Values);
            MemoryMarshal.Cast<byte, DeviceConstraintMetadata>(mapping.Bytes[responseBytes..]).CopyTo(results.Metadata);
        }
    }
    private readonly struct Collect(DeviceContactResults results) : IJob
    {
        public void Execute() => results._backend.CollectCompleted(results._device);
    }
    public void Dispose() { _commands.Dispose(); _readback.Dispose(); }
}
public readonly struct ReadDeviceContactResults(DeviceContactResults results, int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => results.Schedule(in context, rowsPerPacket, dependency);
}
internal readonly partial struct ApplyDeviceContactResults(DeviceContactResults results) : IJobEntity
{
    public void Execute(Entity entity, [JobResources<DeviceConstraintReferenceResources<DeviceContactConstraint>>] in DeviceConstraintBinding<DeviceContactConstraint> binding,
        [JobResources<DeviceContactResponseResources>] in DeviceContactResponseBinding responseBinding, ref ContactResponse response)
    {
        if (!ReferenceEquals(binding.Data, results.Parameters) || !ReferenceEquals(responseBinding.Responses, results.Responses)) return;
        if (binding.Index >= results._rowCount) return;
        ref readonly DeviceConstraintMetadata metadata = ref results.Metadata[binding.Index];
        if (metadata.Active == 0 || metadata.EntityIndex != (uint)entity.Index || metadata.EntityGeneration != (uint)entity.Generation) return;
        ref readonly DeviceContactResponseState value = ref results.Values[binding.Index];
        response.InitialNormalVelocity = value.InitialNormalVelocity; response.RestitutionImpulse = value.RestitutionImpulse;
        response.ForceOnSecond = value.ForceOnSecond; response.StartTime = value.StartTime; response.EndTime = value.EndTime;
    }
}
