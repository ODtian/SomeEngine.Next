using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct XpbdIterationSettings(int Iterations, int RowsPerBlock);

/// <summary>Projects current typed rows in the shared device graph's query/color order.</summary>
public sealed class XpbdGraphCommands : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceConstraintGraph _graph;
    private readonly XpbdIterationSettings _settings;
    private readonly int _threadsPerGroup;
    private readonly Buffer[] _histories;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private ResourceBinding[][] _bindings;
    private readonly ResourceBinding[][][]? _contactBindings;
    private readonly DeviceContactColumns? _firstContacts;
    private readonly CommandContext _commands;
    private readonly Buffer? _dispatchArguments;
    private readonly BufferUav? _dispatchArgumentsView;
    private readonly IndirectCommandLayout? _dispatchLayout;
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private bool _initialized;
    internal int Iterations => _settings.Iterations;

    public XpbdGraphCommands(IGraphicsBackend backend, Device device, DeviceBodies bodies, DeviceConstraintGraph graph,
        XpbdGraphKernels kernels, XpbdIterationSettings settings, IReadOnlyDictionary<string, ResourceBinding[]> inputs,
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
            var argumentsDescription = new BufferDesc((ulong)graph.ConstraintCapacity * 12,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.Indirect, "XPBD row dispatch arguments");
            _dispatchArguments = graph.Memory is { } memory ? memory.CreateBuffer(argumentsDescription, 12,
                graph.ConstraintCapacity - memory.Capacity) : backend.CreateBuffer(device, argumentsDescription);
            _dispatchArgumentsView = backend.CreateBufferUav(device,
                new(_dispatchArguments, new(0, _dispatchArguments.Info.Size), StructureStride: 4));
            _dispatchLayout = backend.CreateIndirectCommandLayout(device, new([new(IndirectArgumentType.Dispatch)], 12));
            bindings.Add("DeviceXpbdDispatchArguments", [ResourceBinding.WritableBuffer(_dispatchArgumentsView)]);
        }
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        _firstContacts = graph.Contacts?.Current;
        if (graph.Contacts is { } contacts)
        {
            contacts.Current.AddBindings(bindings);
            bindings["DeviceXpbdContactStates"] = [ResourceBinding.WritableBuffer(contacts.Current.StateValues.WriteView)];
            ResourceBinding[][] first = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
            contacts.Previous.AddBindings(bindings);
            bindings["DeviceXpbdContactStates"] = [ResourceBinding.WritableBuffer(contacts.Previous.StateValues.WriteView)];
            ResourceBinding[][] second = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
            _contactBindings = [first, second]; _bindings = first;
        }
        else _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public SolveXpbdGraph Solve() => new(this);
    public InitializeXpbdGraph Initialize() => new(this);
    public InitializeXpbdGraph Initialize(bool buildGraph) => new(this, buildGraph);
    public IterateXpbdGraph Iterate(int iterations) => new(this, iterations);
    public IterateXpbdGraph Iterate(int iterations, bool buildGraph) => new(this, iterations, buildGraph);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency)
        => Schedule(in context, initialize, iterations, true, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess> { _bodies.GetWriteAccess(scheduler) };
        _graph.AddBuildAccesses(scheduler, accesses);
        accesses.AddRange(_extraAccesses);
        if (_graph.Contacts is { } contacts) accesses.AddRange(contacts.Current.GetWriteAccesses(scheduler));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        foreach (Buffer history in _histories)
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(history), 0, (long)history.Info.Size));
        if (_dispatchArguments is { } arguments)
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(arguments), 0, (long)arguments.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context, initialize, iterations, buildGraph, _graph.Contacts?.Current), accesses.ToArray(), dependency);
    }
    internal JobHandle ScheduleSolve(in PhysicsStepContext context, JobHandle dependency) =>
        Schedule(in context, true, _settings.Iterations, dependency);

    private async ValueTask<QueueCompletion> RecordAsync(PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, DeviceContactColumns? contacts)
    {
        if (contacts is not null) _bindings = _contactBindings![ReferenceEquals(contacts, _firstContacts) ? 0 : 1];
        if (initialize || buildGraph)
            foreach (IDeviceConstraintSelection selection in _selections) selection.Capture(in context);
        if (buildGraph) await _graph.BuildAsync(context, contacts).ConfigureAwait(false);
        var step = new DeviceStep((uint)_graph.RowCount, (float)context.Duration, context.Number,
            context.StartTime, context.EndTime, (uint)context.Substep, (uint)context.Substeps,
            0, (uint)_settings.RowsPerBlock, (uint)_threadsPerGroup);
        _backend.Begin(_commands, new(0, 0, 32));
        contacts?.BeginWrite(_backend, _commands);
        if (!_initialized)
        {
            foreach (Buffer history in _histories)
                if (_graph.Memory?.Contains(history) != true) _backend.Barrier(_commands, new BufferBarrier(history, history.InitialSync, PipelineSync.ComputeShading,
                    history.InitialAccess, ResourceAccess.UnorderedAccess));
            if (_dispatchArguments is { } arguments && _graph.Memory?.Contains(arguments) != true)
                _backend.Barrier(_commands, new BufferBarrier(arguments, arguments.InitialSync, PipelineSync.ComputeShading,
                    arguments.InitialAccess, ResourceAccess.UnorderedAccess));
        }
        DispatchArguments blocks = DevicePhysicsDispatch.CreateGroups((_graph.RowCount + _settings.RowsPerBlock - 1) / _settings.RowsPerBlock);
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
        public uint Substep = substep, Substeps = substeps;
        public ulong StepPadding;
        public uint Stage = stage, RowsPerBlock = rowsPerBlock, ThreadsPerGroup = threadsPerGroup;
        public uint Padding;
    }
    private readonly struct Submission(XpbdGraphCommands solve, PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, DeviceContactColumns? contacts) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => solve._backend;
        public Device Device => solve._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => SubmitAsync().GetAwaiter().GetResult();
        public ValueTask<QueueCompletion> SubmitAsync() => solve.RecordAsync(context, initialize, iterations, buildGraph, contacts);
    }
    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _dispatchLayout?.Dispose(); _dispatchArgumentsView?.Dispose(); _dispatchArguments?.Dispose();
    }
}
public readonly partial struct SolveXpbdGraph(XpbdGraphCommands solve) : IPhysicsStage, IPhysicsIterations
{
    private readonly bool _buildGraph = true;
    internal SolveXpbdGraph(XpbdGraphCommands solve, bool buildGraph) : this(solve) => _buildGraph = buildGraph;
    public InitializeXpbdGraph Initialize() => new(solve, _buildGraph);
    public IterateXpbdGraph Iterate(int iterations) => new(solve, iterations, _buildGraph);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _buildGraph
        ? solve.ScheduleSolve(in context, dependency) : solve.Schedule(in context, true, solve.Iterations, false, dependency);
    public JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency) =>
        solve.Schedule(in context, context.FirstIteration == 0, iterations, _buildGraph, dependency);
}
public readonly struct InitializeXpbdGraph(XpbdGraphCommands solve) : IPhysicsStage
{
    private readonly bool _buildGraph = true;
    internal InitializeXpbdGraph(XpbdGraphCommands solve, bool buildGraph) : this(solve) => _buildGraph = buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, true, 0, _buildGraph, dependency);
}
public readonly struct IterateXpbdGraph(XpbdGraphCommands solve, int iterations) : IPhysicsStage
{
    private readonly bool _buildGraph = true;
    internal IterateXpbdGraph(XpbdGraphCommands solve, int iterations, bool buildGraph) : this(solve, iterations) => _buildGraph = buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solve.Schedule(in context, false, iterations, _buildGraph, dependency);
}
