using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceContactMatches
{
    internal int InputCount => _detection.ContactCount;
    public UpdateContactRows UpdateRows()=>new(this);
    internal JobHandle ScheduleRows(in PhysicsStepContext context,JobHandle dependency)
    {
        Scheduler scheduler=WorldJobs.GetScheduler(context.World);
        var accesses=new List<JobResourceAccess>{WriteAccess};
        AddSourceAccesses(accesses); AddRowUpdateAccesses(scheduler,accesses);
        return DevicePhysicsJobs.Schedule(scheduler,new RowSubmission(this),accesses.ToArray(),dependency);
    }
    internal void AddRowUpdateAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_rowInputs), 0, _rowInputs.Size));
        accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(_identityInputs), 0, _identityInputs.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_matchingEntities), 0, (long)_matchingEntities.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_rows), 0, (long)_rows.Info.Size));
    }

    internal QueueCompletion SubmitRows()
    {
        DeviceInputPages.MergeRanges(_rowRanges);
        DeviceInputPages.MergeRanges(_identityRanges);
        AcceptLifecycleVersions();
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(_rows, PipelineSync.ComputeShading,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _rowInputs.Copy(_commands, _rows, CollectionsMarshal.AsSpan(_rowRanges));
        _backend.Barrier(_commands, new BufferBarrier(_matchingEntities, PipelineSync.ComputeShading,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _identityInputs.Copy(_commands, _matchingEntities, CollectionsMarshal.AsSpan(_identityRanges));
        _backend.Barrier(_commands, new BufferBarrier(_matchingEntities, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_commands, new BufferBarrier(_rows, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        _rowRanges.Clear();
        _identityRanges.Clear();
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct RowSubmission(DeviceContactMatches matches):IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend=>matches._backend;
        public Device Device=>matches._device;
        public SomeEngine.Graphics.Queue Queue=>Backend.GetQueue(Device,QueueType.Compute);
        public QueueCompletion Submit()=>matches.SubmitRows();
    }
}

public readonly struct UpdateContactRows(DeviceContactMatches matches):IDevicePhysicsStage
{
    public object InputKey=>matches;
    public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>matches.ScheduleRows(in context,dependency);
}
