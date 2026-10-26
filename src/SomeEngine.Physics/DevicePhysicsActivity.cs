using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceActivityKernels(ComputePipelineDesc ApplyInputs, ComputePipelineDesc ClearSignals,
    ComputePipelineDesc CollectBodies, ComputePipelineDesc ReadRows, ComputePipelineDesc WakeBodies,
    ComputePipelineDesc AdvanceQuiet, ComputePipelineDesc FinishBodies, ComputePipelineDesc? ReadBodyInputs = null);

/// <summary>Wake, quiet-time and completed activity results over the solver's original full device graph.</summary>
public sealed class DevicePhysicsActivity : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodyActivities _activities;
    private readonly DeviceConstraintGraph _graph;
    private readonly BodyActivitySettings _settings;
    private readonly int _threadsPerGroup;
    private readonly int _readBodyInputs;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly Buffer _signals, _readback, _previousRows, _previousParticipants;
    private readonly BufferUav _signalsView, _previousRowsView, _previousParticipantsView;
    private readonly CommandContext _commands;
    private readonly JobResourceAccess[] _extraAccesses;
    private bool _initialized;

    public DevicePhysicsActivity(IGraphicsBackend backend, Device device, DeviceBodyActivities activities,
        DeviceConstraintGraph graph, DeviceActivityKernels kernels, BodyActivitySettings settings,
        IReadOnlyDictionary<string, ResourceBinding[]> inputs, ReadOnlyMemory<JobResourceAccess> inputAccesses, int threadsPerGroup)
    {
        _backend = backend; _device = device; _activities = activities; _graph = graph; _settings = settings;
        _threadsPerGroup = threadsPerGroup; _extraAccesses = inputAccesses.ToArray();
        _signals = backend.CreateBuffer(device, new((ulong)activities.Bodies.Capacity * 4,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Activity island signals"), MemoryType.DeviceLocal);
        _signalsView = backend.CreateBufferUav(device, new(_signals, new(0, _signals.Info.Size), StructureStride: 4));
        _previousRows = backend.CreateBuffer(device, new((ulong)graph.ConstraintCapacity * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Activity constraint identities"), MemoryType.DeviceLocal);
        _previousParticipants = backend.CreateBuffer(device, new((ulong)graph.ConstraintCapacity * (ulong)graph.BodiesPerConstraint * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Activity previous participants"), MemoryType.DeviceLocal);
        _previousRowsView = backend.CreateBufferUav(device, new(_previousRows, new(0, _previousRows.Info.Size), StructureStride: 16));
        _previousParticipantsView = backend.CreateBufferUav(device, new(_previousParticipants, new(0, _previousParticipants.Info.Size), StructureStride: 16));
        _readback = backend.CreateBuffer(device, new(activities.States.Info.Size, BufferUsages.CopyDestination,
            "Completed body activity"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device body activity"));
        var descriptions = new List<ComputePipelineDesc> { kernels.ApplyInputs, kernels.ClearSignals, kernels.CollectBodies, kernels.ReadRows,
            kernels.WakeBodies, kernels.AdvanceQuiet, kernels.FinishBodies };
        _readBodyInputs = kernels.ReadBodyInputs.HasValue ? descriptions.Count : -1;
        if (kernels.ReadBodyInputs is { } readBodyInputs) descriptions.Add(readBodyInputs);
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>(); graph.AddBindings(bindings);
        bindings.Add("DeviceActivityIslandSignals", [ResourceBinding.WritableBuffer(_signalsView)]);
        bindings.Add("DeviceActivityPreviousRows", [ResourceBinding.WritableBuffer(_previousRowsView)]);
        bindings.Add("DeviceActivityPreviousParticipants", [ResourceBinding.WritableBuffer(_previousParticipantsView)]);
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        _bindings = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
    }

    public DeviceActivityStage UploadInputs() => new(this, 0);
    public DeviceActivityStage WakeConnected() => new(this, 1);
    public DeviceActivityStage Finish() => new(this, 2);
    public ReadDeviceActivityResults Readback(PhysicsEventBuffer events) => new(this, events);
    internal JobHandle Schedule(in PhysicsStepContext context, int phase, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var accesses = new List<JobResourceAccess>(_activities.GetWriteAccesses(scheduler));
        _graph.AddBuildAccesses(scheduler, accesses);
        accesses.Add(_activities.Bodies.GetWriteAccess(scheduler));
        accesses.AddRange(_extraAccesses);
        if (phase == 0) accesses.AddRange(_activities.SourceAccesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_signals), 0, (long)_signals.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_previousRows), 0, (long)_previousRows.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_previousParticipants), 0, (long)_previousParticipants.Info.Size));
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context, phase), accesses.ToArray(), dependency);
    }
    private QueueCompletion Record(in PhysicsStepContext context, int phase)
    {
        if (phase == 0) _activities.CaptureInputs(in context);
        if (phase == 1) _graph.BuildAndWait();
        var step = new ActivityStep((uint)_activities.Bodies.Capacity, (uint)_graph.ConstraintCapacity,
            (float)context.Duration, _settings, (uint)phase, (uint)context.Substep, context.Number, context.StartTime, context.EndTime);
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized)
        {
            _backend.Barrier(_commands, new BufferBarrier(_activities.States, _activities.States.InitialSync, PipelineSync.ComputeShading,
                _activities.States.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands, new BufferBarrier(_signals, _signals.InitialSync, PipelineSync.ComputeShading,
                _signals.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands, new BufferBarrier(_previousRows, _previousRows.InitialSync, PipelineSync.ComputeShading,
                _previousRows.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands, new BufferBarrier(_previousParticipants, _previousParticipants.InitialSync, PipelineSync.ComputeShading,
                _previousParticipants.InitialAccess, ResourceAccess.UnorderedAccess));
            int count = _graph.ConstraintCapacity * _graph.BodiesPerConstraint;
            ActivityStep initialize = step;
            initialize.Phase = 0;
            Dispatch(1, (count + _threadsPerGroup - 1) / _threadsPerGroup, in initialize);
        }
        int bodies = (_activities.Bodies.Capacity + _threadsPerGroup - 1) / _threadsPerGroup;
        if (phase == 0)
        {
            Dispatch(0, bodies, in step);
            if (_readBodyInputs >= 0) Dispatch(_readBodyInputs, bodies, in step);
        }
        else
        {
            Dispatch(1, bodies, in step);
            Dispatch(2, bodies, in step);
            Dispatch(3, (_graph.ConstraintCapacity + _threadsPerGroup - 1) / _threadsPerGroup, in step);
            if (phase == 1) Dispatch(4, bodies, in step);
            else { Dispatch(5, bodies, in step); Dispatch(6, bodies, in step); }
        }
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void Dispatch(int kernel, int groups, in ActivityStep step)
    {
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel],
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
        _backend.Dispatch(_commands, new((uint)groups, 1, 1));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivityStep(uint bodies, uint rows, float duration, BodyActivitySettings settings, uint phase, uint substep,
        long number, double start, double end)
    {
        public uint Bodies = bodies, Rows = rows;
        public float Duration = duration, SpeedThreshold = settings.SpeedThreshold;
        public float QuietDuration = settings.QuietDuration, LinearErrorThreshold = settings.LinearErrorThreshold, AngularErrorThreshold = settings.AngularErrorThreshold;
        public uint Phase = phase, Substep = substep, Padding;
        public long Number = number;
        public double StartTime = start, EndTime = end;
    }
    private readonly struct Submission(DevicePhysicsActivity activity, PhysicsStepContext context, int phase) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => activity._backend;
        public Device Device => activity._device;
        public QueueCompletion Submit() => activity.Record(in context, phase);
    }
    internal JobHandle ScheduleReadback(in PhysicsStepContext context, PhysicsEventBuffer events, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _activities.GetReadAccesses(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return scheduler.Schedule(new Copy(this, scheduler, context, events, accesses), accesses, dependency);
    }
    private readonly struct Copy(DevicePhysicsActivity activity, Scheduler scheduler, PhysicsStepContext context,
        PhysicsEventBuffer events, JobResourceAccess[] accesses) : IJob
    {
        public void Execute()
        {
            activity._backend.Begin(activity._commands, new(0, 0, 8));
            activity._backend.Barrier(activity._commands, new BufferBarrier(activity._activities.States,
                PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            activity._backend.CopyBuffer(activity._commands, new(activity._activities.States, 0, activity._readback, 0, activity._readback.Info.Size));
            activity._backend.Barrier(activity._commands, new BufferBarrier(activity._activities.States,
                PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            using RecordedCommands recorded = activity._backend.End(activity._commands);
            QueueCompletion complete = activity._backend.Submit(activity._backend.GetQueue(activity._device, QueueType.Compute), new([], [], [recorded], [], []));
            JobHandle ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, activity._backend, complete), accesses);
            JobResourceAccess[] writes = [JobResourceAccess.Write(scheduler.GetResourceToken(activity._activities)),
                JobResourceAccess.Read(scheduler.GetResourceToken(activity._readback), 0, (long)activity._readback.Info.Size),
                .. activity._activities.ResultAccesses, events.WriteAccess];
            scheduler.Schedule(new Apply(activity, context, events), writes, ready);
        }
    }
    private readonly struct Apply(DevicePhysicsActivity activity, PhysicsStepContext context, PhysicsEventBuffer events) : IJob
    {
        public void Execute()
        {
            using (MappedBuffer mapping = activity._backend.Map(activity._readback, MapType.Read, BufferRange.Whole))
            {
                mapping.Invalidate(mapping.Range);
                MemoryMarshal.Cast<byte, DeviceBodyActivityState>(mapping.Bytes).CopyTo(activity._activities.Results);
            }
            activity._activities.ApplyResults(in context, events);
            activity._backend.CollectCompleted(activity._device);
        }
    }
    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        _commands.Dispose(); _signalsView.Dispose(); _signals.Dispose(); _readback.Dispose();
        _previousRowsView.Dispose(); _previousParticipantsView.Dispose(); _previousRows.Dispose(); _previousParticipants.Dispose();
    }
}
public readonly struct DeviceActivityStage(DevicePhysicsActivity activity, int phase) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => activity.Schedule(in context, phase, dependency);
}
public readonly struct ReadDeviceActivityResults(DevicePhysicsActivity activity, PhysicsEventBuffer events) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => activity.ScheduleReadback(in context, events, dependency);
}
