using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct DeviceContactKernels(ComputePipelineDesc Initialize, ComputePipelineDesc ApplyRestitution);

/// <summary>Common approach-velocity sampling and restitution, surrounding the selected position solvers.</summary>
public sealed class DeviceContactActions : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceConstraintGraph _graph;
    private readonly DeviceConstraintState<DeviceContactResponseState> _responses;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly CommandContext _commands;
    private readonly JobResourceAccess[] _inputAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly int _threadsPerGroup;
    private bool _initialized;

    public DeviceContactActions(IGraphicsBackend backend, Device device, DeviceBodies bodies, DeviceConstraintGraph graph,
        DeviceConstraintState<DeviceContactResponseState> responses, DeviceContactKernels kernels,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses,
        int threadsPerGroup, ReadOnlyMemory<IDeviceConstraintSelection> selections)
    {
        _backend = backend; _device = device; _bodies = bodies; _graph = graph; _responses = responses;
        _inputAccesses = inputAccesses.ToArray(); _selections = selections.ToArray(); _threadsPerGroup = threadsPerGroup;
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Common device contact response"));
        ComputePipelineDesc[] descriptions = [kernels.Initialize, kernels.ApplyRestitution];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>();
        graph.AddBindings(bindings);
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }
    public InitializeDeviceContactResponses Initialize() => new(this);
    public ApplyDeviceContactRestitution ApplyRestitution(int iterations) => new(this, iterations);
    public ApplyDeviceContactRestitution ApplyRestitution(int iterations, bool buildGraph) => new(this, iterations, buildGraph);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, JobHandle dependency)
        => Schedule(in context, initialize, iterations, true, dependency);
    internal JobHandle Schedule(in PhysicsStepContext context, bool initialize, int iterations, bool buildGraph, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess> { _bodies.GetWriteAccess(scheduler), _responses.GetWriteAccess(scheduler) };
        _graph.AddBuildAccesses(scheduler, accesses);
        accesses.AddRange(_inputAccesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        foreach (IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler, accesses);
        return DevicePhysicsJobs.Schedule(scheduler, new ResponseSubmission(this, context, initialize, iterations, buildGraph), accesses.ToArray(), dependency);
    }
    private QueueCompletion Record(in PhysicsStepContext context, bool initialize, int iterations, bool buildGraph)
    {
        if (initialize) _graph.CaptureSelections();
        if (initialize || buildGraph)
            foreach (IDeviceConstraintSelection selection in _selections) selection.Capture();
        if (!initialize && buildGraph) _graph.BuildAndWait();
        var step = new ResponseStep((float)context.Duration, 0, 0, context.StartTime, context.EndTime);
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized)
            _backend.Barrier(_commands, new BufferBarrier(_responses.States, _responses.States.InitialSync,
                PipelineSync.ComputeShading, _responses.States.InitialAccess, ResourceAccess.UnorderedAccess));
        if (initialize)
        {
            Bind(0, in step);
            Dispatch();
        }
        else
            for (int iteration = 0; iteration < iterations; iteration++)
                for (int stage = 0; stage < _graph.ConstraintStageCount; stage++)
                {
                    step.Stage = (uint)stage;
                    Bind(1, in step);
                    Dispatch();
                }
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Bind(int kernel, in ResponseStep step)
    {
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel],
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
    }
    private void Dispatch()
    {
        _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups((_graph.ConstraintCapacity + _threadsPerGroup - 1) / _threadsPerGroup));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ResponseStep(float duration, uint stage, ulong padding, double start, double end)
    {
        public float Duration = duration;
        public uint Stage = stage;
        public ulong Padding = padding;
        public double StartTime = start, EndTime = end;
    }
    private readonly struct ResponseSubmission(DeviceContactActions actions, PhysicsStepContext context, bool initialize,
        int iterations, bool buildGraph) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => actions._backend;
        public Device Device => actions._device;
        public QueueCompletion Submit() => actions.Record(in context, initialize, iterations, buildGraph);
    }
    public void Dispose() { foreach (Pipeline pipeline in _pipelines) pipeline.Dispose(); _commands.Dispose(); }
}
public readonly struct InitializeDeviceContactResponses(DeviceContactActions actions) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => actions.Schedule(in context, true, 0, dependency);
}
public readonly struct ApplyDeviceContactRestitution(DeviceContactActions actions, int iterations) : IPhysicsStage
{
    private readonly bool _buildGraph = true;
    internal ApplyDeviceContactRestitution(DeviceContactActions actions, int iterations, bool buildGraph) : this(actions, iterations) => _buildGraph = buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => actions.Schedule(in context, false, iterations, _buildGraph, dependency);
}
