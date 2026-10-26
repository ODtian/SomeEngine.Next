using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Animation.Pose;

public readonly record struct DevicePoseKernels(ComputePipelineDesc Advance, ComputePipelineDesc Sample,
    ComputePipelineDesc? Translate = null, ComputePipelineDesc? TwoBone = null,
    ComputePipelineDesc? Aim = null, ComputePipelineDesc? Stride = null, ComputePipelineDesc? Xpbd = null);
public readonly record struct DevicePoseCapacity(int Targets, int Bones, int BlendStates, int BlendLocals,
    int Controls, int DefinitionBytes, int Goals = 1, int Operations = 1);

/// <summary>Game-world source inputs and their device model transforms, before entity physics and rendering extraction.</summary>
public sealed partial class DevicePoseSources : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly DevicePosePhysics? _physics;
    private readonly QueryHandle _targets;
    private readonly QueryReadResources _resources = new();
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly Entity[] _entities;
    private int _entityCount;
    private readonly Dictionary<Entity, SourceRow> _sources = [];
    private readonly List<SourceRow> _order = [];
    private readonly List<IDisposable> _objects = [];
    private readonly List<UploadBuffer> _uploads = [];
    private readonly List<Buffer> _states = [];
    private readonly UploadBuffer _controls, _inputs, _roots, _offsets, _work, _goals, _movements;
    private UploadBuffer? _definitions;
    private readonly UploadBuffer? _physicsFrames;
    private readonly Buffer _playback, _historyCurrent, _historyPrevious, _readback;
    private readonly Dictionary<string, ResourceBinding[]> _bindings = [];
    private readonly List<BufferSrv> _workViews = [];
    private readonly List<ResourceBinding[]> _sampleBindings = [];
    private readonly List<RigOperation> _rigOperations = [];
    private readonly List<int> _workCounts = [];
    private BufferSrv? _controlView;
    private ResourceBinding[] _advanceBindings = [];
    private readonly List<Pipeline> _pipelines;
    private readonly int _translateKernel;
    private readonly int _twoBoneKernel, _aimKernel, _strideKernel, _xpbdKernel;
    private readonly List<VariableLayoutReflection> _layouts;
    private readonly CommandContext _commands;
    private readonly Matrix4x4[] _models;
    private int _boneCount;

    public DevicePoseSources(IGraphicsBackend backend, Device device, World world, QueryHandle targets,
        DevicePoseKernels kernels, DevicePoseCapacity capacity, DevicePosePhysics? physics = null)
    {
        _backend = backend; _device = device; _world = world; _physics = physics;
        _entities = new Entity[capacity.Targets]; _models = new Matrix4x4[capacity.Bones];
        QueryDefinition definition = world.GetQueryDefinition(targets).AsFilter()
            .WithAccess(new QueryDefinitionBuilder().Read<GpuPoseTarget>().Read<GpuPoseSourceState>()
                .OptionalBuffer<GpuPoseGoal>(QueryAccess.Read).Build());
        _targets = world.Query(definition);
        _resources.Add<GpuPoseTarget, GpuPoseTargetResources>();
        _resources.Add<GpuPoseSourceState, GpuPoseSourceStateResources>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, _resources);
        _controls = CreateUpload(capacity.Targets * 64, 64, "GpuPoseControls");
        _inputs = CreateUpload(capacity.Controls * 4, 4, "GpuPoseInputs");
        if (capacity.DefinitionBytes > 0) _definitions = CreateUpload(capacity.DefinitionBytes, 0, "GpuPoseClips");
        _roots = CreateUpload(capacity.Targets * 32, 32, "GpuPoseRoots");
        _offsets = CreateUpload(capacity.Targets * 16, 16, "GpuPoseOffsets");
        _cpuModels = CreateUpload(capacity.Bones * 64, 64, "GpuPoseCpuModels");
        _work = CreateUpload((capacity.Bones + capacity.Operations) * 16, 16, "GpuPoseWork");
        _goals = CreateUpload(capacity.Goals * 48, 48, "GpuPoseGoals");
        _movements = CreateUpload(capacity.Targets * 48, 48, "GpuPoseMovements");
        if (physics is not null)
        {
            _physicsFrames = CreateUpload(capacity.Targets * 160, 160, "GpuPosePhysicsFrames");
            physics.AddBindings(_bindings);
        }
        _playback = CreateState(capacity.Targets * 64, 64, "GpuPosePlaybackStates");
        CreateState(capacity.BlendStates * 32, 32, "GpuBlendStates");
        CreateState(capacity.BlendLocals * 48, 48, "GpuBlendLocals");
        Models = CreateState(capacity.Bones * 64, 64, "GpuPoseModels");
        _historyCurrent = CreateState(capacity.Bones * 128, 64, "GpuPoseHistoryCurrent");
        _historyPrevious = CreateState(capacity.Bones * 128, 64, "GpuPoseHistoryPrevious");
        _readback = Keep(backend.CreateBuffer(device, new((ulong)capacity.Bones * 64, BufferUsages.CopyDestination), MemoryType.Readback));
        _commands = Keep(backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Game pose sources")));
        var descriptions = new List<ComputePipelineDesc> { kernels.Advance, kernels.Sample };
        _translateKernel = kernels.Translate.HasValue ? descriptions.Count : -1;
        if (kernels.Translate is { } translate) descriptions.Add(translate);
        _twoBoneKernel = kernels.TwoBone.HasValue ? descriptions.Count : -1;
        if (kernels.TwoBone is { } twoBone) descriptions.Add(twoBone);
        _aimKernel = kernels.Aim.HasValue ? descriptions.Count : -1;
        if (kernels.Aim is { } aim) descriptions.Add(aim);
        _strideKernel = kernels.Stride.HasValue ? descriptions.Count : -1;
        if (kernels.Stride is { } stride) descriptions.Add(stride);
        _xpbdKernel = kernels.Xpbd.HasValue ? descriptions.Count : -1;
        if (kernels.Xpbd is { } xpbd) descriptions.Add(xpbd);
        _pipelines = descriptions.Select(kernel => Keep(backend.CreateComputePipeline(device, kernel))).ToList();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToList();
        CreateOutputs(capacity);
    }

    public Buffer Models { get; }
    public EvaluateDevicePoseSources Evaluate() => new(this);
    public ReadDevicePoseSources Readback() => new(this);
    public PoseRange GetModelRange(Entity target) => _sources[target].Range;
    public ReadOnlySpan<Matrix4x4> ReadModels(Entity target)
    { PoseRange range = GetModelRange(target); return _models.AsSpan(range.Start, range.Count); }
    public JobResourceAccess[] GetResultReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(this)), JobResourceAccess.Read(scheduler, _models)];
    public JobResourceAccess GetModelReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(Models), 0, (long)Models.Info.Size);

    private T Keep<T>(T value) where T : IDisposable { _objects.Add(value); return value; }
    private UploadBuffer CreateUpload(int bytes, uint stride, string name)
    {
        Buffer buffer = Keep(_backend.CreateBuffer(_device, new((ulong)bytes, BufferUsages.ShaderRead | BufferUsages.CopySource, name), MemoryType.Upload));
        BufferSrv view = Keep(_backend.CreateBufferSrv(_device, new(buffer, new(0, buffer.Info.Size), StructureStride: stride)));
        var upload = new UploadBuffer(buffer, view, _backend.Map(buffer, MapType.Write, BufferRange.Whole));
        _uploads.Add(upload); _bindings.Add(name, [ResourceBinding.ReadOnlyBuffer(view)]);
        return upload;
    }
    private void WriteDefinitions(ReadOnlySpan<byte> bytes)
    {
        if (_definitions is null || _definitions.Buffer.Info.Size < (ulong)bytes.Length)
        {
            if (_definitions is { } previous)
            {
                _uploads.Remove(previous); _objects.Remove(previous.View); _objects.Remove(previous.Buffer);
                previous.Mapping.Dispose(); previous.View.Dispose(); previous.Buffer.Dispose();
                _bindings.Remove("GpuPoseClips");
            }
            _definitions = CreateUpload(bytes.Length, 0, "GpuPoseClips");
        }
        bytes.CopyTo(_definitions.Mapping.Bytes);
        _definitions.Mapping.Flush(new(0, (ulong)bytes.Length));
    }
    private Buffer CreateState(int bytes, uint stride, string name)
    {
        Buffer buffer = Keep(_backend.CreateBuffer(_device, new((ulong)bytes,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination, name), MemoryType.DeviceLocal));
        BufferUav view = Keep(_backend.CreateBufferUav(_device, new(buffer, new(0, buffer.Info.Size), StructureStride: stride)));
        _states.Add(buffer); _bindings.Add(name, [ResourceBinding.WritableBuffer(view)]);
        return buffer;
    }
    private JobResourceAccess[] GetDeviceAccesses(Scheduler scheduler, bool copy)
    {
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands)) };
        if (copy)
        {
            accesses.Add(GetModelReadAccess(scheduler));
            accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size));
        }
        else
        {
            foreach (UploadBuffer upload in _uploads)
                accesses.Add(JobResourceAccess.Read(scheduler.GetResourceToken(upload.Buffer), 0, (long)upload.Buffer.Info.Size));
            foreach (Buffer state in _states)
                accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(state), 0, (long)state.Info.Size));
            if (_physics is not null) accesses.AddRange(_physics.GetDeviceAccesses(scheduler));
        }
        return [.. accesses];
    }
    private QueueCompletion Submit(bool copy, int targetKernel = -1, int mapKernel = -1)
    {
        _backend.Begin(_commands, new(0, 0, 8));
        if (copy)
        {
            _backend.Barrier(_commands, new BufferBarrier(Models, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            if (_boneCount > 0) _backend.CopyBuffer(_commands, new(Models, 0, _readback, 0, (ulong)_boneCount * 64));
            _backend.Barrier(_commands, new BufferBarrier(Models, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        }
        else if (mapKernel >= 0)
        {
            foreach (RigOperation operation in _rigOperations)
                if (operation.Kernel == _xpbdKernel) DispatchBodyOperation(mapKernel, operation);
                else if (operation.AfterPhysics) Dispatch(operation.Kernel, operation.Bindings, operation.Count);
        }
        else if (_order.Count > 0)
        {
            _physics?.RecordFirstUse(_commands);
            CopyCpuModels();
            Dispatch(0, _advanceBindings, _order.Count);
            for (int level = 0; level < _sampleBindings.Count; level++) Dispatch(1, _sampleBindings[level], _workCounts[level]);
            foreach (RigOperation operation in _rigOperations)
                if (targetKernel < 0) Dispatch(operation.Kernel, operation.Bindings, operation.Count);
                else if (operation.Kernel == _xpbdKernel) DispatchBodyOperation(targetKernel, operation);
                else if (!operation.AfterPhysics) Dispatch(operation.Kernel, operation.Bindings, operation.Count);
        }
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void DispatchBodyOperation(int kernel, RigOperation operation)
    {
        _bindings["GpuPoseWork"] = [ResourceBinding.ReadOnlyBuffer(operation.Work)];
        Dispatch(kernel, OrderBindings(_layouts[kernel], _bindings), operation.Count);
    }
    private void Dispatch(int pipeline, ResourceBinding[] bindings, int count)
    {
        _backend.SetPipeline(_commands, _pipelines[pipeline]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[pipeline], bindings, []));
        _backend.Dispatch(_commands, new((uint)(count + 63) / 64, 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    }
    internal JobHandle ScheduleReadback(Scheduler scheduler, JobHandle dependency)
    {
        JobHandle copy = DevicePhysicsJobs.Schedule(scheduler, new Submission(this, true), GetDeviceAccesses(scheduler, true), dependency);
        JobResourceAccess[] accesses = [JobResourceAccess.Write(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler, _models), JobResourceAccess.Read(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size)];
        return scheduler.Schedule(new MapModels(this), accesses, copy);
    }
    private readonly struct MapModels(DevicePoseSources sources) : IJob
    {
        public void Execute()
        {
            if (sources._boneCount == 0) return;
            using MappedBuffer mapping = sources._backend.Map(sources._readback, MapType.Read, new(0, (ulong)sources._boneCount * 64));
            mapping.Invalidate(mapping.Range);
            MemoryMarshal.Cast<byte, Matrix4x4>(mapping.Bytes).CopyTo(sources._models);
        }
    }
    private readonly struct Submission(DevicePoseSources sources, bool copy) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => sources._backend;
        public Device Device => sources._device;
        public QueueCompletion Submit() => sources.Submit(copy);
    }
    private static ResourceBinding[] OrderBindings(VariableLayoutReflection root, Dictionary<string, ResourceBinding[]> bindings)
    {
        TypeLayoutReflection layout = root.TypeLayout.UnwrapArray();
        if (layout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock) layout = layout.ElementTypeLayout.UnwrapArray();
        var values = new List<ResourceBinding>();
        for (nint range = 0; range < layout.BindingRangeCount; range++)
        {
            nint ranges = layout.GetBindingRangeDescriptorRangeCount(range);
            if (ranges == 0) continue;
            string name = layout.GetBindingRangeLeafVariable(range).Name;
            nint set = layout.GetBindingRangeDescriptorSetIndex(range), first = layout.GetBindingRangeFirstDescriptorRangeIndex(range);
            int position = 0;
            for (nint relative = 0; relative < ranges; relative++)
                for (nint element = 0; element < layout.GetDescriptorSetDescriptorRangeDescriptorCount(set, first + relative); element++)
                    values.Add(bindings[name][position++]);
        }
        return [.. values];
    }
    public void Dispose()
    {
        _world.ReleaseQuery(_targets);
        _controlView?.Dispose();
        foreach (BufferSrv view in _workViews) view.Dispose();
        foreach (UploadBuffer upload in _uploads) upload.Mapping.Dispose();
        for (int index = _objects.Count - 1; index >= 0; index--) _objects[index].Dispose();
    }
    private sealed record UploadBuffer(Buffer Buffer, BufferSrv View, MappedBuffer Mapping);
}

public readonly struct EvaluateDevicePoseSources(DevicePoseSources sources) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => sources.ScheduleEvaluate(in context, dependency);
}
public readonly struct ReadDevicePoseSources(DevicePoseSources sources) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        sources.ScheduleReadback(WorldJobs.GetScheduler(context.World), dependency);
}
