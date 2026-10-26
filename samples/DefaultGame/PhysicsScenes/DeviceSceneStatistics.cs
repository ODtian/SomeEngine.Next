using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render;
using Buffer = SomeEngine.Graphics.Buffer;

namespace DefaultGame;

internal sealed class DeviceSceneStatistics : IDisposable, IDevicePhysicsSubmission
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodies _bodies;
    private readonly DeviceConstraintData<DeviceContactConstraint> _contacts;
    private readonly IDeviceConstraintSelection _selection;
    private readonly JobResourceAccess[] _inputs;
    private readonly Buffer _values, _islandSizes, _readback, _parents;
    private readonly BufferUav _view, _islandSizesView;
    private readonly CommandContext _commands;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private bool _initialized;

    internal DeviceSceneStatistics(IGraphicsBackend backend, Device device, DeviceBodies bodies,
        DeviceConstraintData<DeviceContactConstraint> contacts, IDeviceConstraintSelection selection, DeviceConstraintGraph graph,
        (ComputePipelineDesc Clear, ComputePipelineDesc Bodies, ComputePipelineDesc Contacts) kernels,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, JobResourceAccess[] accesses)
    {
        _backend = backend; _device = device; _bodies = bodies; _contacts = contacts;
        _selection = selection; _inputs = accesses;
        _values = backend.CreateBuffer(device, new(16, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource,
            "Completed scene statistics"), MemoryType.DeviceLocal);
        _islandSizes = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 4,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Completed island body counts"), MemoryType.DeviceLocal);
        _readback = backend.CreateBuffer(device, new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        _view = backend.CreateBufferUav(device, new(_values, new(0, 16), StructureStride: 4));
        _islandSizesView = backend.CreateBufferUav(device, new(_islandSizes, new(0, _islandSizes.Info.Size), StructureStride: 4));
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Completed scene statistics"));
        var bindings = new Dictionary<string, ResourceBinding[]>
        {
            ["SceneStatistics"] = [ResourceBinding.WritableBuffer(_view)],
            ["SceneIslandSizes"] = [ResourceBinding.WritableBuffer(_islandSizesView)],
        };
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        graph.AddBindings(bindings);
        _parents = ((BufferUav)bindings["ConstraintGraphParents"][0].Value!).Resource;
        ComputePipelineDesc[] descriptions = [kernels.Clear, kernels.Bodies, kernels.Contacts];
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        _bindings = _layouts.Select(layout => ShaderResourceLayout.Resolve<ResourceBinding>(layout,
            (name, _) => bindings[name][0])).ToArray();
    }

    internal float MaximumPenetration { get; private set; }
    internal int AwakeBodies { get; private set; }
    internal int ContactPoints { get; private set; }
    internal int LargestIslandBodies { get; private set; }
    internal ReadDeviceSceneStatistics Readback() => new(this);
    IGraphicsBackend IDevicePhysicsSubmission.Backend => _backend;
    Device IDevicePhysicsSubmission.Device => _device;

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(_inputs) { _bodies.GetReadAccess(scheduler) };
        accesses.AddRange(_contacts.GetReadAccesses(scheduler));
        accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(_parents), 0, (long)_parents.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_values), 0, 16));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_islandSizes), 0, (long)_islandSizes.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, 16));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        _selection.AddAccesses(scheduler, accesses);
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, this, accesses.ToArray(), dependency);
        return scheduler.Schedule(new Read(this), new[]
        {
            JobResourceAccess.Read(scheduler.GetResourceToken(_readback), 0, 16),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
        }, copied);
    }

    QueueCompletion IDevicePhysicsSubmission.Submit()
    {
        _selection.Capture();
        _backend.Begin(_commands, new(0, 0, 8));
        if (!_initialized)
        {
            _backend.Barrier(_commands, new BufferBarrier(_values, _values.InitialSync, PipelineSync.ComputeShading,
                _values.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands, new BufferBarrier(_islandSizes, _islandSizes.InitialSync, PipelineSync.ComputeShading,
                _islandSizes.InitialAccess, ResourceAccess.UnorderedAccess));
        }
        _backend.SetPipeline(_commands, _pipelines[0]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[0], _bindings[0], []));
        _backend.Dispatch(_commands, new((uint)(_bodies.Capacity + 63) / 64, 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        _backend.SetPipeline(_commands, _pipelines[1]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[1], _bindings[1], []));
        _backend.Dispatch(_commands, new((uint)(_bodies.Capacity + 63) / 64, 1, 1));
        _backend.SetPipeline(_commands, _pipelines[2]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[2], _bindings[2], []));
        _backend.Dispatch(_commands, new((uint)(_contacts.Capacity + 63) / 64, 1, 1));
        _backend.Barrier(_commands, new BufferBarrier(_values, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(_values, 0, _readback, 0, 16));
        _backend.Barrier(_commands, new BufferBarrier(_values, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands commands = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [commands], [], []));
    }

    private readonly struct Read(DeviceSceneStatistics result) : IJob
    {
        public void Execute()
        {
            using MappedBuffer mapping = result._backend.Map(result._readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            result.MaximumPenetration = BitConverter.UInt32BitsToSingle(values[0]);
            result.AwakeBodies = (int)values[1];
            result.ContactPoints = (int)values[2];
            result.LargestIslandBodies = (int)values[3];
        }
    }

    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _view.Dispose(); _islandSizesView.Dispose();
        _values.Dispose(); _islandSizes.Dispose(); _readback.Dispose();
    }
}

internal readonly struct ReadDeviceSceneStatistics(DeviceSceneStatistics value) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => value.Schedule(in context, dependency);
}
