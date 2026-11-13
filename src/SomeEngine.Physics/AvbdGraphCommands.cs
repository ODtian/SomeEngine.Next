using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct AvbdGraphKernels(ComputePipelineDesc Initialize, ComputePipelineDesc UpdateBody, ComputePipelineDesc UpdateDuals,
    ComputePipelineDesc? InitializeGroups = null, ComputePipelineDesc? UpdateGroups = null,
    ComputePipelineDesc? ClearBodyIndices = null)
{
    public ReadOnlyMemory<AvbdGraphDispatch> GroupInitializationDispatches { get; init; }
    public ReadOnlyMemory<AvbdGraphDispatch> GroupUpdateDispatches { get; init; }
}
public readonly record struct AvbdGraphDispatch(ComputePipelineDesc Kernel, DispatchArguments Groups,
    bool UsesContactCount = false, bool IncludesBodies = false, int SortWidth = 0);
public readonly record struct AvbdIterationSettings(int Iterations, int BodyIterations, float MultiplierRetention, float InitialErrorFraction);

/// <summary>Concrete compliant ALM kernels using current component representations and the shared device graph.</summary>
public sealed class AvbdGraphCommands : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceAvbdBodyEquations _equations;
    private readonly DeviceConstraintGraph _graph;
    private readonly AvbdIterationSettings _settings;
    private readonly int _threadsPerGroup, _bodyThreadsPerGroup;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private ResourceBinding[][] _bindings;
    private readonly ResourceBinding[][][]? _contactBindings;
    private readonly DeviceContactColumns? _firstContacts;
    private readonly CommandContext _commands;
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly (int Pipeline, AvbdGraphDispatch Work)[] _groupInitialization, _groupUpdates;
    private readonly int _clearBodyIndices;
    private bool _initialized;
    private readonly IDeviceConstraintSelection? _contactSelection;
    private readonly bool _equationBindingsFixed;
    private bool _equationInputsUploaded;
    internal DeviceInputTransfer EquationInputs => _equationBindingsFixed && _equationInputsUploaded
        ? DeviceInputTransfer.Resident : DeviceInputTransfer.Upload;

    public AvbdGraphCommands(IGraphicsBackend backend, Device device, DeviceBodies bodies, DeviceAvbdBodyEquations equations,
        DeviceConstraintGraph graph, AvbdGraphKernels kernels, AvbdIterationSettings settings,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses,
        int threadsPerGroup, ReadOnlyMemory<IDeviceConstraintSelection> selections = default, IDeviceConstraintSelection? contactSelection = null,
        bool equationBindingsFixed = false)
    {
        _backend = backend; _device = device; _bodies = bodies; _equations = equations; _graph = graph; _settings = settings;
        _threadsPerGroup = threadsPerGroup; _extraAccesses = inputAccesses.ToArray(); _selections = selections.ToArray(); _contactSelection = contactSelection;
        _equationBindingsFixed = equationBindingsFixed;
        _recordingInputs = [.. graph.RecordingInputs, .. _selections];
        Span<nuint> bodyGroupSize = stackalloc nuint[3];
        kernels.UpdateBody.Compute.GetComputeThreadGroupSize(bodyGroupSize);
        _bodyThreadsPerGroup = (int)bodyGroupSize[0];
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device compliant ALM"));
        var descriptions = new List<ComputePipelineDesc> { kernels.Initialize, kernels.UpdateBody, kernels.UpdateDuals };
        _groupInitialization = AddGroupDispatches(descriptions, kernels.InitializeGroups, kernels.GroupInitializationDispatches.Span);
        _groupUpdates = AddGroupDispatches(descriptions, kernels.UpdateGroups, kernels.GroupUpdateDispatches.Span);
        _clearBodyIndices = kernels.ClearBodyIndices.HasValue ? descriptions.Count : -1;
        if (kernels.ClearBodyIndices is { } clear) descriptions.Add(clear);
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>();
        graph.AddBindings(bindings); equations.AddBindings(bindings);
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        _firstContacts = graph.Contacts?.Current;
        if (graph.Contacts is { } contacts)
        {
            contacts.Current.AddBindings(bindings);
            bindings["DeviceAvbdContactStates"] = [ResourceBinding.WritableBuffer(contacts.Current.StateValues.WriteView)];
            ResourceBinding[][] first = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
            contacts.Previous.AddBindings(bindings);
            bindings["DeviceAvbdContactStates"] = [ResourceBinding.WritableBuffer(contacts.Previous.StateValues.WriteView)];
            ResourceBinding[][] second = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
            _contactBindings = [first, second]; _bindings = first;
        }
        else _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }
    internal IReadOnlyList<object> RecordingInputs => _recordingInputs;
    private readonly object[] _recordingInputs;
    public SolveAvbdGraph Solve() => new(this);
    public SolveAvbdGraph Solve(DeviceInputTransfer equationInputs) => new(this, equationInputs);
    public SolveAvbdGraph Solve(bool buildGraph) => new(this, buildGraph);
    public SolveAvbdGraph Solve(bool buildGraph, DeviceInputTransfer equationInputs)
        => new(this, buildGraph, equationInputs);
    public SolveAvbdGraph SolveIterations(int iterations, bool buildGraph, DeviceInputTransfer equationInputs, float? retention = null)
        => new(this, buildGraph, equationInputs, iterations, retention);
    public InitializeAvbdGraph Initialize() => new(this);
    public InitializeAvbdGraph Initialize(DeviceInputTransfer equationInputs) => new(this, equationInputs);
    public InitializeAvbdGraph Initialize(bool buildGraph) => new(this, buildGraph, null);
    public InitializeAvbdGraph Initialize(bool buildGraph, DeviceInputTransfer equationInputs)
        => new(this, buildGraph, equationInputs);
    public IterateAvbdGraph Iterate(int iterations) => new(this, iterations);
    public IterateAvbdGraph Iterate(int iterations, bool buildGraph) => new(this, iterations, buildGraph);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency)
        => Schedule(in context, initialize, iterations, true, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, JobHandle dependency)
        => Schedule(in context, initialize, iterations, buildGraph, DeviceInputTransfer.Upload, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, bool buildGraph,
        DeviceInputTransfer equationInputs, JobHandle dependency, float? retention = null)
    {
        if (initialize && equationInputs == DeviceInputTransfer.Upload)
        {
            dependency = _equations.Upload(in context, dependency);
            _equationInputsUploaded = true;
        }
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess> { _bodies.GetWriteAccess(scheduler) };
        _equations.AddAccesses(scheduler, accesses); _graph.AddBuildAccesses(scheduler, accesses);
        accesses.AddRange(_extraAccesses);
        if (_graph.Contacts is { } contacts) accesses.AddRange(contacts.Current.GetWriteAccesses(scheduler));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        return DevicePhysicsJobs.Schedule(scheduler, new SolverSubmission(this, context, initialize, iterations, buildGraph, retention ?? _settings.MultiplierRetention, _graph.Contacts?.Current), accesses.ToArray(), dependency);
    }
    internal JobHandle ScheduleSolve(in PhysicsStepContext context, bool buildGraph, DeviceInputTransfer equationInputs, JobHandle dependency)
        => Schedule(in context, true, _settings.Iterations, buildGraph, equationInputs, dependency);
    private async ValueTask<QueueCompletion> RecordAsync(PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, float retention, DeviceContactColumns? contacts)
    {
        if (contacts is not null) _bindings = _contactBindings![ReferenceEquals(contacts, _firstContacts) ? 0 : 1];
        if (initialize || buildGraph)
            foreach (IDeviceConstraintSelection selection in _selections) selection.Capture(in context);
        if (buildGraph) await _graph.BuildAsync(context, contacts).ConfigureAwait(false);
        var step = new DeviceStep((float)context.Duration, retention, _settings.InitialErrorFraction,
            (uint)_settings.BodyIterations, 0, (uint)context.Substep, context.Number, context.StartTime, context.EndTime, (uint)context.FirstIteration);
        _backend.Begin(_commands, new(0, 0, 32));
        contacts?.BeginWrite(_backend, _commands);
        if (!_initialized)
        {
            _backend.Barrier(_commands, new BufferBarrier(_equations.Values, _equations.Values.InitialSync, PipelineSync.ComputeShading,
                _equations.Values.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands, new BufferBarrier(_equations.Indices, _equations.Indices.InitialSync, PipelineSync.ComputeShading,
                _equations.Indices.InitialAccess, ResourceAccess.UnorderedAccess));
        }
        if (initialize)
        {
            if (_clearBodyIndices >= 0)
            {
                Bind(_clearBodyIndices, in step);
                _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_bodies.Capacity + _threadsPerGroup - 1) / _threadsPerGroup));
                Barrier();
            }
            Bind(0, in step);
            _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((Math.Max(_bodies.Capacity, _graph.RowCount) + _threadsPerGroup - 1) / _threadsPerGroup));
            Barrier();
            DispatchGroups(_groupInitialization, in step);
        }
        for (int iteration = 0; iteration < iterations && _graph.BodyCount != 0; iteration++)
        {
            for (int color = 0; color < _graph.BodyColorCount; color++)
            {
                step.Color = (uint)color; Bind(1, in step);
                _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_graph.MaximumBodyColorSize + _bodyThreadsPerGroup - 1) / _bodyThreadsPerGroup));
                Barrier();
            }
            step.Color = 0;
            if (_graph.ConstraintStageCount > 0)
            {
                Bind(2, in step);
                _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_graph.RowCount + _threadsPerGroup - 1) / _threadsPerGroup));
                Barrier();
            }
            DispatchGroups(_groupUpdates, in step);
        }
        using RecordedCommands recorded = _backend.End(_commands);
        QueueCompletion completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        _initialized = true;
        return completion;
    }
    private static (int Pipeline, AvbdGraphDispatch Work)[] AddGroupDispatches(List<ComputePipelineDesc> kernels,
        ComputePipelineDesc? single, ReadOnlySpan<AvbdGraphDispatch> dispatches)
    {
        var selected = new (int Pipeline, AvbdGraphDispatch Work)[dispatches.Length + (single.HasValue ? 1 : 0)];
        int index = 0;
        if (single is { } kernel)
        {
            selected[index++] = (kernels.Count, new(kernel, new(1, 1, 1)));
            kernels.Add(kernel);
        }
        foreach (AvbdGraphDispatch dispatch in dispatches)
        {
            selected[index++] = (kernels.Count, dispatch);
            kernels.Add(dispatch.Kernel);
        }
        return selected;
    }
    private void DispatchGroups(ReadOnlySpan<(int Pipeline, AvbdGraphDispatch Work)> dispatches, in DeviceStep step)
    {
        foreach (var dispatch in dispatches)
        {
            AvbdGraphDispatch work = dispatch.Work;
            DispatchArguments groups = work.Groups;
            if (_contactSelection is { } contacts)
            {
                if (work.SortWidth != 0 && work.SortWidth >= contacts.Count) continue;
                if (work.UsesContactCount)
                {
                    int count = Math.Max(work.IncludesBodies ? _bodies.Capacity : 0, contacts.Count);
                    if (count == 0) continue;
                    groups = DevicePhysicsDispatch.CreateGroups((count + 63) / 64);
                }
            }
            Bind(dispatch.Pipeline, in step);
            _backend.Dispatch(_commands, in groups);
            Barrier();
        }
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
    private struct DeviceStep(float duration, float retention, float fraction, uint bodyIterations, uint color, uint substep,
        long number, double start, double end, uint firstIteration)
    {
        public float Duration = duration, Retention = retention, InitialErrorFraction = fraction;
        public uint BodyIterations = bodyIterations, Color = color, Substep = substep;
        public long Number = number;
        public double StartTime = start, EndTime = end;
        public uint FirstIteration = firstIteration, IterationPadding;
        public ulong StepPadding;
    }
    private readonly struct SolverSubmission(AvbdGraphCommands solver, PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, float retention, DeviceContactColumns? contacts) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => solver._backend;
        public Device Device => solver._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => SubmitAsync().GetAwaiter().GetResult();
        public ValueTask<QueueCompletion> SubmitAsync() => solver.RecordAsync(context, initialize, iterations, buildGraph, retention, contacts);
    }
    public void Dispose() { foreach (Pipeline pipeline in _pipelines) pipeline.Dispose(); _commands.Dispose(); }
}
public readonly partial struct SolveAvbdGraph(AvbdGraphCommands solver) : IDevicePhysicsStage, IPhysicsIterations
{
    public object InputKey => solver;
    public IReadOnlyList<object> SharedInputs => solver.RecordingInputs;
    public bool CanBatch => !_buildGraph && Inputs == DeviceInputTransfer.Resident;
    private readonly DeviceInputTransfer? _equationInputs;
    private DeviceInputTransfer Inputs => _equationInputs ?? solver.EquationInputs;
    private readonly bool _buildGraph = true;
    private readonly int? _iterations;
    private readonly float? _retention;
    public InitializeAvbdGraph Initialize() => new(solver, _buildGraph, _equationInputs);
    public IterateAvbdGraph Iterate(int iterations) => new(solver, iterations, _buildGraph);
    internal SolveAvbdGraph(AvbdGraphCommands solver, bool buildGraph) : this(solver) => _buildGraph = buildGraph;
    internal SolveAvbdGraph(AvbdGraphCommands solver, DeviceInputTransfer equationInputs) : this(solver) => _equationInputs = equationInputs;
    internal SolveAvbdGraph(AvbdGraphCommands solver, bool buildGraph, DeviceInputTransfer equationInputs) : this(solver)
    { _buildGraph = buildGraph; _equationInputs = equationInputs; }
    internal SolveAvbdGraph(AvbdGraphCommands solver, bool buildGraph, DeviceInputTransfer equationInputs, int iterations, float? retention) : this(solver, buildGraph, equationInputs)
    { _iterations = iterations; _retention = retention; }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _iterations is { } count
        ? solver.Schedule(in context, true, count, _buildGraph, Inputs, dependency, _retention)
        : solver.ScheduleSolve(in context, _buildGraph, Inputs, dependency);
    public JobHandle ScheduleIterations(in PhysicsStepContext context, int iterations, JobHandle dependency) =>
        solver.Schedule(in context, true, iterations, _buildGraph, Inputs, dependency, context.FirstIteration == 0 ? _retention : 1);
}
public readonly struct InitializeAvbdGraph(AvbdGraphCommands solver) : IDevicePhysicsStage
{
    public object InputKey => solver;
    public IReadOnlyList<object> SharedInputs => solver.RecordingInputs;
    public bool CanBatch => !_buildGraph && Inputs == DeviceInputTransfer.Resident;
    private readonly DeviceInputTransfer? _equationInputs;
    private DeviceInputTransfer Inputs => _equationInputs ?? solver.EquationInputs;
    private readonly bool _buildGraph = true;
    internal InitializeAvbdGraph(AvbdGraphCommands solver, DeviceInputTransfer equationInputs) : this(solver) => _equationInputs = equationInputs;
    internal InitializeAvbdGraph(AvbdGraphCommands solver, bool buildGraph, DeviceInputTransfer? equationInputs) : this(solver)
    { _buildGraph = buildGraph; _equationInputs = equationInputs; }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solver.Schedule(in context, true, 0, _buildGraph, Inputs, dependency);
}
public readonly struct IterateAvbdGraph(AvbdGraphCommands solver, int iterations) : IDevicePhysicsStage
{
    public object InputKey => solver;
    public IReadOnlyList<object> SharedInputs => solver.RecordingInputs;
    public bool CanBatch => !_buildGraph;
    private readonly bool _buildGraph = true;
    internal IterateAvbdGraph(AvbdGraphCommands solver, int iterations, bool buildGraph) : this(solver, iterations) => _buildGraph = buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => solver.Schedule(in context, false, iterations, _buildGraph, dependency);
}
