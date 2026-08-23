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

public enum RasterPassMergeMode : byte
{
    Mergeable,
    Isolated,
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
    DisableRasterMerging = 1u << 2,
    DisableSplitBarriers = 1u << 3,
    DisableParallelRecording = 1u << 4,
}

public readonly record struct PassOptions(
    PassCullingMode Culling = PassCullingMode.Cullable,
    PassSchedulingMode Scheduling = PassSchedulingMode.Reorderable,
    PassRecordingMode Recording = PassRecordingMode.WorkerEligible,
    RasterPassMergeMode RasterMerging = RasterPassMergeMode.Mergeable,
    uint EstimatedExecutionCost = 1,
    uint EstimatedRecordingCost = 1);

public readonly record struct RenderGraphFrameOptions(
    FrameSubmissionMode SubmissionMode = FrameSubmissionMode.Pipelined,
    RenderGraphDebugOptions Debug = RenderGraphDebugOptions.None,
    RenderGraphDiagnosticsHandler? Diagnostics = null);

public readonly struct PassQueueSelection
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
    QueueCompletion? ReadyAfter = null);

public readonly record struct TextureBoundaryState(
    TextureSubresourceRange Range,
    PipelineSync Sync,
    ResourceAccess Access,
    TextureLayout Layout,
    ResourceContentState Contents,
    Queue? Queue = null,
    QueueCompletion? ReadyAfter = null);

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

public enum GraphAccessTargetKind : byte
{
    Buffer,
    Texture,
    QueryPool,
    RayTracingShaderTable,
}

