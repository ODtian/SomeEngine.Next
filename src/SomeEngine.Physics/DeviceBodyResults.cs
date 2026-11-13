using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
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
    private readonly Dictionary<(World World, Query Members), ResultRows> _rows = [];
    private readonly SelectedResultRanges? _selection;
    private int _stateCount;

    public DeviceBodyResults(IGraphicsBackend backend, Device device, DeviceBodies bodies)
        : this(backend, device, bodies, bodies.Capacity) { }

    public DeviceBodyResults(IGraphicsBackend backend, Device device, DeviceBodies bodies, World world, Query members, int capacity)
        : this(backend, device, bodies, capacity) => _selection = new(this, world, members);

    private DeviceBodyResults(IGraphicsBackend backend, Device device, DeviceBodies bodies, int capacity)
    {
        _backend = backend; _device = device; Bodies = bodies;
        States = new DeviceBodyState[capacity];
        _readback = backend.CreateBuffer(device, new((ulong)capacity * DeviceBodies.StateStride, BufferUsages.CopyDestination,
            "Completed body state readback"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device body readback"));
    }

    /// <summary>The completed typed values, reused by the next read on this instance.</summary>
    public ReadOnlyMemory<DeviceBodyState> Values => States.AsMemory(0, _stateCount);
    public ReadDeviceBodyResults Readback(int rowsPerPacket) => new(this, rowsPerPacket);
    public ReadDeviceBodyStates ReadStates(int rowsPerPacket = 256) => new(this, rowsPerPacket);
    internal JobHandle Schedule(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency, bool apply = true)
    {
        Scheduler scheduler = context.World.Execution!.Scheduler!;
        Query members = _selection?.Members ?? context.Bodies;
        if (!_rows.TryGetValue((context.World, members), out ResultRows? rows))
            _rows.Add((context.World, members), rows = new(context.World, members, rowsPerPacket));
        if (_selection is { } selection) return selection.Schedule(context, rows, rowsPerPacket, dependency, apply);
        JobResourceAccess[] accesses = [Bodies.GetReadAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return scheduler.Schedule(new Copy(this, scheduler, rows, accesses, context, apply), accesses, dependency);
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
        JobResourceAccess[] accesses, PhysicsStepContext context, bool apply) : IJob
    {
        public void Execute()
        {
            rows.Baseline = rows.Window.Begin(context);
            QueueCompletion completion = results.Submit();
            JobHandle ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(results._backend, completion), accesses);
            JobResourceAccess[] read = [JobResourceAccess.Read(scheduler.GetResourceToken(results._readback), 0, (long)results._readback.Info.Size),
                JobResourceAccess.Write(scheduler, results.States)];
            JobHandle mapped = scheduler.Schedule(new Map(results), read, ready);
            if (apply) results.ScheduleApply(rows, scheduler, mapped);
            else scheduler.ScheduleFinally(new Collect(results), default, mapped);
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
            results._stateCount = length;
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

/// <summary>Captures typed device values for an explicit consumer while retaining the World's component values.</summary>
public readonly struct ReadDeviceBodyStates(DeviceBodyResults results, int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        results.Schedule(in context, rowsPerPacket, dependency, apply: false);
}
