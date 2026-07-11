namespace SomeEngine.RenderGraph;

public sealed class RenderGraph : IDisposable
{
    private readonly IDevice _device;
    private readonly int _coordinatorThread;
    private readonly CompilationCache _cache;
    private readonly RenderGraphOptions _options;
    private GraphRecording? _active;
    private long _recordings;
    private long _conservativeCompilations;
    private long _conservativePlanSelections;
    private long _optimizedPlanSelections;
    private long _cacheHits;
    private long _cacheMisses;
    private long _optimizedFlights;
    private long _singleFlightJoins;
    private long _publishedCandidates;
    private long _droppedCandidates;
    private long _failedCandidates;
    private long _cacheEvictions;
    private long _cacheRetirements;
    private long _recordedCommandLists;
    private long _submissions;
    private RenderGraphAliasingStatistics _lastAliasing;
    private RenderGraphRasterStatistics _lastRaster;
    private RenderGraphCullingStatistics _lastCulling;
    private bool _disposed;

    public RenderGraph(IDevice device, RenderGraphOptions? options = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _options = options ?? new RenderGraphOptions();
        if (_options.CompilationCacheEntryLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Compilation cache entry limit cannot be negative.");
        if (_options.CompilationCachePayloadByteBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Compilation cache payload budget cannot be negative.");
        _coordinatorThread = Environment.CurrentManagedThreadId;
        bool enableTransientAliasing = _options.EnableTransientAliasing;
        bool enableRenderPassMerging = _options.EnableRenderPassMerging;
        ulong compilerPolicy = (enableTransientAliasing ? 1UL : 0UL) |
                               (enableRenderPassMerging ? 2UL : 0UL);
        _cache = new CompilationCache(
            device,
            _options.CompilationCacheEntryLimit,
            _options.CompilationCachePayloadByteBudget,
            _options.CompileOptimizedPlansAsynchronously,
            OnCompilationEvent,
            compiler: (graph, compilation, optimized) => Compiler.Compile(
                graph,
                compilation,
                optimized,
                enableTransientAliasing,
                enableRenderPassMerging),
            reportDiagnostic: _options.CompilationDiagnosticSink,
            compilerPolicy: compilerPolicy);
    }

    public RenderGraphStatistics Statistics
    {
        get
        {
            EnsureCoordinator();
            return new RenderGraphStatistics(
                _recordings,
                _conservativeCompilations,
                _conservativePlanSelections,
                _optimizedPlanSelections,
                _cacheHits,
                _cacheMisses,
                _optimizedFlights,
                _singleFlightJoins,
                _publishedCandidates,
                _droppedCandidates,
                _failedCandidates,
                _cacheEvictions,
                _cacheRetirements,
                _cache.ResidentEntryCount,
                _cache.ResidentPayloadBytes,
                _cache.RetiringEntryCount,
                _cache.RetiringPayloadBytes,
                _recordedCommandLists,
                _submissions,
                _lastAliasing,
                _lastRaster,
                _lastCulling);
        }
    }

    public GraphBuilder Begin()
    {
        EnsureCoordinator();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active is not null) throw new InvalidOperationException("Only one graph recording may be active per coordinator.");
        _cache.Drain();
        _device.CollectGarbage();
        _active = new GraphRecording();
        _recordings++;
        return new GraphBuilder(this, _active);
    }

    public GraphExecution Execute(ref GraphBuilder builder)
    {
        EnsureCoordinator();
        ObjectDisposedException.ThrowIf(_disposed, this);
        GraphRecording recording = builder.Consume(this);
        if (!ReferenceEquals(recording, _active)) throw new ArgumentException("The builder is not the active recording.", nameof(builder));
        _active = null;

        DeviceCompilationSnapshot compilation = _device.Compilation;
        FrozenGraph frozen = recording.Freeze(_device);
        if (_device.Compilation.SemanticGeneration != compilation.SemanticGeneration)
        {
            throw new InvalidOperationException(
                "Device compilation semantics changed while the graph was being frozen; the consumed recording cannot be compiled against mixed generations.");
        }
        _cache.Drain();
        CompiledGraphLease lease = _cache.Acquire(frozen, compilation);
        int recordUnitCount = lease.Graph.RecordUnits.Length;
        int executionBatchCount = lease.Graph.ExecutionBatches.Length;
        CompiledAliasingStatistics aliasing = lease.Graph.Aliasing;
        _lastAliasing = new RenderGraphAliasingStatistics(
            aliasing.Enabled,
            aliasing.LogicalRequestedBytes,
            aliasing.NonAliasedPlacedBytes,
            aliasing.PlannedHeapBytes,
            aliasing.AliasSavingsBytes,
            aliasing.AliasSlotCount,
            aliasing.AliasAcquireCount);
        CompiledRasterStatistics raster = lease.Graph.Raster;
        _lastRaster = new RenderGraphRasterStatistics(
            raster.Enabled,
            raster.LiveRasterPasses,
            raster.CandidateScopes,
            raster.MergedLogicalPasses,
            raster.RecordUnitCount,
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.NonRaster],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.Queue],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.RecordingLane],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.ExtentOrSamples],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.AttachmentSet],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.LoadAction],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.DepthStencilMode],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.Barrier],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.AliasAcquire],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.CrossQueueSynchronization],
            raster.BreakReasonCounts[(int)RasterMergeBreakReason.ExternalReadiness]);
        CompiledCullingStatistics culling = lease.Graph.Culling;
        _lastCulling = new RenderGraphCullingStatistics(
            culling.DeclaredPasses,
            culling.LivePasses,
            culling.CulledPasses,
            culling.DeclaredResources,
            culling.LiveResources,
            culling.CulledResources,
            culling.DeclaredViews,
            culling.LiveViews,
            culling.CulledViews,
            culling.CulledTransientBytes,
            culling.ImportedWriteRoots);

        GraphInvocation invocation = GraphInvocation.Realize(_device, frozen, lease);
        GpuCompletion[] completions = invocation.RecordAndSubmit();
        _recordedCommandLists += recordUnitCount;
        _submissions += executionBatchCount;
        return new GraphExecution(_device, completions);
    }

    internal void Abandon(GraphRecording recording)
    {
        EnsureCoordinator();
        if (ReferenceEquals(_active, recording)) _active = null;
    }

    internal BufferMetadata GetBufferMetadata(BufferHandle buffer) => _device.GetBufferMetadata(buffer);
    internal TextureMetadata GetTextureMetadata(TextureHandle texture) => _device.GetTextureMetadata(texture);

    public void Dispose()
    {
        if (_disposed) return;
        EnsureCoordinator();
        if (_active is not null) throw new InvalidOperationException("Dispose the active GraphBuilder before disposing RenderGraph.");
        _cache.Dispose();
        _disposed = true;
    }

    private void OnCompilationEvent(CompilationEvent value)
    {
        switch (value)
        {
            case CompilationEvent.CacheHit: _cacheHits++; break;
            case CompilationEvent.CacheMiss: _cacheMisses++; break;
            case CompilationEvent.ConservativePlanCompiled: _conservativeCompilations++; break;
            case CompilationEvent.ConservativePlanSelected: _conservativePlanSelections++; break;
            case CompilationEvent.OptimizedPlanSelected: _optimizedPlanSelections++; break;
            case CompilationEvent.FlightStarted: _optimizedFlights++; break;
            case CompilationEvent.SingleFlightJoin: _singleFlightJoins++; break;
            case CompilationEvent.CandidatePublished: _publishedCandidates++; break;
            case CompilationEvent.CandidateDropped: _droppedCandidates++; break;
            case CompilationEvent.CandidateFailed: _failedCandidates++; break;
            case CompilationEvent.EntryEvicted: _cacheEvictions++; break;
            case CompilationEvent.EntryRetired: _cacheRetirements++; break;
        }
    }

    private void EnsureCoordinator()
    {
        if (Environment.CurrentManagedThreadId != _coordinatorThread)
            throw new InvalidOperationException("RenderGraph authoring, publication, realization, and submission have one coordinator thread owner.");
    }
}

