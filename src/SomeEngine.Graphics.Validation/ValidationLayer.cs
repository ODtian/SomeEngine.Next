using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics.Validation;

/// <summary>
/// Optional validation receiver. Construction transfers ownership of the wrapped backend.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Concurrent Dispose calls are safe and collectively perform one logical release; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Construction transfers the wrapped backend's only disposal right to this
/// caller-disposed receiver. The configured message sink is borrowed and is never disposed. Diagnostic
/// callbacks are synchronous and cannot cancel underlying cleanup.</para>
/// <para><b>After Dispose:</b> No validation or wrapped-backend operation is available; diagnostics
/// emitted during teardown remain caller-owned sink output rather than a reopenable store.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed partial class ValidationLayer : IGraphicsBackend
{
    private readonly object _gate = new();
    private readonly IdentityRegistry<Device, byte> _devices;
    private readonly IdentityRegistry<Surface, byte> _surfaces;
    private readonly IdentityRegistry<GraphicsObject, ValidationObjectInfo> _objects;
    private readonly IdentityRegistry<Resource, ResourceValidationState> _resourceStates;
    private readonly IdentityRegistry<Device, DeviceValidationState> _deviceStates;
    private readonly IdentityRegistry<Heap, HeapValidationState> _heapStates;
    private readonly IdentityRegistry<Queue, object> _queueSubmissionGates;
    private readonly IdentityRegistry<Queue, SubmitValidationWorkspace> _submitWorkspaces;
    private readonly IdentityRegistry<ExternalTimeline, TimelineValidationState> _timelines;
    private readonly IdentityRegistry<PersistentParameterBindings, BindingValidationState> _persistentBindingStates;
    private readonly IdentityRegistry<Pipeline, PipelineBindingValidationState> _pipelineBindingStates;
    private readonly IdentityRegistry<RecordedBundle, BundleValidationState> _bundleStates;
    private readonly IdentityRegistry<IndirectCommandLayout, IndirectLayoutValidationState> _indirectLayouts;
    private readonly IdentityRegistry<Pipeline, WorkGraphPipelineValidationState> _workGraphPipelines;
    private readonly IdentityRegistry<Pipeline, RayTracingPipelineValidationState> _rayTracingPipelines;
    private readonly IdentityRegistry<RayTracingShaderTable, RayTracingTableValidationState> _rayTracingTables;
    private readonly IdentityRegistry<CommandContext, ContextValidationState> _contexts;
    private readonly Dictionary<RecordedCommandsKey, RecordedValidationState> _recorded = new();
    private readonly Dictionary<QuerySlot, QueryValidationState> _queryStates = new();
    private readonly Dictionary<Queue, ulong> _completedQueueValues =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Queue, NativeQueueLockValidationState> _nativeQueueLockStates =
        new(ReferenceEqualityComparer.Instance);
    private readonly IValidationMessageSink? _sink;
    private readonly bool _reportLiveObjectsOnDispose;
    private DisposeGate _disposeGate;
    private IGraphicsBackend? _backend;
    private int _recordedCapacityReservations;
    private int _completionCapacityReservations;

    public ValidationLayer(IGraphicsBackend backend, in ValidationOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _sink = options.MessageSink;
        _reportLiveObjectsOnDispose = options.ReportLiveObjectsOnDispose;
        _devices = new IdentityRegistry<Device, byte>(_gate);
        _surfaces = new IdentityRegistry<Surface, byte>(_gate);
        _objects = new IdentityRegistry<GraphicsObject, ValidationObjectInfo>(_gate);
        _resourceStates = new IdentityRegistry<Resource, ResourceValidationState>(_gate);
        _deviceStates = new IdentityRegistry<Device, DeviceValidationState>(_gate);
        _heapStates = new IdentityRegistry<Heap, HeapValidationState>(_gate);
        _queueSubmissionGates = new IdentityRegistry<Queue, object>(_gate);
        _submitWorkspaces = new IdentityRegistry<Queue, SubmitValidationWorkspace>(_gate);
        _timelines = new IdentityRegistry<ExternalTimeline, TimelineValidationState>(_gate);
        _persistentBindingStates = new IdentityRegistry<PersistentParameterBindings, BindingValidationState>(_gate);
        _pipelineBindingStates = new IdentityRegistry<Pipeline, PipelineBindingValidationState>(_gate);
        _bundleStates = new IdentityRegistry<RecordedBundle, BundleValidationState>(_gate);
        _indirectLayouts = new IdentityRegistry<IndirectCommandLayout, IndirectLayoutValidationState>(_gate);
        _workGraphPipelines = new IdentityRegistry<Pipeline, WorkGraphPipelineValidationState>(_gate);
        _rayTracingPipelines = new IdentityRegistry<Pipeline, RayTracingPipelineValidationState>(_gate);
        _rayTracingTables = new IdentityRegistry<RayTracingShaderTable, RayTracingTableValidationState>(_gate);
        _contexts = new IdentityRegistry<CommandContext, ContextValidationState>(_gate);

        if (backend is INativeValidationControl validationControl)
            validationControl.EnableNativeValidation();
    }

    private IGraphicsBackend Backend => Volatile.Read(ref _backend)
        ?? throw new ObjectDisposedException(typeof(ValidationLayer).FullName);

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

        var deviceState = new DeviceValidationState(
            desc.EnabledNodeMask,
            desc.Queues);
        var objectInfo = new ValidationObjectInfo(null);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _devices.EnsureAdditionalCapacity();
            _deviceStates.EnsureAdditionalCapacity();
            Device? result = null;
            bool objectAdded = false;
            bool deviceAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateDevice(desc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _devices.Add(result, 0);
                deviceAdded = true;
                _deviceStates.Add(result, deviceState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _deviceStates.Remove(result!);
                if (deviceAdded)
                    _devices.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Surface CreateSurface(in SurfaceDesc desc)
    {
        if (desc.WindowHandle == 0)
            Reject("Presentation", "SurfaceDesc.WindowHandle must not be zero.");

        SurfaceDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(null);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _surfaces.EnsureAdditionalCapacity();
            Surface? result = null;
            bool objectAdded = false;
            bool surfaceAdded = false;
            try
            {
                result = Backend.CreateSurface(createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _surfaces.Add(result, 0);
                surfaceAdded = true;
                return result;
            }
            catch
            {
                if (surfaceAdded)
                    _surfaces.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
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
    }

    public bool IsComplete(in QueueCompletion completion)
    {
        RequireQueue(completion.Queue);
        bool reserved = false;
        lock (_gate)
        {
            if (!_completedQueueValues.ContainsKey(completion.Queue))
            {
                ReserveCompletionCapacity();
                reserved = true;
            }
        }
        try
        {
            bool result = Backend.IsComplete(completion);
            if (result)
                RecordCompletion(completion);
            return result;
        }
        finally
        {
            if (reserved)
                ReleaseCompletionCapacity();
        }
    }

    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        _ = Timeouts.ToMilliseconds(timeout, nameof(timeout));
        RequireQueue(completion.Queue);
        bool reserved = false;
        lock (_gate)
        {
            if (!_completedQueueValues.ContainsKey(completion.Queue))
            {
                ReserveCompletionCapacity();
                reserved = true;
            }
        }
        try
        {
            WaitStatus result = Backend.WaitCpu(completion, timeout);
            if (result == WaitStatus.Completed)
                RecordCompletion(completion);
            return result;
        }
        finally
        {
            if (reserved)
                ReleaseCompletionCapacity();
        }
    }

    public void Dispose()
    {
        if (!_disposeGate.TryEnter())
            return;

        try
        {
            IGraphicsBackend? backend = Interlocked.Exchange(ref _backend, null);
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
                backend.Dispose();
            }
            catch
            {
                // Dispose is an idempotent, no-throw ownership boundary.
            }
        }
        finally
        {
            _disposeGate.Exit();
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

    private sealed class IdentityRegistry<TKey, TValue>
        where TKey : class
    {
        private readonly object _gate;
        private readonly Dictionary<TKey, TValue> _entries = new(ReferenceEqualityComparer.Instance);

        internal IdentityRegistry(object gate) => _gate = gate;

        internal void EnsureAdditionalCapacity(int additionalCount = 1)
        {
            if (additionalCount < 0)
                throw new ArgumentOutOfRangeException(nameof(additionalCount));
            lock (_gate)
            {
                PruneDisposed();
                _entries.EnsureCapacity(checked(_entries.Count + additionalCount));
            }
        }

        internal void Add(TKey key, TValue value)
        {
            lock (_gate)
                _entries.Add(key, value);
        }

        internal bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            lock (_gate)
                return _entries.TryGetValue(key, out value);
        }

        internal TValue GetValue(TKey key, Func<TKey, TValue> factory)
        {
            lock (_gate)
            {
                PruneDisposed();
                if (_entries.TryGetValue(key, out TValue? value))
                    return value;
                _entries.EnsureCapacity(checked(_entries.Count + 1));
                value = factory(key);
                _entries.Add(key, value);
                return value;
            }
        }

        internal bool Contains(TKey key)
        {
            lock (_gate)
                return _entries.ContainsKey(key);
        }

        internal bool Remove(TKey key)
        {
            lock (_gate)
                return _entries.Remove(key);
        }

        internal int Count(Func<TKey, bool> predicate)
        {
            lock (_gate)
            {
                int count = 0;
                foreach (TKey key in _entries.Keys)
                    if (predicate(key))
                        count++;
                return count;
            }
        }

        private void PruneDisposed()
        {
            while (true)
            {
                TKey? disposed = null;
                foreach (TKey key in _entries.Keys)
                {
                    if (key is GraphicsObject graphicsObject && graphicsObject.IsDisposed)
                    {
                        disposed = key;
                        break;
                    }
                }
                if (disposed is null)
                    return;
                _entries.Remove(disposed);
            }
        }
    }

    private sealed class NativeQueueLockValidationState
    {
        internal int OwnerThreadId;
        internal ValidationQueueLockLease? Lease;
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
        }
    }

    private sealed record ValidationObjectInfo(GraphicsObject? Parent);

    private sealed class DeviceValidationState
    {
        private readonly Dictionary<(QueueType Type, uint Index), uint> _queueNodeMasks = [];

        internal DeviceValidationState(
            uint enabledNodeMask,
            ReadOnlySpan<DeviceQueueDesc> descriptions)
        {
            EnabledNodeMask = enabledNodeMask;
            var nextIndices = new Dictionary<QueueType, uint>();
            foreach (ref readonly DeviceQueueDesc description in descriptions)
            {
                nextIndices.TryGetValue(description.Type, out uint firstIndex);
                uint nodeMask = 1u << checked((int)description.NodeIndex);
                for (uint offset = 0; offset < description.Count; offset++)
                {
                    _queueNodeMasks.Add(
                        (description.Type, checked(firstIndex + offset)),
                        nodeMask);
                }
                nextIndices[description.Type] = checked(firstIndex + description.Count);
            }
        }

        internal uint EnabledNodeMask { get; }

        internal uint GetQueueNodeMask(QueueType type, uint index) =>
            _queueNodeMasks.TryGetValue((type, index), out uint mask)
                ? mask
                : throw new InvalidOperationException(
                    $"Queue {type}[{index}] was not requested in the Device description.");

        internal uint ResolveNodeIndex(uint requestedNodeIndex, string parameterName)
        {
            uint nodeIndex = requestedNodeIndex == uint.MaxValue
                ? checked((uint)BitOperations.TrailingZeroCount(EnabledNodeMask))
                : requestedNodeIndex;
            if (nodeIndex >= 32 ||
                (EnabledNodeMask & (1u << checked((int)nodeIndex))) == 0)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The node index must select one enabled linked-adapter node.");
            }
            return nodeIndex;
        }
    }

    private sealed record HeapValidationState(uint VisibleNodeMask);

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
        internal uint QueueNodeMask;
        internal Pipeline? Pipeline;
        internal PipelineType? PipelineType;
        internal bool WorkGraphBound;
        internal int EventDepth;
        internal RecordingValidationPayload Payload = new();
        internal readonly Stack<RecordingValidationPayload> AvailablePayloads = [];
        internal readonly Stack<RecordedValidationState> AvailableRecordings = [];
        internal List<QueryValidationEvent> QueryEvents => Payload.QueryEvents;
        internal Dictionary<QuerySlot, QueryLocalPhase> QueryPhases => Payload.QueryPhases;
        internal List<ResourceValidationEvent> ResourceEvents => Payload.ResourceEvents;
        internal Dictionary<ResourceCellKey, LocalResourceState> ResourceStates => Payload.ResourceStates;
        internal HashSet<GraphicsObject> Dependencies => Payload.Dependencies;

        internal RecordingValidationPayload TransferPayload()
        {
            AvailablePayloads.EnsureCapacity(checked(AvailablePayloads.Count + 1));
            RecordingValidationPayload transferred = Payload;
            Payload = AvailablePayloads.Count == 0
                ? new RecordingValidationPayload()
                : AvailablePayloads.Pop();
            return transferred;
        }

        internal void RestorePayload(RecordingValidationPayload transferred)
        {
            RecordingValidationPayload replacement = Payload;
            Payload = transferred;
            AvailablePayloads.Push(replacement);
        }

        internal void RecyclePayload(RecordingValidationPayload payload)
        {
            payload.Clear();
            AvailablePayloads.Push(payload);
        }

        internal RecordedValidationState RentRecording(RecordingValidationPayload payload)
        {
            AvailableRecordings.EnsureCapacity(checked(AvailableRecordings.Count + 1));
            RecordedValidationState recording = AvailableRecordings.Count == 0
                ? new RecordedValidationState()
                : AvailableRecordings.Pop();
            recording.Initialize(this, payload);
            return recording;
        }

        internal void RecycleRecording(RecordedValidationState recording)
        {
            RecordingValidationPayload payload = recording.ReleasePayload();
            RecyclePayload(payload);
            AvailableRecordings.Push(recording);
        }
    }

    private sealed class RecordingValidationPayload
    {
        internal readonly List<QueryValidationEvent> QueryEvents = [];
        internal readonly Dictionary<QuerySlot, QueryLocalPhase> QueryPhases = [];
        internal readonly List<ResourceValidationEvent> ResourceEvents = [];
        internal readonly Dictionary<ResourceCellKey, LocalResourceState> ResourceStates = [];
        internal readonly HashSet<GraphicsObject> Dependencies =
            new(ReferenceEqualityComparer.Instance);

        internal void Clear()
        {
            QueryEvents.Clear();
            QueryPhases.Clear();
            ResourceEvents.Clear();
            ResourceStates.Clear();
            Dependencies.Clear();
        }
    }

    private struct CommandMutationCapacity
    {
        internal int Dependencies;
        internal int QueryEvents;
        internal int QueryPhases;
        internal int ResourceEvents;
        internal int ResourceStates;
    }

    private sealed class IndirectLayoutValidationState
    {
        internal IndirectLayoutValidationState(
            PipelineType actionPipelineType,
            Pipeline? pipeline)
        {
            ActionPipelineType = actionPipelineType;
            Pipeline = pipeline;
        }

        internal PipelineType ActionPipelineType { get; }
        internal Pipeline? Pipeline { get; }
    }

    private sealed class WorkGraphPipelineValidationState
    {
        private readonly WorkGraphEntryPointInfo[] _entries;

        internal WorkGraphPipelineValidationState(WorkGraphEntryPointInfo[] entries)
        {
            _entries = entries;
        }

        internal WorkGraphEntryPointInfo GetEntryPoint(
            SlangShaderSharp.EntryPointReflection entryPoint)
        {
            if (entryPoint == SlangShaderSharp.EntryPointReflection.Null)
                throw new ArgumentException("A Work Graph dispatch requires a Slang entry point.");
            foreach (ref readonly WorkGraphEntryPointInfo entry in _entries.AsSpan())
            {
                if (entry.EntryPoint == entryPoint)
                    return entry;
            }
            throw new ArgumentException(
                "The Slang entry point is not a materialized program entry of this Work Graph Pipeline.");
        }
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
        SlangShaderSharp.VariableLayoutReflection[] Layouts);

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

    private sealed class RecordedValidationState
    {
        private ContextValidationState? _owner;
        private RecordingValidationPayload? _payload;

        internal void Initialize(
            ContextValidationState owner,
            RecordingValidationPayload payload)
        {
            _owner = owner;
            _payload = payload;
        }

        internal ContextValidationState Owner => _owner!;
        internal RecordingValidationPayload Payload => _payload!;
        internal List<QueryValidationEvent> QueryEvents => Payload.QueryEvents;
        internal List<ResourceValidationEvent> ResourceEvents => Payload.ResourceEvents;
        internal HashSet<GraphicsObject> Dependencies => Payload.Dependencies;

        internal RecordingValidationPayload ReleasePayload()
        {
            RecordingValidationPayload payload = _payload!;
            _payload = null;
            _owner = null;
            return payload;
        }
    }

    private sealed class BindingValidationState
    {
        internal BindingValidationState(
            SlangShaderSharp.VariableLayoutReflection layout,
            Pipeline pipeline,
            PipelineBindingValidationState validation,
            GraphicsObject[] dependencies)
        {
            Layout = layout;
            Pipeline = pipeline;
            Validation = validation;
            Dependencies = dependencies;
        }

        internal SlangShaderSharp.VariableLayoutReflection Layout { get; }
        internal Pipeline Pipeline { get; }
        internal PipelineBindingValidationState Validation { get; }
        internal GraphicsObject[] Dependencies { get; set; }
    }

    private sealed record BundleValidationState(HashSet<GraphicsObject> Dependencies);

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
        internal readonly Dictionary<QuerySlot, QuerySubmissionEntry> Simulated = [];
        internal readonly List<QuerySubmissionEntry> Entries = [];
        internal readonly List<KeyValuePair<QuerySlot, QueryValidationState>> NewStates = [];
        internal TimelinePoint[] TimelineSignals = [];

        internal void Clear()
        {
            Simulated.Clear();
            Entries.Clear();
            NewStates.Clear();
            TimelineSignals = [];
        }
    }

    private sealed class SubmitValidationReservation
    {
        internal ResourceSubmissionReservation? Resources;
        internal QuerySubmissionReservation? Queries;
        internal TimelineSignalReservation? Timelines;
    }

    private sealed class SubmitValidationWorkspace
    {
        internal readonly HashSet<SwapchainImageLease> Images =
            new(ReferenceEqualityComparer.Instance);
        internal readonly HashSet<RecordedCommandsKey> CommandKeys = [];
        internal readonly List<RecordedValidationState> Recordings = [];
        internal readonly ResourceSubmissionReservation Resources = new();
        internal readonly QuerySubmissionReservation Queries = new();
        internal readonly SubmitValidationReservation Reservation = new();

        internal void Clear()
        {
            Images.Clear();
            CommandKeys.Clear();
            Recordings.Clear();
            Resources.Clear();
            Queries.Clear();
            Reservation.Resources = null;
            Reservation.Queries = null;
            Reservation.Timelines = null;
        }
    }
}
