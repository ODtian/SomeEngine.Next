namespace SomeEngine.RenderGraph;

public delegate void RenderGraphDiagnosticsHandler(in RenderGraphDiagnosticsView diagnostics);

public readonly ref struct RenderGraphDiagnosticsView
{
    internal RenderGraphDiagnosticsView(
        ulong structureVersion,
        ReadOnlySpan<RenderGraphPassDiagnostic> passes,
        ReadOnlySpan<RenderGraphBufferDiagnostic> buffers,
        ReadOnlySpan<RenderGraphTextureDiagnostic> textures,
        ReadOnlySpan<RenderGraphAccessDiagnostic> accesses,
        ReadOnlySpan<RenderGraphDependencyDiagnostic> dependencies,
        ReadOnlySpan<RenderGraphBarrierDiagnostic> barriers,
        in RenderGraphStatistics statistics)
    {
        StructureVersion = structureVersion;
        Passes = passes;
        Buffers = buffers;
        Textures = textures;
        Accesses = accesses;
        Dependencies = dependencies;
        Barriers = barriers;
        Statistics = statistics;
    }

    public ulong StructureVersion { get; }
    public ReadOnlySpan<RenderGraphPassDiagnostic> Passes { get; }
    public ReadOnlySpan<RenderGraphBufferDiagnostic> Buffers { get; }
    public ReadOnlySpan<RenderGraphTextureDiagnostic> Textures { get; }
    public ReadOnlySpan<RenderGraphAccessDiagnostic> Accesses { get; }
    public ReadOnlySpan<RenderGraphDependencyDiagnostic> Dependencies { get; }
    public ReadOnlySpan<RenderGraphBarrierDiagnostic> Barriers { get; }
    public RenderGraphStatistics Statistics { get; }
}

public readonly record struct RenderGraphPassDiagnostic(
    GraphPassId Id,
    string Label,
    GraphPassKind Kind,
    bool Enabled,
    bool Live,
    long DeclarationOrdinal,
    int ScheduledOrdinal,
    Queue? Queue);

public readonly record struct RenderGraphBufferDiagnostic(
    GraphBufferId Id,
    string? Label,
    RenderGraphResourceOwnership Ownership,
    RenderGraphResourceLifetime Lifetime,
    ulong Size,
    ulong PlacementOffset,
    ulong PlacementSize,
    int FirstScheduledUse,
    int LastScheduledUse);

public readonly record struct RenderGraphTextureDiagnostic(
    GraphTextureId Id,
    string? Label,
    RenderGraphResourceOwnership Ownership,
    RenderGraphResourceLifetime Lifetime,
    uint Width,
    uint Height,
    Format Format,
    ulong PlacementOffset,
    ulong PlacementSize,
    int FirstScheduledUse,
    int LastScheduledUse);

public readonly record struct RenderGraphAccessDiagnostic(
    GraphPassId Pass,
    GraphAccessTargetKind TargetKind,
    int TargetOrdinal,
    GraphAccessMode Mode,
    WriteCoverage Coverage,
    PipelineSync Sync,
    ResourceAccess Access,
    BufferRange BufferRange,
    TextureSubresourceRange TextureRange,
    TextureLayout TextureLayout,
    ResourceContentState? ResultContents,
    BufferContentScope BufferContentScope);

public enum RenderGraphDependencyKind : byte
{
    Value,
    Execution,
    Physical,
}

public readonly record struct RenderGraphDependencyDiagnostic(
    GraphPassId Predecessor,
    GraphPassId Consumer,
    RenderGraphDependencyKind Kind);

public enum RenderGraphBarrierKind : byte
{
    Buffer,
    Texture,
    QueueAcquire,
    QueueRelease,
    Aliasing,
}

public readonly record struct RenderGraphBarrierDiagnostic(
    GraphPassId Pass,
    RenderGraphBarrierKind Kind,
    BarrierPhase Phase);

/// <summary>Counts and memory usage for the execution being diagnosed.</summary>
/// <param name="DeclaredPassCount">Number of declared passes.</param>
/// <param name="LivePassCount">Number of passes retained by value analysis.</param>
/// <param name="ScheduledPassCount">Number of scheduled passes.</param>
/// <param name="BufferCount">Number of buffer declarations.</param>
/// <param name="TextureCount">Number of texture declarations.</param>
/// <param name="AccessCount">Number of resource accesses.</param>
/// <param name="DependencyCount">Number of pass dependencies.</param>
/// <param name="BarrierCount">Number of diagnostic barrier entries.</param>
/// <param name="QueueCount">Number of queues used by this execution.</param>
/// <param name="TransientResourceBytes">Sum of the live transient resource allocation requirements, before aliasing.</param>
/// <param name="TransientOccupiedBytes">Bytes occupied by distinct transient regions in this execution's retained heaps.</param>
/// <param name="TransientHeapBytes">Actual capacity of this execution's retained transient heaps, including unused space.</param>
/// <param name="GraphHeapBytes">Actual heap capacity charged to the graph, including persistent resources and every frame in flight.</param>
public readonly record struct RenderGraphStatistics(
    int DeclaredPassCount,
    int LivePassCount,
    int ScheduledPassCount,
    int BufferCount,
    int TextureCount,
    int AccessCount,
    int DependencyCount,
    int BarrierCount,
    int QueueCount,
    ulong TransientResourceBytes,
    ulong TransientOccupiedBytes,
    ulong TransientHeapBytes,
    ulong GraphHeapBytes)
{
    public bool AnalysisReused { get; init; }
    public ulong TransientUnusedBytes => TransientHeapBytes - TransientOccupiedBytes;
}

