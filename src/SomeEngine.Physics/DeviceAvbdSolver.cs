using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct DeviceAvbdKernels(ComputePipelineDesc Initialize, ComputePipelineDesc UpdateBody, ComputePipelineDesc UpdateDuals,
    ComputePipelineDesc? InitializeGroups = null, ComputePipelineDesc? UpdateGroups = null,
    ComputePipelineDesc? ClearBodyIndices = null)
{
    public ReadOnlyMemory<DeviceAvbdGroupDispatch> GroupInitializationDispatches { get; init; }
    public ReadOnlyMemory<DeviceAvbdGroupDispatch> GroupUpdateDispatches { get; init; }
}
public readonly record struct DeviceAvbdGroupDispatch(ComputePipelineDesc Kernel, DispatchArguments Groups);
public readonly record struct DeviceAvbdSettings(int Iterations, int BodyIterations, float MultiplierRetention, float InitialErrorFraction);

/// <summary>Concrete compliant ALM kernels using current component representations and the shared device graph.</summary>
public sealed class DeviceAvbdSolver : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceAvbdBodyEquations _equations;
    private readonly DeviceConstraintGraph _graph;
    private readonly DeviceAvbdSettings _settings;
    private readonly int _threadsPerGroup;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly CommandContext _commands;
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly IDeviceConstraintSelection[] _selections;
    private readonly (int Pipeline, DispatchArguments Groups)[] _groupInitialization, _groupUpdates;
    private readonly int _clearBodyIndices;
    private bool _initialized;

    public DeviceAvbdSolver(IGraphicsBackend backend, Device device, DeviceBodies bodies, DeviceAvbdBodyEquations equations,
        DeviceConstraintGraph graph, DeviceAvbdKernels kernels, DeviceAvbdSettings settings,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses,
        int threadsPerGroup, ReadOnlyMemory<IDeviceConstraintSelection> selections = default)
    {
        _backend=backend; _device=device; _bodies=bodies; _equations=equations; _graph=graph; _settings=settings;
        _threadsPerGroup=threadsPerGroup; _extraAccesses=inputAccesses.ToArray(); _selections=selections.ToArray();
        _commands=backend.CreateCommandContext(device,new(QueueType.Compute,0,1,Label:"Device compliant ALM"));
        var descriptions=new List<ComputePipelineDesc>{kernels.Initialize,kernels.UpdateBody,kernels.UpdateDuals};
        _groupInitialization=AddGroupDispatches(descriptions,kernels.InitializeGroups,kernels.GroupInitializationDispatches.Span);
        _groupUpdates=AddGroupDispatches(descriptions,kernels.UpdateGroups,kernels.GroupUpdateDispatches.Span);
        _clearBodyIndices=kernels.ClearBodyIndices.HasValue?descriptions.Count:-1;
        if(kernels.ClearBodyIndices is { } clear) descriptions.Add(clear);
        _pipelines=descriptions.Select(kernel=>backend.CreateComputePipeline(device,kernel)).ToArray();
        _layouts=descriptions.Select(kernel=>kernel.Reflection.GetGlobalParamsVarLayout()??VariableLayoutReflection.Null).ToArray();
        var bindings=new Dictionary<string,ResourceBinding[]>();
        graph.AddBindings(bindings); equations.AddBindings(bindings);
        foreach(var input in inputs) bindings.Add(input.Key,input.Value);
        foreach(IDeviceConstraintSelection selection in _selections) selection.AddBindings(bindings);
        _bindings=_layouts.Select(layout=>DeviceCollisionGrid.OrderBindings(layout,bindings)).ToArray();
    }
    public SolveDeviceAvbd Solve()=>new(this);
    public SolveDeviceAvbd Solve(DeviceInputTransfer equationInputs)=>new(this,equationInputs);
    public InitializeDeviceAvbd Initialize()=>new(this);
    public InitializeDeviceAvbd Initialize(DeviceInputTransfer equationInputs)=>new(this,equationInputs);
    public IterateDeviceAvbd Iterate(int iterations)=>new(this,iterations);
    public IterateDeviceAvbd Iterate(int iterations,bool buildGraph)=>new(this,iterations,buildGraph);
    internal JobHandle Schedule(in PhysicsStepContext context,bool initialize,int iterations,JobHandle dependency)
        =>Schedule(in context,initialize,iterations,true,dependency);
    internal JobHandle Schedule(in PhysicsStepContext context,bool initialize,int iterations,bool buildGraph,JobHandle dependency)
        =>Schedule(in context,initialize,iterations,buildGraph,DeviceInputTransfer.Upload,dependency);
    internal JobHandle Schedule(in PhysicsStepContext context,bool initialize,int iterations,bool buildGraph,
        DeviceInputTransfer equationInputs,JobHandle dependency)
    {
        if(initialize&&equationInputs==DeviceInputTransfer.Upload) dependency=_equations.Upload(in context,_threadsPerGroup,dependency);
        Scheduler scheduler=WorldJobs.GetScheduler(context.World);
        var accesses=new List<JobResourceAccess>{_bodies.GetWriteAccess(scheduler)};
        _equations.AddAccesses(scheduler,accesses); _graph.AddBuildAccesses(scheduler,accesses);
        accesses.AddRange(_extraAccesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        foreach(IDeviceConstraintSelection selection in _selections) selection.AddAccesses(scheduler,accesses);
        return DevicePhysicsJobs.Schedule(scheduler,new SolverSubmission(this,context,initialize,iterations,buildGraph),accesses.ToArray(),dependency);
    }
    internal JobHandle ScheduleSolve(in PhysicsStepContext context,JobHandle dependency)
        =>ScheduleSolve(in context,DeviceInputTransfer.Upload,dependency);
    internal JobHandle ScheduleSolve(in PhysicsStepContext context,DeviceInputTransfer equationInputs,JobHandle dependency)
    {
        JobHandle initialized=Schedule(in context,true,0,true,equationInputs,dependency);
        return Schedule(in context,false,_settings.Iterations,initialized);
    }
    private QueueCompletion Record(in PhysicsStepContext context,bool initialize,int iterations,bool buildGraph)
    {
        _graph.CaptureSelections();
        foreach(IDeviceConstraintSelection selection in _selections) selection.Capture();
        if(!initialize&&buildGraph) _graph.BuildAndWait();
        var step=new DeviceStep((float)context.Duration,_settings.MultiplierRetention,_settings.InitialErrorFraction,
            (uint)_settings.BodyIterations,0,(uint)context.Substep,context.Number,context.StartTime,context.EndTime);
        _backend.Begin(_commands,new(0,0,32));
        if(!_initialized)
        {
            _backend.Barrier(_commands,new BufferBarrier(_equations.Values,_equations.Values.InitialSync,PipelineSync.ComputeShading,
                _equations.Values.InitialAccess,ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands,new BufferBarrier(_equations.Indices,_equations.Indices.InitialSync,PipelineSync.ComputeShading,
                _equations.Indices.InitialAccess,ResourceAccess.UnorderedAccess));
        }
        if(initialize)
        {
            if(_clearBodyIndices>=0)
            {
                Bind(_clearBodyIndices,in step);
                _backend.Dispatch(_commands,DevicePhysicsDispatch.CreateGroups((_bodies.Capacity+_threadsPerGroup-1)/_threadsPerGroup));
                Barrier();
            }
            Bind(0,in step);
            _backend.Dispatch(_commands,DevicePhysicsDispatch.CreateGroups((Math.Max(_bodies.Capacity,_graph.ConstraintCapacity)+_threadsPerGroup-1)/_threadsPerGroup));
            Barrier();
            DispatchGroups(_groupInitialization,in step);
        }
        else
            for(int iteration=0;iteration<iterations;iteration++)
            {
                for(int color=0;color<_graph.BodyColorCount;color++)
                {
                    step.Color=(uint)color; Bind(1,in step);
                    _backend.Dispatch(_commands,DevicePhysicsDispatch.CreateGroups((_bodies.Capacity+_threadsPerGroup-1)/_threadsPerGroup));
                    Barrier();
                }
                step.Color=0; Bind(2,in step);
                _backend.Dispatch(_commands,DevicePhysicsDispatch.CreateGroups((_graph.ConstraintCapacity+_threadsPerGroup-1)/_threadsPerGroup));
                Barrier();
                DispatchGroups(_groupUpdates,in step);
            }
        using RecordedCommands recorded=_backend.End(_commands);
        QueueCompletion completion=_backend.Submit(_backend.GetQueue(_device,QueueType.Compute),new([],[],[recorded],[],[]));
        _initialized=true;
        return completion;
    }
    private static (int Pipeline, DispatchArguments Groups)[] AddGroupDispatches(List<ComputePipelineDesc> kernels,
        ComputePipelineDesc? single, ReadOnlySpan<DeviceAvbdGroupDispatch> dispatches)
    {
        var selected=new (int Pipeline, DispatchArguments Groups)[dispatches.Length+(single.HasValue?1:0)];
        int index=0;
        if(single is { } kernel)
        {
            selected[index++]=(kernels.Count,new(1,1,1));
            kernels.Add(kernel);
        }
        foreach(DeviceAvbdGroupDispatch dispatch in dispatches)
        {
            selected[index++]=(kernels.Count,dispatch.Groups);
            kernels.Add(dispatch.Kernel);
        }
        return selected;
    }
    private void DispatchGroups(ReadOnlySpan<(int Pipeline, DispatchArguments Groups)> dispatches,in DeviceStep step)
    {
        foreach(var dispatch in dispatches)
        {
            Bind(dispatch.Pipeline,in step);
            _backend.Dispatch(_commands,in dispatch.Groups);
            Barrier();
        }
    }
    private void Bind(int kernel,in DeviceStep step)
    {
        _backend.SetPipeline(_commands,_pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands,new(_layouts[kernel],_bindings[kernel],
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step,1))));
    }
    private void Barrier()=>_backend.Barrier(_commands,new MemoryBarrier(PipelineSync.ComputeShading,PipelineSync.ComputeShading,
        ResourceAccess.UnorderedAccess,ResourceAccess.UnorderedAccess));
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceStep(float duration,float retention,float fraction,uint bodyIterations,uint color,uint substep,
        long number,double start,double end)
    {
        public float Duration=duration,Retention=retention,InitialErrorFraction=fraction;
        public uint BodyIterations=bodyIterations,Color=color,Substep=substep;
        public long Number=number;
        public double StartTime=start,EndTime=end;
    }
    private readonly struct SolverSubmission(DeviceAvbdSolver solver,PhysicsStepContext context,bool initialize,int iterations,bool buildGraph) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend=>solver._backend;
        public Device Device=>solver._device;
        public QueueCompletion Submit()=>solver.Record(in context,initialize,iterations,buildGraph);
    }
    public void Dispose() { foreach(Pipeline pipeline in _pipelines) pipeline.Dispose(); _commands.Dispose(); }
}
public readonly struct SolveDeviceAvbd(DeviceAvbdSolver solver) : IPhysicsStage
{
    private readonly DeviceInputTransfer _equationInputs=DeviceInputTransfer.Upload;
    internal SolveDeviceAvbd(DeviceAvbdSolver solver,DeviceInputTransfer equationInputs):this(solver)=>_equationInputs=equationInputs;
    public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>solver.ScheduleSolve(in context,_equationInputs,dependency);
}
public readonly struct InitializeDeviceAvbd(DeviceAvbdSolver solver) : IPhysicsStage
{
    private readonly DeviceInputTransfer _equationInputs=DeviceInputTransfer.Upload;
    internal InitializeDeviceAvbd(DeviceAvbdSolver solver,DeviceInputTransfer equationInputs):this(solver)=>_equationInputs=equationInputs;
    public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>solver.Schedule(in context,true,0,true,_equationInputs,dependency);
}
public readonly struct IterateDeviceAvbd(DeviceAvbdSolver solver,int iterations) : IPhysicsStage
{
    private readonly bool _buildGraph=true;
    internal IterateDeviceAvbd(DeviceAvbdSolver solver,int iterations,bool buildGraph):this(solver,iterations)=>_buildGraph=buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>solver.Schedule(in context,false,iterations,_buildGraph,dependency);
}
