namespace SomeEngine.RenderGraph;

public enum ResourceContentState : byte
{
    Undefined,
    Defined,
}

public enum WriteCoverage : byte
{
    Partial,
    Complete,
}

/// <summary>
/// Describes which logical portion of a Buffer access must contain initialized values.
/// Count-bounded prefixes are GPU-produced append ranges whose valid element count is carried
/// separately; consumers using the same contract must never index outside that count.
/// </summary>
public enum BufferContentScope : byte
{
    FullRange,
    CountBoundedPrefix,
    /// <summary>
    /// Disjoint GPU-produced ranges identified by separate offsets and counts.
    /// Consumers may read only the elements inside those ranges.
    /// </summary>
    CountBoundedRanges,
}

public enum PassCullingMode : byte
{
    Cullable,
    NeverCull,
}

public enum PassSchedulingMode : byte
{
    Reorderable,
    PreserveDeclarationPosition,
}

public enum PassRecordingMode : byte
{
    WorkerEligible,
    CallingThread,
}

public enum FrameSubmissionMode : byte
{
    Pipelined,
    RecordAllThenSubmit,
    /// <summary>
    /// Records worker-eligible batches ahead of submission. Each submission waits only
    /// for its own recording. Earlier submissions remain valid if later recording fails;
    /// every recording job is joined before frame execution returns, including on failure.
    /// </summary>
    RecordAhead,
}

[Flags]
public enum RenderGraphDebugOptions : uint
{
    None = 0,
    DisableCulling = 1u << 0,
    DeclarationOrderScheduling = 1u << 1,
    DisableSplitBarriers = 1u << 2,
    DisableParallelRecording = 1u << 3,
}

/// <param name="RepeatCommands">
/// Allows reuse of recorded commands until declared resources, bindings or pass data change.
/// The callback must only encode commands, with no per-frame CPU side effects or reads of
/// undeclared mutable data. Change command data through the graph setters. This does not skip
/// GPU execution: shaders and copies still consume the current buffer contents every frame.
/// </param>
public readonly record struct PassOptions(
    PassCullingMode Culling = PassCullingMode.Cullable,
    PassSchedulingMode Scheduling = PassSchedulingMode.Reorderable,
    PassRecordingMode Recording = PassRecordingMode.WorkerEligible,
    uint EstimatedExecutionCost = 1,
    uint EstimatedRecordingCost = 1,
    uint RasterWidth = 0,
    uint RasterHeight = 0,
    bool RepeatCommands = false)
{
    /// <summary>Ends this queue's submission after the pass so its result can be observed earlier.</summary>
    public bool SubmitAfter { get; init; }
}

public readonly record struct RenderGraphFrameOptions(
    FrameSubmissionMode SubmissionMode = FrameSubmissionMode.Pipelined,
    RenderGraphDebugOptions Debug = RenderGraphDebugOptions.None,
    RenderGraphDiagnosticsHandler? Diagnostics = null);

public readonly struct PassQueueSelection : IEquatable<PassQueueSelection>
{
    private PassQueueSelection(Queue? queue, QueueType type, bool exact, bool anyCompatible = false)
    {
        Queue = queue;
        Type = type;
        IsExact = exact;
        AllowsAnyType = anyCompatible;
    }

    public bool IsExact { get; }
    public Queue? Queue { get; }
    public QueueType Type { get; }
    public bool AllowsAnyType { get; }

    public bool Equals(PassQueueSelection other) =>
        IsExact == other.IsExact && AllowsAnyType == other.AllowsAnyType &&
        Type == other.Type && ReferenceEquals(Queue, other.Queue);
    public override bool Equals(object? other) => other is PassQueueSelection value && Equals(value);
    public override int GetHashCode() => HashCode.Combine(IsExact, AllowsAnyType, Type, Queue);
    public static bool operator ==(PassQueueSelection left, PassQueueSelection right) => left.Equals(right);
    public static bool operator !=(PassQueueSelection left, PassQueueSelection right) => !left.Equals(right);

    public static PassQueueSelection Exact(Queue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return new PassQueueSelection(queue, queue.Type, true);
    }

    public static PassQueueSelection AnyOfType(QueueType type)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        return new PassQueueSelection(null, type, false);
    }

    /// <summary>
    /// Allows any registered queue that supports the pass commands, preferring
    /// fewer transfers from predecessor queues before comparing available time.
    /// Copies also prefer the queues explicitly requested by their direct consumers.
    /// </summary>
    public static PassQueueSelection AnyCompatible() => new(null, default, false, true);
}

