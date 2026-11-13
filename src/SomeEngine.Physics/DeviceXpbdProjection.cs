using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct XpbdGraphKernels(ComputePipelineDesc Initialize, ComputePipelineDesc Project)
{
    public ComputePipelineDesc? WriteDispatchArguments { get; init; }
}

/// <summary>Runs a concrete device projection against shared physical parameters and per-entity XPBD history.</summary>
public sealed class DeviceXpbdProjection<TData, TState> : IDisposable where TData : unmanaged where TState : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly BodyDynamicsCommands _dynamics;
    private readonly DeviceConstraintData<TData> _data;
    private readonly DeviceConstraintState<TState> _state;
    private readonly World _world;
    private readonly Query _rows;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly Buffer _parameters;
    private readonly Buffer _indices;
    private readonly BufferSrv _indicesView;
    private readonly BufferUav _metadataView;
    private readonly BufferCbv _parametersView;
    private readonly CommandContext _commands;
    private readonly int _threadsPerGroup;
    private readonly JobResourceAccess[] _extraAccesses;
    private bool _initialized;
    private readonly PhysicsQueryWindow _window;

    public DeviceXpbdProjection(IGraphicsBackend backend, Device device, World world, Query rows,
        DeviceBodies bodies, BodyDynamicsCommands dynamics, DeviceConstraintData<TData> data,
        DeviceConstraintState<TState> state, XpbdGraphKernels kernels, int threadsPerGroup,
        IReadOnlyDictionary<string, ResourceBinding[]>? extraBindings = null, ReadOnlyMemory<JobResourceAccess> extraAccesses = default)
    {
        _backend = backend; _device = device; _bodies = bodies; _data = data; _state = state; _threadsPerGroup = threadsPerGroup;
        _dynamics = dynamics;
        _world = world;
        _rows = rows.WithOptionalRead<DeviceConstraintBinding<TData>>().WithOptionalRead<DeviceXpbdState<TState>>();
        _window = new(_rows);
        _extraAccesses = extraAccesses.ToArray();
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Device XPBD step"), MemoryType.Upload);
        _parametersView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        _indices = backend.CreateBuffer(device, new((ulong)data.Capacity * 16, BufferUsages.ShaderRead,
            "XPBD selected constraint indices"), MemoryType.Upload);
        _indicesView = backend.CreateBufferSrv(device, new(_indices, new(0, _indices.Info.Size), StructureStride: 16));
        _metadataView = backend.CreateBufferUav(device, new(data.Metadata, new(0, data.Metadata.Info.Size), StructureStride: 16));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device XPBD projection"));
        ComputePipelineDesc[] descriptions = [kernels.Initialize, kernels.Project];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["XpbdStep"] = [ResourceBinding.ConstantBuffer(_parametersView)],
            ["DeviceXpbdRows"] = [ResourceBinding.ReadOnlyBuffer(_indicesView)],
            ["DeviceXpbdMetadata"] = [ResourceBinding.WritableBuffer(_metadataView)],
            ["DeviceBodyStates"] = [ResourceBinding.WritableBuffer(bodies.View)],
        };
        data.AddBindings(bindings); state.AddBindings(bindings);
        dynamics.AddSelectionBindings(bindings);
        if (extraBindings is not null) foreach (var binding in extraBindings) bindings.Add(binding.Key, binding.Value);
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public InitializeDeviceXpbd<TData, TState> Initialize() => new(this);
    public IterateDeviceXpbd<TData, TState> Iterate(int iterations) => new(this, iterations);
    internal JobHandle Schedule(in PhysicsStepContext context, int kernel, int iterations, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. ((WorldJobs)_world.Execution!).CollectAccess<RowAccess>(_rows), _bodies.GetWriteAccess(scheduler), .. _dynamics.GetSelectionReadAccesses(scheduler),
            .. _data.GetReadAccesses(scheduler),
            _state.GetWriteAccess(scheduler), .. _extraAccesses,
            JobResourceAccess.Write(scheduler.GetResourceToken(_parameters), 0, 256),
            JobResourceAccess.Write(scheduler.GetResourceToken(_indices), 0, (long)_indices.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, new Projection(this, context, kernel, iterations), accesses, dependency);
    }
    private QueueCompletion Submit(in PhysicsStepContext context, int kernel, int iterations)
    {
        int count;
        using (MappedBuffer mapping = _backend.Map(_indices, MapType.Write, BufferRange.Whole))
        {
            count = 0;
            using QueryScope scope = _rows.Since(_window.Begin(context)).OpenRead();
            foreach (QueryChunk chunk in scope)
            {
                var bindings = chunk.Read<DeviceConstraintBinding<TData>>(); var states = chunk.Read<DeviceXpbdState<TState>>();
                for (int row = 0; row < chunk.Count; row++)
                    if (ReferenceEquals(bindings[row].Data, _data) && ReferenceEquals(states[row].States, _state))
                        MemoryMarshal.Cast<byte, SelectedRow>(mapping.Bytes)[count++] = new((uint)bindings[row].Index,
                            (uint)chunk.Entities[row].Id, (uint)chunk.Entities[row].Generation);
            }
            _window.Accept(_world.Version);
            mapping.Flush(new(0, (ulong)count * 16));
        }
        var step = new XpbdStep((uint)count, (float)context.Duration, context.Number,
            context.StartTime, context.EndTime, (uint)context.Substep, (uint)context.Substeps);
        using (MappedBuffer mapping = _backend.Map(_parameters, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.Write(mapping.Bytes, in step);
            mapping.Flush(new(0, (ulong)Unsafe.SizeOf<XpbdStep>()));
        }
        _state.Memory?.EnsureRows(_data.RowCount);
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized && _state.Memory?.Contains(_state.States) != true)
            _backend.Barrier(_commands, new BufferBarrier(_state.States, _state.States.InitialSync, PipelineSync.ComputeShading,
                _state.States.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel], []));
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            if (count != 0) _backend.Dispatch(_commands, new((uint)((count + _threadsPerGroup - 1) / _threadsPerGroup), 1, 1));
            _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        }
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private readonly record struct XpbdStep(uint Capacity, float Duration, long Number, double StartTime,
        double EndTime, uint Substep, uint Substeps);
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct SelectedRow(uint Index, uint EntityIndex, uint EntityGeneration);
    private readonly struct RowAccess : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<DeviceConstraintBinding<TData>>(true), ChunkAccess.Read<DeviceXpbdState<TState>>(true)];
        public void Execute(SystemChunk chunk) { }
    }
    private readonly struct Projection(DeviceXpbdProjection<TData, TState> project, PhysicsStepContext context, int kernel, int iterations)
        : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => project._backend;
        public Device Device => project._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => project.Submit(in context, kernel, iterations);
    }
    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _parametersView.Dispose(); _parameters.Dispose(); _indicesView.Dispose(); _indices.Dispose(); _metadataView.Dispose();
    }
}
public readonly struct InitializeDeviceXpbd<TData, TState>(DeviceXpbdProjection<TData, TState> project) : IPhysicsStage
    where TData : unmanaged where TState : unmanaged
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => project.Schedule(in context, 0, 1, dependency);
}
public readonly struct IterateDeviceXpbd<TData, TState>(DeviceXpbdProjection<TData, TState> project, int iterations) : IPhysicsStage
    where TData : unmanaged where TState : unmanaged
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => project.Schedule(in context, 1, iterations, dependency);
}
