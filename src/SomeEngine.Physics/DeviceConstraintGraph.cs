using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Diagnostics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceGraphCapacity(int Constraints, int BodiesPerConstraint);
public readonly record struct DeviceGraphKernels(ComputePipelineDesc WriteRows, ComputePipelineDesc Build)
{
    public DeviceGraphParallelKernels? Parallel { get; init; }
    public DeviceGraphKernels(ComputePipelineDesc writeRows, DeviceGraphParallelKernels parallel) : this(writeRows, default(ComputePipelineDesc)) => Parallel = parallel;
}

/// <summary>Resident entity participation, connectivity, colors and adjacency shared by concrete device solvers.</summary>
public sealed partial class DeviceConstraintGraph : IDisposable
{
    private static readonly TraceSite BuildTrace = new("physics", "ConstraintGraph.Build");
    private static readonly TraceSite ConnectivityTrace = new("physics", "ConstraintGraph.Connectivity");
    private static readonly TraceSite ActiveWorkTrace = new("physics", "ConstraintGraph.ActiveWork");
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceBodyDynamics _dynamics;
    private readonly DeviceGraphCapacity _capacity;
    private readonly int _threadsPerGroup;
    private readonly Buffer _parameters, _readback;
    private readonly BufferCbv _parametersView;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly Dictionary<string, ResourceBinding[]> _graphBindings = [];
    private readonly List<Buffer> _buffers = [];
    private readonly List<BufferUav> _views = [];
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly uint[] _counts = new uint[5];
    private readonly Buffer _countBuffer;
    private readonly DeviceGraphParallelKernels? _parallel;
    private readonly Buffer? _buildProgress;
    private readonly uint[] _colorProgress = new uint[4];
    private bool _initialized;

