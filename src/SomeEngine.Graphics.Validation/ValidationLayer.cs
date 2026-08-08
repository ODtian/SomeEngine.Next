using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics.Validation;

/// <summary>
/// Optional validation receiver. Construction transfers ownership of the wrapped backend.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Construction transfers the wrapped backend's only disposal right to this
/// caller-disposed receiver. The configured message sink is borrowed and is never disposed. Diagnostic
/// callbacks are synchronous and cannot cancel underlying cleanup.</para>
/// <para><b>After Dispose:</b> No validation or wrapped-backend operation is available; diagnostics
/// emitted during teardown remain caller-owned sink output rather than a reopenable store.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed partial class ValidationLayer<TBackend> : IGraphicsBackend
    where TBackend : class, IGraphicsBackend
{
    private readonly object _gate = new();
    private readonly HashSet<Device> _devices = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Surface> _surfaces = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<GraphicsObject, ValidationObjectInfo> _objects = new();
    private readonly ConditionalWeakTable<Resource, ResourceValidationState> _resourceStates = new();
    private readonly ConditionalWeakTable<Queue, object> _queueSubmissionGates = new();
    private readonly ConditionalWeakTable<ExternalTimeline, TimelineValidationState> _timelines = new();
    private readonly ConditionalWeakTable<PersistentParameterBindings, BindingValidationState>
        _persistentBindingStates = new();
    private readonly ConditionalWeakTable<Pipeline, PipelineBindingValidationState>
        _pipelineBindingStates = new();
    private readonly ConditionalWeakTable<RecordedBundle, BundleValidationState> _bundleStates = new();
    private readonly ConditionalWeakTable<IndirectCommandLayout, IndirectLayoutValidationState>
        _indirectLayouts = new();
    private readonly ConditionalWeakTable<Pipeline, WorkGraphPipelineValidationState>
        _workGraphPipelines = new();
    private readonly ConditionalWeakTable<Pipeline, RayTracingPipelineValidationState>
        _rayTracingPipelines = new();
    private readonly ConditionalWeakTable<RayTracingShaderTable, RayTracingTableValidationState>
        _rayTracingTables = new();
    private readonly Dictionary<CommandContext, ContextValidationState> _contexts =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<RecordedCommandsKey, RecordedValidationState> _recorded = new();
    private readonly Dictionary<QuerySlot, QueryValidationState> _queryStates = new();
    private readonly Dictionary<Queue, ulong> _completedQueueValues =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<ManualSubmissionValidationState> _manualSubmissions = [];
    private readonly IValidationMessageSink? _sink;
    private readonly bool _reportLiveObjectsOnDispose;
    private TBackend? _backend;

    public ValidationLayer(TBackend backend, in ValidationOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _sink = options.MessageSink;
        _reportLiveObjectsOnDispose = options.ReportLiveObjectsOnDispose;

        if (backend is INativeValidationControl validationControl)
            validationControl.EnableNativeValidation();
    }

    private TBackend Backend => Volatile.Read(ref _backend)
        ?? throw new ObjectDisposedException(typeof(ValidationLayer<TBackend>).FullName);

    public bool TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount) =>
        Backend.TryEnumerateAdapters(options, destination, out requiredCount);

    public Device CreateDevice(in DeviceDesc desc)
    {
        if (desc.Queues.IsEmpty)
            Reject("Device", "DeviceDesc must request at least one Queue.");
        if (desc.EnabledNodeMask == 0)
            Reject("Device", "DeviceDesc.EnabledNodeMask must not be zero.");

        Device device = Backend.CreateDevice(desc);
        lock (_gate)
            _devices.Add(device);
        Track(device, null);
        return device;
    }

    public Surface CreateSurface(in SurfaceDesc desc)
    {
        if (desc.WindowHandle == 0)
            Reject("Presentation", "SurfaceDesc.WindowHandle must not be zero.");

        Surface surface = Backend.CreateSurface(desc);
        lock (_gate)
            _surfaces.Add(surface);
        Track(surface, null);
        return surface;
    }

    public Queue GetQueue(Device device, QueueType type, uint index = 0)
    {
        RequireDevice(device);
        return Backend.GetQueue(device, type, index);
    }

    public bool TryGetCapability<TCapability>(Device device, out TCapability? capability)
        where TCapability : DeviceCapability
    {
        RequireDevice(device);
        return Backend.TryGetCapability(device, out capability);
    }

    public void CollectCompleted(Device device)
    {
        RequireDevice(device);
        Backend.CollectCompleted(device);
        SweepManualSubmissions(device);
    }

    public bool IsComplete(in QueueCompletion completion)
    {
        RequireQueue(completion.Queue);
        bool result = Backend.IsComplete(completion);
        if (result)
            RecordCompletion(completion);
        else
            ReportManualLifetimeViolation(completion);
        return result;
    }

    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        RequireQueue(completion.Queue);
        WaitStatus result = Backend.WaitCpu(completion, timeout);
        if (result == WaitStatus.Completed)
            RecordCompletion(completion);
        else
            ReportManualLifetimeViolation(completion);
        return result;
    }

    public void Dispose()
    {
        TBackend? backend = Interlocked.Exchange(ref _backend, null);
        if (backend is null)
            return;

        try
        {
            if (_reportLiveObjectsOnDispose)
            {
                lock (_gate)
                {
                    int liveDevices = _devices.Count(
                        static device => device.Status != DeviceStatus.Disposed);
                    int liveSurfaces = _surfaces.Count(static surface => !surface.IsDisposed);
                    if (liveDevices != 0 || liveSurfaces != 0)
                    {
                        Report(
                            ValidationMessageType.Warning,
                            "Lifetime",
                            $"Validation receiver is closing with {liveDevices} live Device(s) and {liveSurfaces} live Surface(s).");
                    }
                }
            }
        }
        catch
        {
            // Validation diagnostics are observational and must never interrupt teardown.
        }

        try
        {
            ReportOutstandingManualSubmissions();
        }
        catch
        {
            // A failing user sink cannot strand the receiver-owned backend.
        }

        try
        {
            backend.Dispose();
        }
        catch
        {
            // Dispose is an idempotent, no-throw ownership boundary.
        }
    }

    private void RequireDevice(Device? device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (!_devices.Contains(device))
                Reject("Ownership", "The Device was not created through this Validation Layer.", device.Label);
        }
        device.ThrowIfUnavailable();
    }

    private void RequireQueue(Queue? queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        RequireDevice(queue.Device);
    }

    private T Require<T>(T? value)
        where T : DeviceResource
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireDevice(value.Device);
        if (value.IsDisposed)
            Reject("Lifetime", $"{typeof(T).Name} is disposed.", value.Label);
        if (!_objects.TryGetValue(value, out _))
            Reject("Ownership", $"{typeof(T).Name} was not created through this Validation Layer.", value.Label);
        return value;
    }

    private T RequireOnDevice<T>(Device expected, T? value, string objectType)
        where T : DeviceResource
    {
        T required = Require(value);
        RequireSameDevice(expected, required.Device, objectType);
        return required;
    }

    private T Track<T>(T value, GraphicsObject? parent)
        where T : GraphicsObject
    {
        _objects.Add(value, new ValidationObjectInfo(parent));
        if (value is Resource resource)
            _resourceStates.Add(resource, new ResourceValidationState(resource));
        return value;
    }

    private T TrackIfAbsent<T>(T value, GraphicsObject? parent)
        where T : GraphicsObject
    {
        _ = _objects.GetValue(value, _ => new ValidationObjectInfo(parent));
        if (value is Resource resource)
            _ = _resourceStates.GetValue(resource, static item => new ResourceValidationState(item));
        return value;
    }

    private void Report(
        ValidationMessageType type,
        string area,
        string text,
        string? label = null) =>
        _sink?.Report(new ValidationMessage(type, area, text, label));

    [DoesNotReturn]
    private void Reject(string area, string text, string? label = null)
    {
        Report(ValidationMessageType.Error, area, text, label);
        throw new InvalidOperationException(text);
    }

    private void RecordCompletion(in QueueCompletion completion)
    {
        lock (_gate)
        {
            if (!_completedQueueValues.TryGetValue(completion.Queue, out ulong completed) ||
                completion.Value > completed)
            {
                _completedQueueValues[completion.Queue] = completion.Value;
            }
            for (int index = _manualSubmissions.Count - 1; index >= 0; index--)
            {
                ManualSubmissionValidationState submission = _manualSubmissions[index];
                if (submission.Accepted &&
                    ReferenceEquals(submission.Completion.Queue, completion.Queue) &&
                    submission.Completion.Value <= completion.Value)
                {
                    _manualSubmissions.RemoveAt(index);
                }
            }
        }
    }

    private void ReportManualLifetimeViolation(in QueueCompletion completion)
    {
        lock (_gate)
        {
            foreach (ManualSubmissionValidationState submission in _manualSubmissions)
            {
                if (!submission.Accepted || submission.ViolationReported ||
                    !ReferenceEquals(submission.Completion.Queue, completion.Queue) ||
                    submission.Completion.Value > completion.Value)
                {
                    continue;
                }

                GraphicsObject? disposed = submission.Dependencies.FirstOrDefault(
                    static dependency => dependency.IsDisposed);
                if (disposed is null)
                    continue;
                submission.ViolationReported = true;
                Report(
                    ValidationMessageType.Error,
                    "Retirement",
                    $"Manual-retirement dependency '{disposed.Label ?? disposed.GetType().Name}' was disposed before Queue completion {submission.Completion.Value} was observed.",
                    disposed.Label);
            }
        }
    }

    private void SweepManualSubmissions(Device device)
    {
        ManualSubmissionValidationState[] submissions;
        lock (_gate)
        {
            submissions = _manualSubmissions
                .Where(submission => submission.Accepted &&
                    ReferenceEquals(submission.Completion.Queue.Device, device))
                .ToArray();
        }
        foreach (ManualSubmissionValidationState submission in submissions)
        {
            if (Backend.IsComplete(submission.Completion))
                RecordCompletion(submission.Completion);
            else
                ReportManualLifetimeViolation(submission.Completion);
        }
    }

    private void ReportOutstandingManualSubmissions()
    {
        lock (_gate)
        {
            foreach (ManualSubmissionValidationState submission in _manualSubmissions)
            {
                if (!submission.Accepted)
                    continue;
                GraphicsObject? disposed = submission.Dependencies.FirstOrDefault(
                    static dependency => dependency.IsDisposed);
                string detail = disposed is null
                    ? "has not been observed complete"
                    : $"still names disposed dependency '{disposed.Label ?? disposed.GetType().Name}'";
                Report(
                    ValidationMessageType.Warning,
                    "Retirement",
                    $"Manual Queue completion {submission.Completion.Value} {detail} during Validation receiver teardown.",
                    disposed?.Label);
            }
        }
    }

    private sealed record ValidationObjectInfo(GraphicsObject? Parent);

    private sealed class TimelineValidationState
    {
        internal TimelineValidationState(bool lastSignalKnown, ulong lastSignalValue)
        {
            LastSignalKnown = lastSignalKnown;
            LastSignalValue = lastSignalValue;
        }

        internal bool LastSignalKnown;
        internal ulong LastSignalValue;
        internal bool SubmissionInProgress;
    }

    private sealed class TimelineSignalReservation
    {
        internal TimelineSignalReservation(
            KeyValuePair<TimelineValidationState, ulong>[] entries) => Entries = entries;

        internal KeyValuePair<TimelineValidationState, ulong>[] Entries { get; }
    }

    private sealed class ContextValidationState
    {
        internal int ThreadId;
        internal bool Recording;
        internal bool Rendering;
        internal bool Bundle;
        internal Pipeline? Pipeline;
        internal PipelineType? PipelineType;
        internal PipelineSignature PipelineSignature;
        internal bool PipelineSignatureSet;
        internal bool WorkGraphProgram;
        internal int EventDepth;
        internal readonly List<QueryValidationEvent> QueryEvents = [];
        internal readonly Dictionary<QuerySlot, QueryLocalPhase> QueryPhases = [];
        internal readonly List<ResourceValidationEvent> ResourceEvents = [];
        internal readonly Dictionary<ResourceCellKey, LocalResourceState> ResourceStates = [];
        internal readonly HashSet<GraphicsObject> Dependencies =
            new(ReferenceEqualityComparer.Instance);
    }

    private sealed class IndirectLayoutValidationState
    {
        internal IndirectLayoutValidationState(
            PipelineType actionPipelineType,
            in PipelineSignature pipelineSignature,
            bool pipelineSignatureSet)
        {
            ActionPipelineType = actionPipelineType;
            PipelineSignature = pipelineSignature;
            PipelineSignatureSet = pipelineSignatureSet;
        }

        internal PipelineType ActionPipelineType { get; }
        internal PipelineSignature PipelineSignature { get; }
        internal bool PipelineSignatureSet { get; }
    }

    private sealed class WorkGraphPipelineValidationState
    {
        internal WorkGraphPipelineValidationState(
            uint maximumInputRecordCount,
            uint[] entryMaximumInputRecordCounts)
        {
            MaximumInputRecordCount = maximumInputRecordCount;
            EntryMaximumInputRecordCounts = entryMaximumInputRecordCounts;
        }

        internal uint MaximumInputRecordCount { get; }
        internal uint[] EntryMaximumInputRecordCounts { get; }
    }

    private enum RayExportValidationType : byte
    {
        RayGeneration,
        Miss,
        Hit,
        Callable,
    }

    private sealed record RayExportValidationState(
        RayExportValidationType Type,
        SlangShaderSharp.VariableLayoutReflection Layout,
        ValidationParameterBlockLayout? ParameterLayout);

    private sealed class RayTracingPipelineValidationState
    {
        internal Dictionary<SlangShaderSharp.EntryPointReflection, RayExportValidationState>
            Entries { get; } = [];
        internal Dictionary<string, RayExportValidationState> HitGroups { get; } =
            new(StringComparer.Ordinal);

        internal bool HasExport(RayExportValidationType type) =>
            Entries.Values.Any(value => value.Type == type);
    }

    private sealed record RayTracingTableValidationState(
        RayTracingPipelineValidationState Pipeline);

    private readonly struct RecordedCommandsKey : IEquatable<RecordedCommandsKey>
    {
        internal RecordedCommandsKey(in RecordedCommands commands)
        {
            Lease = commands.Lease;
            Sequence = commands.Sequence;
        }

        internal RecordedCommandsLease Lease { get; }
        internal ulong Sequence { get; }

        public bool Equals(RecordedCommandsKey other) =>
            ReferenceEquals(Lease, other.Lease) && Sequence == other.Sequence;

        public override bool Equals(object? obj) =>
            obj is RecordedCommandsKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(Lease), Sequence);
    }

    private readonly struct QuerySlot : IEquatable<QuerySlot>
    {
        internal QuerySlot(QueryPool pool, uint index)
        {
            Pool = pool;
            Index = index;
        }

        internal QueryPool Pool { get; }
        internal uint Index { get; }

        public bool Equals(QuerySlot other) =>
            ReferenceEquals(Pool, other.Pool) && Index == other.Index;

        public override bool Equals(object? obj) => obj is QuerySlot other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(Pool), Index);
    }

    private enum QueryValidationEventType : byte
    {
        Begin,
        End,
        Write,
        Resolve,
    }

    private enum QueryValidationPhase : byte
    {
        Idle,
        Active,
        Ready,
        Resolved,
    }

    private enum QueryLocalPhase : byte
    {
        Unknown,
        Active,
        Ready,
        Resolved,
    }

    private readonly record struct QueryValidationEvent(
        QuerySlot Slot,
        QueryValidationEventType Type);

    private sealed record RecordedValidationState(
        QueryValidationEvent[] QueryEvents,
        ResourceValidationEvent[] ResourceEvents,
        GraphicsObject[] Dependencies);

    private sealed class BindingValidationState
    {
        internal BindingValidationState(
            ValidationParameterBlockLayout layout,
            GraphicsObject[] dependencies)
        {
            Layout = layout;
            Dependencies = dependencies;
        }

        internal ValidationParameterBlockLayout Layout { get; }
        internal GraphicsObject[] Dependencies { get; set; }
    }

    private sealed record BundleValidationState(GraphicsObject[] Dependencies);

    private sealed class ManualSubmissionValidationState
    {
        internal ManualSubmissionValidationState(GraphicsObject[] dependencies)
        {
            Dependencies = dependencies;
        }

        internal QueueCompletion Completion;
        internal GraphicsObject[] Dependencies { get; }
        internal bool Accepted;
        internal bool ViolationReported;
    }

    private sealed class QueryValidationState
    {
        internal QueryValidationPhase Phase;
        internal Queue? Queue;
        internal QueueCompletion Completion;
        internal bool HasCompletion;
        internal TimelinePoint[] TimelineSignals = [];
        internal bool SubmissionInProgress;
    }

    private readonly record struct QuerySubmissionEntry(
        QueryValidationState State,
        QueryValidationPhase Phase);

    private sealed class QuerySubmissionReservation
    {
        internal QuerySubmissionReservation(
            QuerySubmissionEntry[] entries,
            TimelinePoint[] timelineSignals)
        {
            Entries = entries;
            TimelineSignals = timelineSignals;
        }

        internal QuerySubmissionEntry[] Entries { get; }
        internal TimelinePoint[] TimelineSignals { get; }
    }
}
