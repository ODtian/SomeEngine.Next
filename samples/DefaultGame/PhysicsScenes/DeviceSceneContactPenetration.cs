using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render;
using Buffer = SomeEngine.Graphics.Buffer;

namespace DefaultGame;

internal sealed class DeviceSceneContactPenetration : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceConstraintData<DeviceContactConstraint> _contacts;
    private readonly IDeviceConstraintSelection _selection;
    private readonly JobResourceAccess[] _inputs;
    private readonly Buffer _maximum, _readback;
    private readonly BufferUav _view;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private bool _initialized;

    internal DeviceSceneContactPenetration(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceConstraintData<DeviceContactConstraint> contacts, IDeviceConstraintSelection selection, DeviceConstraintGraph graph,
        (ComputePipelineDesc Clear, ComputePipelineDesc Read) kernels,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, JobResourceAccess[] accesses)
    {
        _backend = backend; _device = device; _bodies = bodies; _contacts = contacts;
        _selection = selection; _inputs = accesses;
        _maximum = backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource,
            "Maximum completed contact penetration"), MemoryType.DeviceLocal);
        _readback = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
        _view = backend.CreateBufferUav(device, new(_maximum, new(0, 4), StructureStride: 4));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact gap observation"));
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["SceneContactPenetration"] = [ResourceBinding.WritableBuffer(_view)],
        };
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        graph.AddBindings(bindings);
        ComputePipelineDesc[] descriptions = [kernels.Clear, kernels.Read];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        _bindings = _layouts.Select(layout => ShaderResourceLayout.Resolve<ResourceBinding>(layout,
            (name, _) => bindings[name][0])).ToArray();
    }

    internal float Value { get; private set; }
    internal ReadSceneContactPenetration Readback() => new(this);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(_inputs) { _bodies.GetReadAccess(scheduler) };
        accesses.AddRange(_contacts.GetReadAccesses(scheduler));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_maximum), 0, 4));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, 4));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        _selection.AddAccesses(scheduler, accesses);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, this, accesses.ToArray(), dependency);
        return scheduler.Schedule(new Read(this), new[]
        {
            JobResourceAccess.Read(scheduler.GetResourceToken(_readback), 0, 4),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
        }, copied);
    }

    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _selection.Capture();
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized)
            _backend.Barrier(_commands, new BufferBarrier(_maximum, _maximum.InitialSync, PipelineSync.ComputeShading,
                _maximum.InitialAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipelines[0]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[0], _bindings[0], []));
        _backend.Dispatch(_commands, new(1, 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipelines[1]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[1], _bindings[1], []));
        _backend.Dispatch(_commands, new((uint)(_contacts.Capacity + 63) / 64, 1, 1));
        _backend.Barrier(_commands, new BufferBarrier(_maximum, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_maximum, 0, _readback, 0, 4));
        _backend.Barrier(_commands, new BufferBarrier(_maximum, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands commands = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [commands], [], []));
    }

    private readonly struct Read(DeviceSceneContactPenetration result) : IJob
    {
        public void Execute()
        {
            using MappedBuffer mapping = result._backend.Map(result._readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            result.Value = MemoryMarshal.Read<float>(mapping.Bytes);
        }
    }

    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _view.Dispose(); _maximum.Dispose(); _readback.Dispose();
    }
}

internal readonly struct ReadSceneContactPenetration(DeviceSceneContactPenetration value) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => value.Schedule(in context, dependency);
}