    public DeviceConstraintGraph(IGraphicsBackend backend, Device device, DeviceBodies bodies, DeviceBodyDynamics dynamics,
        DeviceGraphKernels kernels, DeviceGraphCapacity capacity, IReadOnlyDictionary<string, ResourceBinding[]> inputs,
        ReadOnlyMemory<JobResourceAccess> inputAccesses, int threadsPerGroup,
        ReadOnlyMemory<IDeviceConstraintSelection> selections = default)
    {
        _backend = backend; _device = device; _bodies = bodies; _dynamics = dynamics; _capacity = capacity;
        _threadsPerGroup = threadsPerGroup; _extraAccesses = inputAccesses.ToArray(); _selections = selections.ToArray();
        _parallel = kernels.Parallel;
        AddBuffer("ConstraintGraphRows", capacity.Constraints, 16);
        AddBuffer("ConstraintGraphParticipants", capacity.Constraints * capacity.BodiesPerConstraint, 8);
        AddBuffer("ConstraintGraphParents", bodies.Capacity, 4);
        AddBuffer("ConstraintGraphActiveBodies", bodies.Capacity, 4);
        AddBuffer("ConstraintGraphBodyColors", bodies.Capacity, 4);
        AddBuffer("ConstraintGraphRowColors", capacity.Constraints, 4);
        AddBuffer("ConstraintGraphBodyOrder", bodies.Capacity, 4);
        AddBuffer("ConstraintGraphRowOrder", capacity.Constraints, 4);
        AddBuffer("ConstraintGraphBodyRanges", bodies.Capacity, 8);
        AddBuffer("ConstraintGraphRowRanges", capacity.Constraints, 8);
        AddBuffer("ConstraintGraphBodyOffsets", bodies.Capacity + 1, 4);
        AddBuffer("ConstraintGraphAdjacency", capacity.Constraints * capacity.BodiesPerConstraint, 4);
        AddBuffer("ConstraintGraphScratch", bodies.Capacity, 4);
        AddBuffer("ConstraintGraphIslands", bodies.Capacity, 4);
        _countBuffer = AddBuffer("ConstraintGraphCounts", 5, 4);
        if (_parallel.HasValue)
        {
            int blocks = (bodies.Capacity + 255) / 256;
            AddBuffer("ConstraintGraphBlockTotals", blocks, 4);
            AddBuffer("ConstraintGraphBlockOffsets", blocks, 4);
            AddBuffer("ConstraintGraphRowProposals", capacity.Constraints, 4);
            AddBuffer("ConstraintGraphGroupColors", capacity.Constraints, 4);
            AddBuffer("ConstraintGraphGroupOffsets", capacity.Constraints + 1, 4);
            _buildProgress = AddBuffer("ConstraintGraphBuildProgress", 4, 4);
        }
        _parameters = backend.CreateBuffer(device, new(256, BufferUsages.Constant, "Constraint graph dimensions"), MemoryType.Upload);
        _parametersView = backend.CreateBufferCbv(device, new(_parameters, new(0, 256)));
        _readback = backend.CreateBuffer(device, new(_parallel.HasValue ? 36UL : 20UL, BufferUsages.CopyDestination, "Constraint graph work counts"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device constraint graph"));
        _graphBindings.Add("ConstraintGraphParameters", [ResourceBinding.ConstantBuffer(_parametersView)]);
        _graphBindings.Add("DeviceBodyStates", [ResourceBinding.WritableBuffer(bodies.View)]);
        dynamics.AddSelectionBindings(_graphBindings);
        var bindings = new Dictionary<string, ResourceBinding[]>(_graphBindings);
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        ComputePipelineDesc[] descriptions = _parallel is { } parallel ? parallel.Descriptions(kernels.WriteRows) : [kernels.WriteRows, kernels.Build];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public int BodyColorCount => (int)_counts[1];
    public int ConstraintStageCount => (int)_counts[2];
    public int BodyCount => (int)_counts[0];
    public int IslandCount => (int)_counts[3];
    public int ConstraintCapacity => _capacity.Constraints;
    internal int MaximumBodyColorSize => (int)_counts[4];
    internal int BodiesPerConstraint => _capacity.BodiesPerConstraint;
    public BuildDeviceConstraintGraph Build() => new(this);
    public BuildDeviceConstraintConnectivity BuildConnectivity() => new(this);
    public UpdateDeviceActiveConstraintWork UpdateActiveWork() => new(this);
    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        foreach (var binding in _graphBindings) bindings.Add(binding.Key, binding.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
    }
    public void AddBuildAccesses(Scheduler scheduler, List<JobResourceAccess> accesses)
    {
        accesses.Add(_bodies.GetReadAccess(scheduler));
        accesses.AddRange(_dynamics.GetSelectionReadAccesses(scheduler));
        accesses.AddRange(_extraAccesses);
        foreach (Buffer buffer in _buffers) accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(buffer), 0, (long)buffer.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_parameters), 0, 256));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        accesses.Add(JobResourceAccess.Write(scheduler, _counts));
        if (_parallel.HasValue) accesses.Add(JobResourceAccess.Write(scheduler, _colorProgress));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
    }
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(); AddBuildAccesses(scheduler, accesses);
        return scheduler.Schedule(new BuildGraph(this), accesses.ToArray(), dependency);
    }
    internal JobHandle ScheduleConnectivity(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!_parallel.HasValue) return Schedule(in context, dependency);
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(); AddBuildAccesses(scheduler, accesses);
        return DevicePhysicsJobs.Schedule(scheduler, new ConnectivitySubmission(this), accesses.ToArray(), dependency);
    }
    internal JobHandle ScheduleActiveWork(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!_parallel.HasValue) return Schedule(in context, dependency);
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(); AddBuildAccesses(scheduler, accesses);
        return scheduler.Schedule(new UpdateActiveGraphWork(this), accesses.ToArray(), dependency);
    }
    internal void BuildAndWait()
    {
        using var trace = EngineTrace.Begin(BuildTrace);
        CaptureSelections();
        WriteParameters(activeWorkOnly: false);
        if (_parallel.HasValue) { BuildParallelAndWait(); return; }
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized)
            foreach (Buffer buffer in _buffers)
                _backend.Barrier(_commands, new BufferBarrier(buffer, buffer.InitialSync, PipelineSync.ComputeShading,
                    buffer.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipelines[0]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[0], _bindings[0], []));
        _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_capacity.Constraints + _threadsPerGroup - 1) / _threadsPerGroup));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipelines[1]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[1], _bindings[1], []));
        _backend.Dispatch(_commands, new(1, 1, 1));
        _backend.Barrier(_commands, new BufferBarrier(_countBuffer, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_countBuffer, 0, _readback, 0, 20));
        _backend.Barrier(_commands, new BufferBarrier(_countBuffer, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        QueueCompletion completed = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        _initialized = true;
        if (_backend.WaitCpu(in completed, TimeSpan.FromSeconds(60)) != WaitStatus.Completed) throw new TimeoutException();
        using (MappedBuffer mapping = _backend.Map(_readback, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            MemoryMarshal.Cast<byte, uint>(mapping.Bytes).CopyTo(_counts);
        }
        _backend.CollectCompleted(_device);
    }
    private QueueCompletion RecordConnectivity()
    {
        using var trace = EngineTrace.Begin(ConnectivityTrace);
        CaptureSelections();
        WriteParameters(activeWorkOnly: false);
        BeginParallelGraph();
        RecordParallelConnectivity();
        return SubmitGraphCommands();
    }
    private void UpdateActiveWorkAndWait()
    {
        using var trace = EngineTrace.Begin(ActiveWorkTrace);
        WriteParameters(activeWorkOnly: true);
        BuildParallelAndWait(activeWorkOnly: true);
    }
    private void WriteParameters(bool activeWorkOnly)
    {
        using MappedBuffer mapping = _backend.Map(_parameters, MapType.Write, BufferRange.Whole);
        Span<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
        values[0] = (uint)_bodies.Capacity; values[1] = (uint)_capacity.Constraints;
        values[2] = (uint)_capacity.BodiesPerConstraint; values[3] = activeWorkOnly ? 1u : 0;
        mapping.Flush(new(0, 16));
    }
    internal void CaptureSelections()
    { foreach (IDeviceConstraintSelection selection in _selections) selection.Capture(); }
    private Buffer AddBuffer(string name, int count, uint stride)
    {
        Buffer buffer = _backend.CreateBuffer(_device, new((ulong)count * stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, name), MemoryType.DeviceLocal);
        BufferUav view = _backend.CreateBufferUav(_device, new(buffer, new(0, buffer.Info.Size), StructureStride: stride));
        _buffers.Add(buffer); _views.Add(view); _graphBindings.Add(name, [ResourceBinding.WritableBuffer(view)]);
        return buffer;
    }
    private readonly struct BuildGraph(DeviceConstraintGraph graph) : IJob { public void Execute() => graph.BuildAndWait(); }
    private readonly struct UpdateActiveGraphWork(DeviceConstraintGraph graph) : IJob { public void Execute() => graph.UpdateActiveWorkAndWait(); }
    private readonly struct ConnectivitySubmission(DeviceConstraintGraph graph) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => graph._backend;
        public Device Device => graph._device;
        public QueueCompletion Submit() => graph.RecordConnectivity();
    }
    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _parametersView.Dispose(); _parameters.Dispose(); _readback.Dispose();
        foreach (BufferUav view in _views) view.Dispose();
        foreach (Buffer buffer in _buffers) buffer.Dispose();
    }
}
public readonly struct BuildDeviceConstraintGraph(DeviceConstraintGraph graph) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => graph.Schedule(in context, dependency);
}
public readonly struct BuildDeviceConstraintConnectivity(DeviceConstraintGraph graph) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => graph.ScheduleConnectivity(in context, dependency);
}
public readonly struct UpdateDeviceActiveConstraintWork(DeviceConstraintGraph graph) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => graph.ScheduleActiveWork(in context, dependency);
}
