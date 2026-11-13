using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceAvbdState<TState>(DeviceConstraintState<TState> States) : ITrackedComponent where TState : unmanaged;
public readonly struct DeviceAvbdStateResources<TState> : IJobComponentResources<DeviceAvbdState<TState>> where TState : unmanaged
{ public static void Collect(in DeviceAvbdState<TState> component, ref JobResourceAccessBuilder resources) { } }
public readonly struct DeviceAvbdStateRows<TState>(DeviceConstraintState<TState> states) : IDeviceConstraintRowSelection where TState : unmanaged
{
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.ReadOptional<DeviceAvbdState<TState>>(true)];
    public Query AddReads(Query query) => query.WithOptionalRead<DeviceAvbdState<TState>>();
    public bool Matches(QueryChunk chunk, int row) => chunk.Has<DeviceAvbdState<TState>>(row)
        && ReferenceEquals(chunk.Read<DeviceAvbdState<TState>>()[row].States, states);
    public bool HasChangedInputs(QueryChunk chunk, ulong since) => chunk.WasWritten<DeviceAvbdState<TState>>(since);
}
public interface IDeviceAvbdStateSource<TComponent, TState> where TComponent : unmanaged where TState : unmanaged
{ TState Read(in TComponent component); }

/// <summary>Writes changed CPU state rows while preserving every continuing device-only numerical value.</summary>
public sealed class DeviceAvbdStateInputs<TComponent, TData, TState, TSource> : IDisposable
    where TComponent : unmanaged, IComponent where TData : unmanaged where TState : unmanaged
    where TSource : struct, IDeviceAvbdStateSource<TComponent, TState>
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly Query _rows;
    private readonly DeviceConstraintData<TData> _parameters;
    private readonly DeviceConstraintState<TState> _states;
    private readonly TSource _source;
    private readonly CommandContext _commands;
    private readonly SystemRunner _runner;
    private ulong _since;
    private bool _first = true;
    public DeviceAvbdStateInputs(IGraphicsBackend backend, Device device, World world, Query rows,
        DeviceConstraintData<TData> parameters, DeviceConstraintState<TState> states, TSource source)
    {
        _backend = backend; _device = device; _world = world; _rows = rows; _parameters = parameters; _states = states; _source = source;
        _runner = new(world);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Edited solver state transfer"));
    }
    public UploadDeviceAvbdState<TComponent, TData, TState, TSource> Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        JobHandle written = _runner.Schedule(_rows, new Write(this), dependency);
        return DevicePhysicsJobs.Schedule(scheduler, new Transfer(this), [_states.GetWriteAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_states.Inputs),0,_states.Inputs.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),JobResourceAccess.Write(scheduler.GetResourceToken(_commands))], written);
    }
    private readonly struct Write(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> input) : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<TComponent>(),
            ChunkAccess.Read<DeviceConstraintBinding<TData>>(true),ChunkAccess.Read<DeviceAvbdState<TState>>(true)];
        public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
        {
            var parameters = rows.Read<DeviceConstraintBinding<TData>>();
            var states = rows.Read<DeviceAvbdState<TState>>();
            int stride = Unsafe.SizeOf<TState>();
            for (int row = 0; row < rows.Count; row++) resources.Write(states[row].States.Inputs, (long)parameters[row].Index * stride, stride);
        }
        public void Execute(SystemChunk rows)
        {
            if (!input._first && !rows.WasWritten<TComponent>(input._since)
                && !rows.WasWritten<DeviceConstraintBinding<TData>>(input._since)
                && !rows.WasWritten<DeviceAvbdState<TState>>(input._since)) return;
            ReadOnlySpan<TComponent> values = rows.Read<TComponent>();
            var bindings = rows.Read<DeviceConstraintBinding<TData>>();
            var states = rows.Read<DeviceAvbdState<TState>>();
            for (int row = 0; row < rows.Count; row++)
            {
                if (!ReferenceEquals(bindings[row].Data, input._parameters) || !ReferenceEquals(states[row].States, input._states)) continue;
                if (!input._first && !rows.WasWritten<TComponent>(row, input._since)
                    && !rows.WasWritten<DeviceConstraintBinding<TData>>(row, input._since)
                    && !rows.WasWritten<DeviceAvbdState<TState>>(row, input._since)) continue;
                TState value = input._source.Read(in values[row]);
                input._states.Memory?.EnsureRows(bindings[row].Index + 1);
                MemoryMarshal.Write(input._states.Inputs.Row(bindings[row].Index), in value);
            }
        }
    }
    private QueueCompletion Submit()
    {
        _backend.Begin(_commands, new(0, 0, 8));
        _states.BeginWrite(_backend, _commands, PipelineSync.Copy, ResourceAccess.CopyDestination);
        _states.Inputs.CopyWrittenPages(_commands, _states.States);
        _backend.Barrier(_commands, new BufferBarrier(_states.States, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        _since = _world.Version; _first = false;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private readonly struct Transfer(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> input) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => input._backend;
        public Device Device => input._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => input.Submit();
    }
    public void Dispose() => _commands.Dispose();
}
public readonly struct UploadDeviceAvbdState<TComponent, TData, TState, TSource>(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> input) : IDevicePhysicsStage
    where TComponent : unmanaged, IComponent where TData : unmanaged where TState : unmanaged
    where TSource : struct, IDeviceAvbdStateSource<TComponent, TState>
{
    public object InputKey => input;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => input.Schedule(in context, dependency);
}
