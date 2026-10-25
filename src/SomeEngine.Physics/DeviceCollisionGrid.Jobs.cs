using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionGrid
{
    internal JobHandle Schedule(Scheduler scheduler, in PhysicsStepContext context, DevicePairing pairing,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback, JobHandle dependency)
    {
        var accesses = new List<JobResourceAccess>();
        var resources = new JobResourceAccessBuilder(scheduler, accesses);
        foreach (var buffer in _gpuBuffers)
        {
            if (ReferenceEquals(buffer, Bodies) && transfer == DeviceInputTransfer.Resident)
                resources.Read(buffer, 0, (long)Capacity.Bodies * InputStride);
            else
                resources.Write(buffer, 0, (long)buffer.Info.Size);
        }
        if (transfer == DeviceInputTransfer.Upload)
            resources.Read(Upload, 0, (long)Capacity.Bodies * InputStride);
        resources.Write(_parameters, 0, 256);
        resources.Write(_commands);
        if (readback == DevicePairReadback.Pairs)
            resources.Write(_readback, 0, (long)_readback.Info.Size);
        accesses.AddRange(_extraAccesses);
        JobResourceAccess[] deviceAccesses = [.. accesses];
        return scheduler.Schedule(new SubmitDeviceGrid(this, scheduler, context, pairing, rules, transfer,
            readback, deviceAccesses), deviceAccesses, dependency);
    }

    private readonly struct SubmitDeviceGrid(DeviceCollisionGrid grid, Scheduler scheduler, PhysicsStepContext context,
        DevicePairing pairing, DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback,
        JobResourceAccess[] deviceAccesses) : IJob
    {
        public void Execute()
        {
            QueueCompletion completion = grid.Submit(pairing, rules, transfer, readback);
            var fence = new DeviceQueueFence(scheduler, grid._backend, completion);
            JobHandle ready = scheduler.CreateExternalFenceHandle(fence, deviceAccesses);
            var accesses = new List<JobResourceAccess>();
            var resources = new JobResourceAccessBuilder(scheduler, accesses);
            if (readback == DevicePairReadback.Pairs)
                resources.Read(grid._readback, 0, (long)grid._readback.Info.Size);
            resources.Write(grid._resultPairs.AsMemory());
            resources.Write(grid._resultOffsets.AsMemory());
            JobHandle consumed = scheduler.Schedule(new AcceptDeviceGrid(grid, context, readback),
                CollectionsMarshal.AsSpan(accesses), ready);
            scheduler.ScheduleFinally(new CollectDeviceGrid(grid), default, consumed);
        }
    }

    private readonly struct AcceptDeviceGrid(DeviceCollisionGrid grid, PhysicsStepContext context,
        DevicePairReadback readback) : IJob
    {
        public void Execute() => grid.AcceptResults(in context, readback);
    }

    private readonly struct CollectDeviceGrid(DeviceCollisionGrid grid) : IJob
    {
        public void Execute() => grid._backend.CollectCompleted(grid._device);
    }
}
