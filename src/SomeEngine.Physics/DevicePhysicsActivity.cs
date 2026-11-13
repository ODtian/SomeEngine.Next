using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Diagnostics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceActivityKernels(ComputePipelineDesc ApplyInputs, ComputePipelineDesc ClearSignals,
    ComputePipelineDesc CollectBodies, ComputePipelineDesc ReadRows, ComputePipelineDesc WakeBodies,
    ComputePipelineDesc AdvanceQuiet, ComputePipelineDesc FinishBodies, ComputePipelineDesc? ReadBodyInputs = null, ComputePipelineDesc? CollectResults = null,
    ComputePipelineDesc? RemovedContacts = null);

/// <summary>Wake, quiet-time and completed activity results over the solver's original full device graph.</summary>
public sealed partial class DevicePhysicsActivity : IDisposable
{
    private static readonly TraceSite CopyCommandsTrace = new("physics", "Activity.CopyCommands");
    private static readonly TraceSite MapResultsTrace = new("physics", "Activity.MapResults");
    private static readonly TraceSite ApplyResultsTrace = new("physics", "Activity.ApplyResults");
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly DeviceBodyActivities _activities;
    private readonly DeviceConstraintGraph _graph;
    private readonly BodyActivitySettings _settings;
    private readonly int _threadsPerGroup;
    private readonly int _readBodyInputs, _removedContacts;
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private ResourceBinding[][] _bindings;
    private readonly ResourceBinding[][][][] _historyBindings;
    private int _historyBank;
    private ulong _matchingNumber;
    private readonly bool[] _historyReadOnly = new bool[2];
    private readonly DeviceContactColumns? _firstContacts;
    private readonly Buffer _signals, _readback;
    private readonly Buffer[] _previousRows = new Buffer[2], _previousParticipants = new Buffer[2];
    private readonly BufferUav _signalsView;
    private readonly BufferUav[] _previousRowsView = new BufferUav[2], _previousParticipantsView = new BufferUav[2];
    private readonly BufferSrv[] _rowReads = new BufferSrv[2], _participantReads = new BufferSrv[2];
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
        var rowsDescription = new BufferDesc((ulong)graph.ConstraintCapacity * 20,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Activity constraint identities");
        var participantsDescription = new BufferDesc((ulong)graph.ConstraintCapacity * (ulong)graph.BodiesPerConstraint * 16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Activity previous participants");
        for (int bank = 0; bank < 2; bank++)
        {
            _previousRows[bank] = graph.Memory is { } memory ? memory.CreateBuffer(rowsDescription, 20, graph.ContactStart)
                : backend.CreateBuffer(device, rowsDescription);
            _previousParticipants[bank] = graph.Memory is { } contactMemory ? contactMemory.CreateBuffer(participantsDescription,
                (uint)graph.BodiesPerConstraint * 16, graph.ContactStart) : backend.CreateBuffer(device, participantsDescription);
            _previousRowsView[bank] = backend.CreateBufferUav(device, new(_previousRows[bank], BufferRange.Whole, StructureStride: 20));
            _previousParticipantsView[bank] = backend.CreateBufferUav(device, new(_previousParticipants[bank], BufferRange.Whole, StructureStride: 16));
            _rowReads[bank] = backend.CreateBufferSrv(device, new(_previousRows[bank], BufferRange.Whole, StructureStride: 20));
            _participantReads[bank] = backend.CreateBufferSrv(device, new(_previousParticipants[bank], BufferRange.Whole, StructureStride: 16));
        }
        _readback = backend.CreateBuffer(device, new(kernels.CollectResults.HasValue ? 4 + (ulong)activities.Bodies.Capacity * 36 : activities.States.Info.Size, BufferUsages.CopyDestination,
            "Completed body activity"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device body activity"));
        var descriptions = new List<ComputePipelineDesc> { kernels.ApplyInputs, kernels.ClearSignals, kernels.CollectBodies, kernels.ReadRows,
            kernels.WakeBodies, kernels.AdvanceQuiet, kernels.FinishBodies };
        _readBodyInputs = kernels.ReadBodyInputs.HasValue ? descriptions.Count : -1;
        if (kernels.ReadBodyInputs is { } readBodyInputs) descriptions.Add(readBodyInputs);
        _removedContacts = graph.Contacts is null ? -1 : descriptions.Count;
        if (graph.Contacts is not null) descriptions.Add(kernels.RemovedContacts!.Value);
        CreateResultBuffers(kernels, descriptions);
        _pipelines = descriptions.Select(kernel => backend.CreateComputePipeline(device, kernel)).ToArray();
        _layouts = descriptions.Select(kernel => kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null).ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>(); graph.AddBindings(bindings);
        bindings.Add("DeviceActivityIslandSignals", [ResourceBinding.WritableBuffer(_signalsView)]);
        AddResultBindings(bindings);
        foreach (var input in inputs) bindings.Add(input.Key, input.Value);
        _firstContacts = graph.Contacts?.Current;
        _historyBindings = new ResourceBinding[2][][][];
        for (int history = 0; history < 2; history++)
        {
            bindings["DeviceActivityPreviousRows"] = [ResourceBinding.WritableBuffer(_previousRowsView[history])];
            bindings["DeviceActivityPreviousParticipants"] = [ResourceBinding.WritableBuffer(_previousParticipantsView[history])];
            bindings["DeviceActivityRowsBefore"] = [ResourceBinding.ReadOnlyBuffer(_rowReads[1 - history])];
            bindings["DeviceActivityParticipantsBefore"] = [ResourceBinding.ReadOnlyBuffer(_participantReads[1 - history])];
            graph.Contacts?.AddPreviousPointsBinding(bindings);
            graph.Contacts?.AddRemovedPointsBinding(bindings);
            graph.Contacts?.Current.AddBindings(bindings);
            ResourceBinding[][] first = _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray();
            if (graph.Contacts is { } contacts)
            {
                contacts.Previous.AddBindings(bindings);
                _historyBindings[history] = [first, _layouts.Select(layout => DeviceCollisionGrid.OrderBindings(layout, bindings)).ToArray()];
            }
            else _historyBindings[history] = [first];
        }
        _bindings = _historyBindings[0][0];
    }

    internal IReadOnlyList<object> RecordingInputs => _graph.RecordingInputs;
    internal object InputData => _activities;
    public DeviceActivityStage UploadInputs() => new(this, 0);
    public DeviceActivityStage WakeConnected() => new(this, 1);
    public DeviceActivityStage WakeConnected(bool buildGraph) => new(this, 1, buildGraph);
    public DeviceActivityStage Finish() => new(this, 2);
    public ReadDeviceActivityResults Readback(PhysicsEventBuffer events) => new(this, events);
    internal JobHandle Schedule(in PhysicsStepContext context, int phase, bool buildGraph, JobHandle dependency)
    {
        if (phase == 0) dependency = _activities.ScheduleCapture(in context, dependency);
        return ScheduleDevice(in context, phase, buildGraph, dependency);
    }
    internal JobHandle ScheduleInputs(in PhysicsStepContext context, JobHandle dependency) =>
        _activities.ScheduleCapture(in context, dependency);
    internal JobHandle ScheduleDevice(in PhysicsStepContext context, int phase, bool buildGraph, JobHandle dependency)
    {
        Scheduler scheduler = context.World.Execution!.Scheduler!;
        var accesses = new List<JobResourceAccess>(_activities.GetWriteAccesses(scheduler));
        _graph.AddBuildAccesses(scheduler, accesses);
        accesses.Add(_activities.Bodies.GetWriteAccess(scheduler));
        accesses.AddRange(_extraAccesses);
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_signals), 0, (long)_signals.Info.Size));
        DeviceContactManifolds? manifolds = _graph.Contacts;
        DeviceContactColumns? contacts = manifolds?.Current;
        bool remapped = phase == 1 && manifolds is not null && manifolds.MatchingNumber != _matchingNumber;
        int history = phase == 1 ? 1 - _historyBank : _historyBank;
        if (phase == 1) { _historyBank = history; _matchingNumber = manifolds?.MatchingNumber ?? 0; }
        for (int bank = 0; bank < 2; bank++)
        {
            bool write = phase != 1 || bank == history;
            accesses.Add(write ? JobResourceAccess.Write(scheduler.GetResourceToken(_previousRows[bank]), 0, (long)_previousRows[bank].Info.Size)
                : JobResourceAccess.Read(scheduler.GetResourceToken(_previousRows[bank]), 0, (long)_previousRows[bank].Info.Size));
            accesses.Add(write ? JobResourceAccess.Write(scheduler.GetResourceToken(_previousParticipants[bank]), 0, (long)_previousParticipants[bank].Info.Size)
                : JobResourceAccess.Read(scheduler.GetResourceToken(_previousParticipants[bank]), 0, (long)_previousParticipants[bank].Info.Size));
        }
        if (manifolds is not null)
        {
            accesses.Add(manifolds.GetPreviousPointsReadAccess(scheduler));
            accesses.Add(manifolds.GetRemovedPointsReadAccess(scheduler));
        }
        accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(_commands)));
        return DevicePhysicsJobs.Schedule(scheduler, new Submission(this, context, phase, buildGraph, contacts, history, remapped), accesses.ToArray(), dependency);
    }

    private async ValueTask<QueueCompletion> RecordAsync(PhysicsStepContext context, int phase, bool buildGraph, DeviceContactColumns? contacts,
        int history, bool remapped)
    {
        int contactBank = contacts is null || ReferenceEquals(contacts, _firstContacts) ? 0 : 1;
        _bindings = _historyBindings[history][contactBank];
        if (phase == 1 && buildGraph) await _graph.BuildAsync(context, contacts).ConfigureAwait(false);
        _graph.Memory?.EnsureRows(1);
        var step = new ActivityStep((uint)_activities.Bodies.Capacity, (uint)_graph.RowCount,
            (float)context.Duration, _settings, (uint)phase, (uint)context.Substep, context.Number, context.StartTime, context.EndTime) { Flags = remapped ? ActivityDataFlags.PreviousPointMap : 0 };
        _backend.Begin(_commands, new(0, 0, 16));
        if (!_initialized)
        {
            _backend.Barrier(_commands, new BufferBarrier(_activities.States, _activities.States.InitialSync, PipelineSync.ComputeShading,
                _activities.States.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.Barrier(_commands, new BufferBarrier(_signals, _signals.InitialSync, PipelineSync.ComputeShading,
                _signals.InitialAccess, ResourceAccess.UnorderedAccess));
            for (int bank = 0; bank < 2; bank++)
            {
                if (_graph.Memory?.Contains(_previousRows[bank]) != true)
                    _backend.Barrier(_commands, new BufferBarrier(_previousRows[bank], _previousRows[bank].InitialSync,
                        PipelineSync.ComputeShading, _previousRows[bank].InitialAccess, ResourceAccess.UnorderedAccess));
                if (_graph.Memory?.Contains(_previousParticipants[bank]) != true)
                    _backend.Barrier(_commands, new BufferBarrier(_previousParticipants[bank], _previousParticipants[bank].InitialSync,
                        PipelineSync.ComputeShading, _previousParticipants[bank].InitialAccess, ResourceAccess.UnorderedAccess));
                _bindings = _historyBindings[bank][contactBank];
                ActivityStep initialize = step; initialize.Phase = 0;
                Dispatch(1, (_graph.RowCount + _threadsPerGroup - 1) / _threadsPerGroup, in initialize);
            }
            _bindings = _historyBindings[history][contactBank];
        }
        if (phase == 1)
        {
            SetHistoryRead(1 - history, true); SetHistoryRead(history, false);
        }
        if (phase != 0)
        {
            _graph.Contacts?.MatchValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
            _graph.Contacts?.RemovedValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
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
            Dispatch(3, (_graph.RowCount + _threadsPerGroup - 1) / _threadsPerGroup, in step);
            if (phase == 1)
            {
                if (remapped) Dispatch(_removedContacts, (_graph.Contacts!.Capacity.Points + _threadsPerGroup - 1) / _threadsPerGroup, in step);
                Dispatch(4, bodies, in step);
            }
            else { Dispatch(5, bodies, in step); Dispatch(6, bodies, in step); }
        }
        if (phase != 0)
        {
            _graph.Contacts?.MatchValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            _graph.Contacts?.RemovedValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        }
        using RecordedCommands recorded = _backend.End(_commands);
        _initialized = true;
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private void SetHistoryRead(int bank, bool read)
    {
        if (_historyReadOnly[bank] == read) return;
        ResourceAccess before = read ? ResourceAccess.UnorderedAccess : ResourceAccess.ShaderResource;
        ResourceAccess after = read ? ResourceAccess.ShaderResource : ResourceAccess.UnorderedAccess;
        _backend.Barrier(_commands, new BufferBarrier(_previousRows[bank], PipelineSync.ComputeShading, PipelineSync.ComputeShading, before, after));
        _backend.Barrier(_commands, new BufferBarrier(_previousParticipants[bank], PipelineSync.ComputeShading, PipelineSync.ComputeShading, before, after));
        _historyReadOnly[bank] = read;
    }
    private void Dispatch(int kernel, int groups, in ActivityStep step)
    {
        _backend.SetPipeline(_commands, _pipelines[kernel]);
        _backend.SetTransientParameterBindings(_commands, new(_layouts[kernel], _bindings[kernel],
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in step, 1))));
        _backend.Dispatch(_commands, DevicePhysicsDispatch.CreateGroups(groups));
        _backend.Barrier(_commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
    }
    [Flags]
    private enum ActivityDataFlags : uint { PreviousPointMap = 1, EventReaders = 2 }
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivityStep(uint bodies, uint rows, float duration, BodyActivitySettings settings, uint phase, uint substep,
        long number, double start, double end)
    {
        public uint Bodies = bodies, Rows = rows;
        public float Duration = duration, SpeedThreshold = settings.SpeedThreshold;
        public float QuietDuration = settings.QuietDuration, LinearErrorThreshold = settings.LinearErrorThreshold, AngularErrorThreshold = settings.AngularErrorThreshold;
        public uint Phase = phase, Substep = substep;
        public ActivityDataFlags Flags;
        public long Number = number;
        public double StartTime = start, EndTime = end;
    }
    private readonly struct Submission(DevicePhysicsActivity activity, PhysicsStepContext context, int phase, bool buildGraph,
        DeviceContactColumns? contacts, int history, bool remapped) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => activity._backend;
        public Device Device => activity._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => SubmitAsync().GetAwaiter().GetResult();
        public ValueTask<QueueCompletion> SubmitAsync() => activity.RecordAsync(context, phase, buildGraph, contacts, history, remapped);
    }
    internal JobHandle ScheduleReadback(in PhysicsStepContext context, PhysicsEventBuffer events, JobHandle dependency)
    {
        Scheduler scheduler = context.World.Execution!.Scheduler!;
        JobResourceAccess[] accesses = [.. _activities.GetResultReadAccesses(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        if (_collectResults >= 0)
        {
            var all = new List<JobResourceAccess>(accesses); AddResultAccesses(scheduler, all);
            JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new ResultSubmission(this, context, events.HasReaders), all.ToArray(), dependency);
            return scheduler.Schedule(new Apply(this, context, events), [JobResourceAccess.Write(scheduler.GetResourceToken(_activities)),
                JobResourceAccess.Read(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
                events.WriteAccess], copied);
        }
        return scheduler.Schedule(new Copy(this, scheduler, context, events, accesses), accesses, dependency);
    }
    private readonly struct Copy(DevicePhysicsActivity activity, Scheduler scheduler, PhysicsStepContext context,
        PhysicsEventBuffer events, JobResourceAccess[] accesses) : IJob
    {
        public void Execute()
        {
            using var trace = EngineTrace.Begin(CopyCommandsTrace);
            activity._backend.Begin(activity._commands, new(0, 0, 8));
            activity._backend.Barrier(activity._commands, new BufferBarrier(activity._activities.States,
                PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            activity._backend.CopyBuffer(activity._commands, new(activity._activities.States, 0, activity._readback, 0, activity._readback.Info.Size));
            activity._backend.Barrier(activity._commands, new BufferBarrier(activity._activities.States,
                PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            using RecordedCommands recorded = activity._backend.End(activity._commands);
            QueueCompletion complete = activity._backend.Submit(activity._backend.GetQueue(activity._device, QueueType.Compute), new([], [], [recorded], [], []));
            JobHandle ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(activity._backend, complete), accesses);
            JobResourceAccess[] writes = [JobResourceAccess.Write(scheduler.GetResourceToken(activity._activities)),
                JobResourceAccess.Read(scheduler.GetResourceToken(activity._readback), 0, (long)activity._readback.Info.Size),
                events.WriteAccess];
            scheduler.Schedule(new Apply(activity, context, events), writes, ready);
        }
    }
    private readonly struct Apply(DevicePhysicsActivity activity, PhysicsStepContext context, PhysicsEventBuffer events) : IJob
    {
        public void Execute()
        {
            using (EngineTrace.Begin(MapResultsTrace))
            {
                if (activity._collectResults >= 0) activity.AcceptResultChanges(in context, events);
                else
                {
                    activity._activities.UseResultCandidates = false;
                    using MappedBuffer mapping = activity._backend.Map(activity._readback, MapType.Read, BufferRange.Whole);
                    mapping.Invalidate(mapping.Range);
                    MemoryMarshal.Cast<byte, DeviceBodyActivityState>(mapping.Bytes).CopyTo(activity._activities.Results);
                    foreach (ref readonly DeviceBodyActivityState result in activity._activities.Results.AsSpan())
                        DeviceBodyActivities.AddEvents(in result, in context, events);
                }
            }
            activity._activities.ScheduleApply(default);
            activity._backend.CollectCompleted(activity._device);
        }
    }
    public void Dispose()
    {
        foreach (Pipeline pipeline in _pipelines) pipeline.Dispose();
        DisposeResultBuffers();
        _commands.Dispose(); _signalsView.Dispose(); _signals.Dispose(); _readback.Dispose();
        for (int bank = 0; bank < 2; bank++)
        {
            _rowReads[bank].Dispose(); _participantReads[bank].Dispose();
            _previousRowsView[bank].Dispose(); _previousParticipantsView[bank].Dispose();
            _previousRows[bank].Dispose(); _previousParticipants[bank].Dispose();
        }
    }
}
public readonly struct DeviceActivityStage(DevicePhysicsActivity activity, int phase) : IDevicePhysicsStage, IPhysicsStageComposition
{
    public object InputKey => activity;
    public object? SharedInputKey => activity.InputData;
    public IReadOnlyList<object> SharedInputs => activity.RecordingInputs;
    public bool CanBatch => phase == 2 || phase == 1 && !_buildGraph;
    public void Append(PhysicsStageCommands commands)
    {
        if (phase == 0)
        {
            commands.Schedule(new ReadActivityInputs(activity));
            commands.Append(new DeviceWork(activity, phase, _buildGraph));
        }
        else if (CanBatch) commands.Append(new DeviceWork(activity, phase, _buildGraph));
        else commands.Schedule(this);
    }
    private readonly struct ReadActivityInputs(DevicePhysicsActivity activity) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => activity.ScheduleInputs(in context, dependency);
    }
    private readonly struct DeviceWork(DevicePhysicsActivity activity, int phase, bool buildGraph) : IDevicePhysicsStage
    {
        public object InputKey => activity;
        public object? SharedInputKey => activity.InputData;
        public IReadOnlyList<object> SharedInputs => activity.RecordingInputs;
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => activity.ScheduleDevice(in context, phase, buildGraph, dependency);
    }
    private readonly bool _buildGraph = true;
    internal DeviceActivityStage(DevicePhysicsActivity activity, int phase, bool buildGraph) : this(activity, phase) => _buildGraph = buildGraph;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => activity.Schedule(in context, phase, _buildGraph, dependency);
}
public readonly struct ReadDeviceActivityResults(DevicePhysicsActivity activity, PhysicsEventBuffer events) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => activity.ScheduleReadback(in context, events, dependency);
}
