using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public sealed partial class DevicePhysicsActivity
{
    private int _collectResults = -1, _resultCount;
    private Buffer? _previousResults, _resultValues, _resultCounter, _resultReset;
    private BufferUav? _previousResultView, _resultView, _resultCounterView;
    private bool _resultInitialized;
    private void CreateResultBuffers(DeviceActivityKernels kernels, List<ComputePipelineDesc> descriptions)
    {
        if (kernels.CollectResults is not { } kernel) return;
        _collectResults = descriptions.Count; descriptions.Add(kernel);
        _previousResults = _backend.CreateBuffer(_device, new((ulong)_activities.Bodies.Capacity * 32,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite), MemoryType.DeviceLocal);
        _resultValues = _backend.CreateBuffer(_device, new((ulong)_activities.Bodies.Capacity * 36,
            BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
        _resultCounter = _backend.CreateBuffer(_device, new(4,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination), MemoryType.DeviceLocal);
        _resultReset = _backend.CreateBuffer(_device, new(4, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer mapping = _backend.Map(_resultReset, MapType.Write, new(0, 4)))
        { mapping.Bytes.Clear(); mapping.Flush(mapping.Range); }
        _previousResultView = _backend.CreateBufferUav(_device, new(_previousResults, new(0, _previousResults.Info.Size), StructureStride: 32));
        _resultView = _backend.CreateBufferUav(_device, new(_resultValues, new(0, _resultValues.Info.Size), StructureStride: 36));
        _resultCounterView = _backend.CreateBufferUav(_device, new(_resultCounter, new(0, 4), StructureStride: 4));
    }
    private void AddResultBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        if (_collectResults < 0) return;
        bindings.Add("DeviceBodyActivityPreviousResults", [ResourceBinding.WritableBuffer(_previousResultView!)]);
        bindings.Add("DeviceBodyActivityResults", [ResourceBinding.WritableBuffer(_resultView!)]);
        bindings.Add("DeviceBodyActivityResultCount", [ResourceBinding.WritableBuffer(_resultCounterView!)]);
    }
    private void AddResultAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        foreach (Buffer buffer in new[] { _previousResults!, _resultValues!, _resultCounter! })
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size));
        accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(_resultReset!), 0, 4));
    }
    private async ValueTask<QueueCompletion> CopyResultChangesAsync(PhysicsStepContext context, bool events)
    {
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_resultInitialized)
            foreach (Buffer buffer in new[] { _previousResults!, _resultValues!, _resultCounter! })
                _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_commands, new BufferBarrier(_resultCounter!, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(_resultReset!, 0, _resultCounter!, 0, 4));
        _backend.Barrier(_commands, new BufferBarrier(_resultCounter!, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        var step = new ActivityStep((uint)_activities.Bodies.Capacity, (uint)_graph.ConstraintCapacity,
            (float)context.Duration, _settings, _resultInitialized ? 3u : 4u, (uint)context.Substep, context.Number, context.StartTime, context.EndTime) { Flags = events ? ActivityDataFlags.EventReaders : 0 };
        Dispatch(_collectResults, (_activities.Bodies.Capacity + _threadsPerGroup - 1) / _threadsPerGroup, in step);
        CopyResultPrefix(_resultCounter!, 0, 4);
        QueueCompletion completion = SubmitResultCommands(); _resultInitialized = true;
        await DevicePhysicsJobs.WhenComplete(_backend, completion).ConfigureAwait(false);
        using (MappedBuffer mapping = _backend.Map(_readback, MapType.Read, new(0, 4)))
        { mapping.Invalidate(mapping.Range); _resultCount = MemoryMarshal.Read<int>(mapping.Bytes); }
        if (_resultCount != 0)
        {
            _backend.Begin(_commands, new(0, 0, 8));
            CopyResultPrefix(_resultValues!, 4, (ulong)_resultCount * 36);
            completion = SubmitResultCommands();
        }
        return completion;
    }
    private void CopyResultPrefix(Buffer source, ulong offset, ulong size)
    {
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(source, 0, _readback, offset, size));
        _backend.Barrier(_commands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
    }
    private QueueCompletion SubmitResultCommands()
    {
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void AcceptResultChanges(in PhysicsStepContext context, PhysicsEventBuffer events)
    {
        _activities.UseResultCandidates = true; _activities.ResultCandidates.Clear();
        if (_resultCount == 0) return;
        using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, new(4, (ulong)_resultCount * 36));
        mapping.Invalidate(mapping.Range);
        foreach (ref readonly ActivityResult result in MemoryMarshal.Cast<byte, ActivityResult>(mapping.Bytes))
        {
            _activities.Results[result.Row] = result.State;
            _activities.ResultCandidates.Add(result.Entity);
            DeviceBodyActivities.AddEvents(in result.State, in context, events);
        }
    }
    [StructLayout(LayoutKind.Explicit, Size = 36)]
    private readonly struct ActivityResult
    {
        [FieldOffset(0)] public readonly uint Row;
        [FieldOffset(4)] public readonly DeviceBodyActivityState State;
        [FieldOffset(4)] public readonly Entity Entity;
    }
    private readonly struct ResultSubmission(DevicePhysicsActivity activity, PhysicsStepContext context, bool events) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => activity._backend;
        public Device Device => activity._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => SubmitAsync().GetAwaiter().GetResult();
        public ValueTask<QueueCompletion> SubmitAsync() => activity.CopyResultChangesAsync(context, events);
    }
    private void DisposeResultBuffers()
    {
        _previousResultView?.Dispose(); _resultView?.Dispose(); _resultCounterView?.Dispose();
        _previousResults?.Dispose(); _resultValues?.Dispose(); _resultCounter?.Dispose(); _resultReset?.Dispose();
    }
}
