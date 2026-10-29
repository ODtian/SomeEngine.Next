using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceXpbdSettings(int Iterations, int RowsPerBlock);

/// <summary>Projects current typed rows in the shared device graph's query/color order.</summary>
public sealed class DeviceXpbdSolver : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceConstraintGraph _graph;
    private readonly DeviceXpbdSettings _settings;
    private readonly int _threadsPerGroup;
    private readonly Buffer[] _histories;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly CommandContext _commands;
    private readonly Buffer? _dispatchArguments;
    private readonly BufferUav? _dispatchArgumentsView;
    private readonly IndirectCommandLayout? _dispatchLayout;
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private bool _initialized;

    public DeviceXpbdSolver(IGraphicsBackend backend, Device device, DeviceBodies bodies, DeviceConstraintGraph graph,
        DeviceXpbdKernels kernels, DeviceXpbdSettings settings, IReadOnlyDictionary<string, ResourceBinding[]> inputs,
        ReadOnlyMemory<JobResourceAccess> inputAccesses, ReadOnlyMemory<Buffer> histories, int threadsPerGroup,
        ReadOnlyMemory<IDeviceConstraintSelection> selections = default)
    {
        _backend = backend; _device = device; _bodies = bodies; _graph = graph; _settings = settings;
        _threadsPerGroup = threadsPerGroup; _extraAccesses = inputAccesses.ToArray(); _histories = histories.ToArray();
        _selections = selections.ToArray();
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device graph XPBD"));
        ComputePipelineDesc[] descriptions = kernels.WriteDispatchArguments is { } arguments
            ? [kernels.Initialize, kernels.Project, arguments] : [kernels.Initialize, kernels.Project];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>();
        graph.AddBindings(bindings);
        if (kernels.WriteDispatchArguments.HasValue)
        {
            _dispatchArguments = backend.CreateBuffer(device, new((ulong)graph.ConstraintCapacity * 12,
                BufferUsages.ShaderWrite | BufferUsages.Indirect, "XPBD row dispatch arguments"), MemoryType.DeviceLocal);
            _dispatchArgumentsView = backend.CreateBufferUav(device,
                new(_dispatchArguments, new(0, _dispatchArguments.Info.Size), StructureStride: 4));
            _dispatchLayout = backend.CreateIndirectCommandLayout(device, new([new(IndirectArgumentType.Dispatch)], 12));
            bindings.Add("DeviceXpbdDispatchArguments", [ResourceBinding.WritableBuffer(_dispatchArgumentsView)]);
        }
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public SolveDeviceXpbd Solve() => new(this);
    public InitializeDeviceXpbdGraph Initialize() => new(this);
    public InitializeDeviceXpbdGraph Initialize(bool buildGraph) => new(this, buildGraph);
    public IterateDeviceXpbdGraph Iterate(int iterations) => new(this, iterations);
    public IterateDeviceXpbdGraph Iterate(int iterations, bool buildGraph) => new(this, iterations, buildGraph);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency)
        => Schedule(in context, initialize, iterations, true, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess> { _bodies.GetWriteAccess(scheduler) };
        _graph.AddBuildAccesses(scheduler, accesses);
        accesses.AddRange(_extraAccesses);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        foreach (Buffer history in _histories)
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(history), 0, (long)history.Info.Size));
        if (_dispatchArguments is { } arguments)
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(arguments), 0, (long)arguments.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context, initialize, iterations, buildGraph), accesses.ToArray(), dependency);
    }
    internal JobHandle ScheduleSolve(in PhysicsStepContext context, JobHandle dependency) =>
        Schedule(in context, true, _settings.Iterations, dependency);

    private QueueCompletion Record(in PhysicsStepContext context, bool initialize, int iterations, bool buildGraph)
    {
        if (initialize || buildGraph)
            foreach (IDeviceConstraintSelection selection in _selections) selection.Capture();
        if (buildGraph) _graph.BuildAndWait();
        var step = new DeviceStep((uint)_graph.ConstraintCapacity, (float)context.Duration, context.Number,
            context.StartTime, context.EndTime, (uint)context.Substep, (uint)context.Substeps,
            0, (uint)_settings.RowsPerBlock, (uint)_threadsPerGroup);
        _backend.Begin(_commands, new(0, 0, 32));
        if (!_initialized)
        {
            foreach (Buffer history in _histories)
                _backend.Barrier(_commands, new BufferBarrier(history, history.InitialSync, PipelineSync.ComputeShading,
                    history.InitialAccess, ResourceAccess.UnorderedAccess));
            if (_dispatchArguments is { } arguments)
                _backend.Barrier(_commands, new BufferBarrier(arguments, arguments.InitialSync, PipelineSync.ComputeShading,
                    arguments.InitialAccess, ResourceAccess.UnorderedAccess));
        }
        DispatchArguments blocks = DevicePhysicsDispatch.CreateGroups((_graph.ConstraintCapacity + _settings.RowsPerBlock - 1) / _settings.RowsPerBlock);
        if (initialize)
        {
            Bind(0, in step);
            _backend.Dispatch(_commands, in blocks);
            Barrier();
        }
        bool indirect = iterations > 0 && _graph.ConstraintStageCount > 0 && _dispatchArguments is not null;
        if (indirect)
        {
            Bind(2, in step);
            _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups(
                (_graph.ConstraintStageCount + _threadsPerGroup - 1) / _threadsPerGroup));
            _backend.Barrier(_commands, new BufferBarrier(_dispatchArguments!, PipelineSync.ComputeShading, PipelineSync.ExecuteIndirect,
                ResourceAccess.UnorderedAccess, ResourceAccess.IndirectArgument));
        }
        for (int iteration = 0; iteration < iterations; iteration++)
            for (int stage = 0; stage < _graph.ConstraintStageCount; stage++)
            {
                step.Stage = (uint)stage;
                Bind(1, in step);
                if (indirect)
                    _backend.ExecuteIndirect(_commands, _dispatchLayout!, new(_dispatchArguments!, new((ulong)stage * 12, 12)), 1);
                else _backend.Dispatch(_commands, in blocks);
                Barrier();
            }
        if (indirect)
            _backend.Barrier(_commands, new BufferBarrier(_dispatchArguments!, PipelineSync.ExecuteIndirect, PipelineSync.ComputeShading,
                ResourceAccess.IndirectArgument, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Bind(int kernel, in DeviceStep step)
    {
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel],
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
    }
    private void Barrier() => _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
        ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceStep(uint capacity, float duration, long number, double startTime, double endTime,
        uint substep, uint substeps, uint stage, uint rowsPerBlock, uint threadsPerGroup)
    {
        public uint Capacity = capacity;
        public float Duration = duration;
        public long Number = number;
        public double StartTime = startTime, EndTime = endTime;
        public uint Substep = substep, Substeps = substeps, Stage = stage, RowsPerBlock = rowsPerBlock, ThreadsPerGroup = threadsPerGroup;
        public uint Padding;
    }
    private readonly struct Submission(DeviceXpbdSolver solve, PhysicsStepContext context, bool initialize, int iterations, bool buildGraph) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => solve._backend;
        public Device Device => solve._device;
        public QueueCompletion Submit() => solve.Record(in context, initialize, iterations, buildGraph);
    }
    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _dispatchLayout?.Dispose(); _dispatchArgumentsView?.Dispose(); _dispatchArguments?.Dispose();
    }
}
public readonly struct SolveDeviceXpbd(DeviceXpbdSolver solve) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.ScheduleSolve(in context, dependency);
}
public readonly struct InitializeDeviceXpbdGraph(DeviceXpbdSolver solve) : IPhysicsStage
{
    private readonly bool _buildGraph = true;
    internal InitializeDeviceXpbdGraph(DeviceXpbdSolver solve, bool buildGraph) : this(solve) => _buildGraph = buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, _buildGraph, dependency);
}
public readonly struct IterateDeviceXpbdGraph(DeviceXpbdSolver solve, int iterations) : IPhysicsStage
{
    private readonly bool _buildGraph = true;
    internal IterateDeviceXpbdGraph(DeviceXpbdSolver solve, int iterations, bool buildGraph) : this(solve, iterations) => _buildGraph = buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, _buildGraph, dependency);
}
