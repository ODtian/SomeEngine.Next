using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Copies selected completed device states into the original entity components.</summary>
public sealed partial class DeviceBodyResults : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Buffer _readback;
    private readonly CommandContext _commands;
    internal readonly DeviceBodies Bodies;
    internal readonly DeviceBodyState[] States;
    private readonly Dictionary<(World World, QueryDefinition Members), ResultRows> _rows = [];
    private readonly SelectedResultRanges? _selection;

    public DeviceBodyResults(IGraphicsBackend backend, Device device, DeviceBodies bodies)
        : this(backend, device, bodies, bodies.Capacity) { }

    public DeviceBodyResults(IGraphicsBackend backend, Device device, DeviceBodies bodies, World world, QueryHandle members, int capacity)
        : this(backend, device, bodies, capacity) => _selection = new(this, world, members);

    private DeviceBodyResults(IGraphicsBackend backend, Device device, DeviceBodies bodies, int capacity)
    {
        _backend = backend; _device = device; Bodies = bodies;
        States = new DeviceBodyState[capacity];
        _readback = backend.CreateBuffer(device, new((ulong)capacity * DeviceBodies.StateStride, BufferUsages.CopyDestination,
            "Completed body state readback"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device body readback"));
    }

    public ReadDeviceBodyResults Readback(int rowsPerPacket) => new(this, rowsPerPacket);
    internal JobHandle Schedule(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        QueryDefinition members = _selection?.Definition ?? context.Bodies;
        if (!_rows.TryGetValue((context.World, members), out ResultRows? rows))
            _rows.Add((context.World, members), rows = new(context.World, members, States.Length));
        if (_selection is { } selection) return selection.Schedule(rows, rowsPerPacket, dependency);
        JobResourceAccess[] accesses = [Bodies.GetReadAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return scheduler.Schedule(new Copy(this, scheduler, rows, rowsPerPacket, accesses), accesses, dependency);
    }

    private QueueCompletion Submit()
    {
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(Bodies.States, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(Bodies.States, 0, _readback, 0, _readback.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(Bodies.States, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    private readonly struct Copy(DeviceBodyResults results, Scheduler scheduler, ResultRows rows,
        int rowsPerPacket, JobResourceAccess[] accesses) : IJob
    {
        public void Execute()
        {
            QueueCompletion completion = results.Submit();
            JobHandle ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, results._backend, completion), accesses);
            JobResourceAccess[] read = [JobResourceAccess.Read(scheduler.GetResourceToken(results._readback), 0, (long)results._readback.Info.Size),
                JobResourceAccess.Write(scheduler, results.States)];
            JobHandle mapped = scheduler.Schedule(new Map(results), read, ready);
            JobResourceAccess[] capture = [.. rows.Accesses, JobResourceAccess.Read(scheduler, results.States),
                JobResourceAccess.Write(scheduler.GetResourceToken(rows))];
            scheduler.Schedule(new CaptureResults(results, rows, scheduler, rowsPerPacket), capture, mapped);
        }
    }
    private readonly struct Map(DeviceBodyResults results, int count = -1) : IJob
    {
        public void Execute()
        {
            using MappedBuffer mapping = results._backend.Map(results._readback, MapType.Read, BufferRange.Whole);
            int length = count < 0 ? results.States.Length : count;
            if (length != 0) mapping.Invalidate(new(0, (ulong)length * DeviceBodies.StateStride));
            MemoryMarshal.Cast<byte, DeviceBodyState>(mapping.Bytes)[..length].CopyTo(results.States);
        }
    }
    private readonly struct Collect(DeviceBodyResults results) : IJob
    {
        public void Execute() => results._backend.CollectCompleted(results._device);
    }
    public void Dispose()
    {
        foreach (ResultRows rows in _rows.Values) rows.Dispose();
        _selection?.Dispose();
        _commands.Dispose(); _readback.Dispose();
    }
}

public readonly struct ReadDeviceBodyResults(DeviceBodyResults results, int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => results.Schedule(in context, rowsPerPacket, dependency);
}
