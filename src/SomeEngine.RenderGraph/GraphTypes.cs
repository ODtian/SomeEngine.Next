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

public readonly record struct PassOptions(
    PassCullingMode Culling = PassCullingMode.Cullable,
    PassSchedulingMode Scheduling = PassSchedulingMode.Reorderable,
    PassRecordingMode Recording = PassRecordingMode.WorkerEligible,
    uint EstimatedExecutionCost = 1,
    uint EstimatedRecordingCost = 1,
    uint RasterWidth = 0,
    uint RasterHeight = 0);

public readonly record struct RenderGraphFrameOptions(
    FrameSubmissionMode SubmissionMode = FrameSubmissionMode.Pipelined,
    RenderGraphDebugOptions Debug = RenderGraphDebugOptions.None,
    RenderGraphDiagnosticsHandler? Diagnostics = null);

public readonly struct PassQueueSelection : IEquatable<PassQueueSelection>
{
    private PassQueueSelection(Queue? queue, QueueType type, bool exact)
    {
        Queue = queue;
        Type = type;
        IsExact = exact;
    }

    public bool IsExact { get; }
    public Queue? Queue { get; }
    public QueueType Type { get; }

    public bool Equals(PassQueueSelection other) =>
        IsExact == other.IsExact && Type == other.Type && ReferenceEquals(Queue, other.Queue);
    public override bool Equals(object? other) => other is PassQueueSelection value && Equals(value);
    public override int GetHashCode() => HashCode.Combine(IsExact, Type, Queue);
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
}

public readonly record struct BufferBoundaryState(
    BufferRange Range,
    PipelineSync Sync,
    ResourceAccess Access,
    ResourceContentState Contents,
    Queue? Queue = null,
    QueueCompletion? ReadyAfter = null,
    BufferContentScope ContentScope = BufferContentScope.FullRange,
    Queue? ReleasedTo = null);

public readonly record struct TextureBoundaryState(
    TextureSubresourceRange Range,
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout Layout,
    ResourceContentState Contents,
    Queue? Queue = null,
    QueueCompletion? ReadyAfter = null,
    Queue? ReleasedTo = null,
    TextureSubresourceRange? ReleaseRange = null);

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
/// </summary>
public enum UnorderedAccessHazardMode : byte
{
    Ordered,
    AtomicMergeable,
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