public record struct BufferBoundaryState(
    BufferRange Range,
    PipelineSync Sync,
    ResourceAccess Access,
    ResourceContentState Contents,
    Queue? Queue = null,
    QueueCompletion? ReadyAfter = null,
    BufferContentScope ContentScope = BufferContentScope.FullRange,
    Queue? ReleasedTo = null)
{
    public BufferRange Range { get; init; } = Range;
    public PipelineSync Sync { get; init; } = Sync;
    public ResourceAccess Access { get; init; } = Access;
    public ResourceContentState Contents { get; init; } = Contents;
    public Queue? Queue { get; init; } = Queue;
    public BufferContentScope ContentScope { get; init; } = ContentScope;
    public Queue? ReleasedTo { get; init; } = ReleasedTo;

    private QueueCompletion? _readyAfter = ReadyAfter;
    public QueueCompletion? ReadyAfter
    {
        readonly get => _readyAfter;
        init => _readyAfter = value;
    }

    internal void SetCompletion(QueueCompletion? completion) => _readyAfter = completion;
}

public record struct TextureBoundaryState(
    TextureSubresourceRange Range,
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout Layout,
    ResourceContentState Contents,
    Queue? Queue = null,
    QueueCompletion? ReadyAfter = null,
    Queue? ReleasedTo = null,
    TextureSubresourceRange? ReleaseRange = null)
{
    public TextureSubresourceRange Range { get; init; } = Range;
    public PipelineSync Sync { get; init; } = Sync;
    public ResourceAccess Access { get; init; } = Access;
    public TextureLayout Layout { get; init; } = Layout;
    public ResourceContentState Contents { get; init; } = Contents;
    public Queue? Queue { get; init; } = Queue;
    public Queue? ReleasedTo { get; init; } = ReleasedTo;
    public TextureSubresourceRange? ReleaseRange { get; init; } = ReleaseRange;

    private QueueCompletion? _readyAfter = ReadyAfter;
    public QueueCompletion? ReadyAfter
    {
        readonly get => _readyAfter;
        init => _readyAfter = value;
    }

    internal void SetCompletion(QueueCompletion? completion) => _readyAfter = completion;
}

public readonly record struct QueryRange(uint FirstQuery, uint QueryCount);

public readonly record struct RenderGraphDesc(
    uint MaximumFramesInFlight = 3,
    ulong MaximumHeapBytes = ulong.MaxValue,
    string? Label = null);

public enum RenderGraphResourceOwnership : byte
{
    GraphOwned,
    CallerOwned,
}

public enum RenderGraphResourceLifetime : byte
{
    Persistent,
    PerFrame,
}

public enum GraphPassKind : byte
{
    Raster,
    Compute,
    Copy,
    General,
}

public enum GraphAccessMode : byte
{
    Read,
    Write,
    ReadWrite,
}

/// <summary>
/// Describes whether overlapping unordered-access writes require an execution order.
/// AtomicMergeable is a strong caller contract: every write is an atomic, associative,
/// commutative reduction into the existing value, so peer passes may execute in either order.
/// DisjointWrites promises that written bytes or texels are separate from every peer
/// unordered writer's accesses. Readers wait for every writer in either mode.
/// </summary>
public enum UnorderedAccessHazardMode : byte
{
    Ordered,
    AtomicMergeable,
    DisjointWrites,
}

internal static class UnorderedAccessHazardRules
{
    [System.Diagnostics.Conditional("DEBUG")]
    internal static void Validate(
        UnorderedAccessHazardMode hazards,
        GraphAccessMode mode,
        WriteCoverage coverage,
        ResourceAccess access,
        TextureLayout layout,
        ResourceContentState? resultContents)
    {
        if (!Enum.IsDefined(hazards))
            throw new ArgumentOutOfRangeException(nameof(hazards));
        if (hazards == UnorderedAccessHazardMode.Ordered)
            return;
        if (hazards == UnorderedAccessHazardMode.DisjointWrites)
        {
            if (mode == GraphAccessMode.Write && coverage == WriteCoverage.Partial &&
                access == ResourceAccess.UnorderedAccess &&
                resultContents is null or ResourceContentState.Defined &&
                layout is TextureLayout.Undefined or TextureLayout.UnorderedAccess)
                return;
            throw new ArgumentException("Disjoint writes require a partial UAV write in UnorderedAccess layout.");
        }
        if (mode != GraphAccessMode.ReadWrite ||
            coverage != WriteCoverage.Partial ||
            access != ResourceAccess.UnorderedAccess ||
            resultContents.HasValue ||
            layout is not (TextureLayout.Undefined or TextureLayout.UnorderedAccess))
        {
            throw new ArgumentException(
                "Atomic-mergeable access must be a partial ReadWrite UAV access in UnorderedAccess layout without a result-content override.");
        }
    }
}

public enum GraphAccessTargetKind : byte
{
    Buffer,
    Texture,
    QueryPool,
    RayTracingShaderTable,
}

/// <summary>A buffer and the byte range containing one value or array.</summary>
public readonly record struct GraphBufferSlice(GraphBufferId Buffer, BufferRange Range)
{
    internal BufferRange ResolveRange(BufferRange? requested)
    {
        if (requested is null || requested.Value.IsWhole) return Range;
        BufferRange range = requested.Value;
        DevelopmentCheck.Require(Range.IsWhole ||
            (range.Offset <= Range.Size && range.Size <= Range.Size - range.Offset),
            "The requested buffer range exceeds its input slice.");
        return new BufferRange(checked(Range.Offset + range.Offset), range.Size);
    }
}
