using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct DeviceXpbdChainWork(int Count, IReadOnlyDictionary<string, ResourceBinding[]> Inputs);
public interface IDeviceXpbdChainInputs
{
    int WorkCount { get; }
    DeviceXpbdChainWork GetWork(int index);
    void AddAccesses(Scheduler scheduler, List<JobResourceAccess> accesses);
}

/// <summary>Solves independent entity-body chains with common prediction, typed projections and velocity reconstruction.</summary>
public sealed class DeviceXpbdChains<TInputs> : IPhysicsStage, IDisposable where TInputs : IDeviceXpbdChainInputs
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly TInputs _inputs;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _layout;
    private readonly CommandContext _commands;

    public DeviceXpbdChains(IGraphicsBackend backend, Device device, TInputs inputs, ComputePipelineDesc kernel)
    {
        _backend = backend; _device = device; _inputs = inputs;
        _pipeline = backend.CreateComputePipeline(device, kernel);
        _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Entity chain XPBD"));
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        return scheduler.Schedule(new SubmitWork(this, scheduler),
            [JobResourceAccess.Write(scheduler.GetResourceToken(_inputs))], dependency);
    }
    private readonly struct SubmitWork(DeviceXpbdChains<TInputs> solver, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(scheduler.GetResourceToken(solver._commands)) };
            solver._inputs.AddAccesses(scheduler, accesses);
            DevicePhysicsJobs.Schedule(scheduler, new Submission(solver), accesses.ToArray(), default);
        }
    }
    private QueueCompletion Record()
    {
        _backend.Begin(_commands, new(0, 0, 16));
        _backend.SetPipeline(_commands, _pipeline);
        for (int index = 0; index < _inputs.WorkCount; index++)
        {
            DeviceXpbdChainWork work = _inputs.GetWork(index);
            Record(_commands, in work);
        }
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    /// <summary>Records the same chain stage into a caller's command context under its declared input accesses.</summary>
    public void Record(CommandContext commands, in DeviceXpbdChainWork work)
    {
        _backend.SetPipeline(commands, _pipeline);
        ResourceBinding[] bindings = DeviceCollisionGrid.OrderBindings(_layout, work.Inputs);
        _backend.SetTransientParameterBindings(commands, new(_layout, bindings, []));
        _backend.Dispatch(commands, DevicePhysicsDispatch.CreateGroups((work.Count + 63) / 64));
        _backend.Barrier(commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    }
    private readonly struct Submission(DeviceXpbdChains<TInputs> solver) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => solver._backend;
        public Device Device => solver._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => solver.Record();
    }
    public void Dispose() { _commands.Dispose(); _pipeline.Dispose(); }
}