public sealed record RenderGraphOptions
{
    /// <summary>
    /// Target maximum number of exact reusable plans visible to lookup. Zero disables retention.
    /// An entry with an active invocation lease may temporarily exceed a positive limit until the
    /// invocation finishes recording and submission.
    /// </summary>
    public int CompilationCacheEntryLimit { get; init; } = 128;

    /// <summary>
    /// Maximum deterministic estimated bytes visible to lookup. Zero disables retention, and an
    /// individually oversized plan is never admitted. Active CPU/recording leases may temporarily
    /// put the aggregate admitted set over budget until the invocation releases them.
    /// </summary>
    public long CompilationCachePayloadByteBudget { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Starts one detached optimized compilation per exact miss key when an optimized transform is
    /// enabled. Publication remains coordinator-owned and can only affect a later invocation.
    /// </summary>
    public bool CompileOptimizedPlansAsynchronously { get; init; } = true;

    /// <summary>
    /// Allows optimized plans to reuse placed-resource ranges when the existing execution DAG
    /// proves non-overlapping lifetimes. Disabled until representative game workloads establish
    /// the memory/CPU/GPU trade-off; conservative plans always retain no-alias placement.
    /// </summary>
    public bool EnableTransientAliasing { get; init; }

    /// <summary>
    /// Allows optimized plans to combine compatible adjacent raster passes into one native
    /// rendering scope. Disabled until representative renderer workloads justify the trade-off.
    /// </summary>
    public bool EnableRenderPassMerging { get; init; }

    /// <summary>
    /// Receives optional optimized-compilation diagnostics on the render coordinator. Consumer
    /// failures are isolated from plan selection so a valid conservative resident remains usable.
    /// </summary>
    public Action<RenderGraphCompilationDiagnostic>? CompilationDiagnosticSink { get; init; }
}

public enum CompilationFailureStage : byte
{
    Scheduling,
    OptimizedCompilation,
    ResultContract,
}

public sealed record RenderGraphCompilationDiagnostic(
    string CanonicalSignature,
    CompilationFailureStage Stage,
    IReadOnlyList<string> PassNames,
    Exception Exception);

public readonly record struct RenderGraphStatistics(
    long Recordings,
    long ConservativeCompilations,
    long ConservativePlanSelections,
    long OptimizedPlanSelections,
    long CacheHits,
    long CacheMisses,
    long OptimizedFlightsStarted,
    long SingleFlightJoins,
    long CandidatePublications,
    long CandidateDrops,
    long CandidateFailures,
    long CacheEvictions,
    long CacheRetirements,
    int ResidentCacheEntries,
    long ResidentCachePayloadBytes,
    int RetiringCacheEntries,
    long RetiringCachePayloadBytes,
    long CommandListsRecorded,
    long Submissions,
    RenderGraphAliasingStatistics LastAliasing,
    RenderGraphRasterStatistics LastRaster,
    RenderGraphCullingStatistics LastCulling)
{
    public long TotalCachePayloadBytes => checked(ResidentCachePayloadBytes + RetiringCachePayloadBytes);
}

public readonly record struct RenderGraphAliasingStatistics(
    bool Enabled,
    ulong LogicalRequestedBytes,
    ulong NonAliasedPlacedBytes,
    ulong PlannedHeapBytes,
    ulong AliasSavingsBytes,
    int AliasSlotCount,
    int AliasAcquireCount);

public readonly record struct RenderGraphRasterStatistics(
    bool Enabled,
    int LiveRasterPasses,
    int CandidateScopes,
    int MergedLogicalPasses,
    int RecordUnitCount,
    int NonRasterBreaks,
    int QueueBreaks,
    int RecordingLaneBreaks,
    int ExtentOrSampleBreaks,
    int AttachmentSetBreaks,
    int LoadActionBreaks,
    int DepthStencilModeBreaks,
    int BarrierBreaks,
    int AliasAcquireBreaks,
    int CrossQueueSynchronizationBreaks,
    int ExternalReadinessBreaks);

public readonly record struct RenderGraphCullingStatistics(
    int DeclaredPasses,
    int LivePasses,
    int CulledPasses,
    int DeclaredResources,
    int LiveResources,
    int CulledResources,
    int DeclaredViews,
    int LiveViews,
    int CulledViews,
    ulong CulledTransientBytes,
    int ImportedWriteRoots);
