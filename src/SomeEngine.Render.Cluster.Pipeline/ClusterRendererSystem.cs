using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Composes Cluster geometry with independently published lighting and presentation features.
/// </summary>
public sealed partial class ClusterRendererSystem : ISystem<RenderFrameSystemContext>
{
    private const int ReadbackGenerationCount = 2;
    private static readonly TimeSpan ReadbackRetirementTimeout = TimeSpan.FromSeconds(30);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly ClusterRenderResources _resources;
    private readonly IRenderInstanceBatchSource<RenderInstanceSingleGroup> _instances;
    private readonly RenderInstancePropertyLayout _instanceLayout;
    private readonly ClusterMaterialTable _materialTable;
    private readonly ClusterShaders _configuration;
    private readonly ClusterRenderTargetMailbox _targetMailbox;
    private readonly RenderLightSetMailbox _lightMailbox;
    private readonly ClusterPipelineOptions _options;
    private QueryHandle _viewQuery;
    private ClusterPipelineSet? _pipelines;
    private ClusterRenderHistory? _history;
    private readonly List<ClusterRenderHistory> _histories = [];
    private IndirectCommandLayout? _dispatchIndirectLayout;
    private IndirectCommandLayout? _drawIndirectLayout;
    private readonly Buffer?[] _pageFaultReadbacks = new Buffer?[ReadbackGenerationCount];
    private byte[] _pageFaultReadbackBytes = [];
    private readonly ClusterEpochId[] _pageFaultReadbackEpochs =
        new ClusterEpochId[ReadbackGenerationCount];
    private readonly bool[] _pageFaultReadbackPending = new bool[ReadbackGenerationCount];
    private readonly Buffer?[] _frameMetricReadbacks = new Buffer?[ReadbackGenerationCount];
    private byte[] _frameMetricReadbackBytes = [];
    private readonly ulong[] _frameMetricReadbackFrames = new ulong[ReadbackGenerationCount];
    private readonly bool[] _frameMetricReadbackPending = new bool[ReadbackGenerationCount];
    private readonly QueueCompletion[][] _readbackFences = [[], []];
    private readonly ulong[] _readbackSequences = new ulong[ReadbackGenerationCount];
    private readonly ViewCollector _viewCollector = new();
    private GpuLightBufferPool? _gpuLightBuffers;
    private Buffer? _lightCountsBuffer;
    private Buffer? _lightGridBuffer;
    private Buffer? _lightIndicesBuffer;
    private Buffer? _lightGridUniformsBuffer;
    private int _lightStructureWidth;
    private int _lightStructureHeight;
    private int _lightStructureDirectionalCount = -1;
    private int _lightStructurePointCount = -1;
    private int _lightStructureSpotCount = -1;
    private ulong _frameMetricsSubmittedFrame;
    private ulong _nextReadbackSequence;
    private int _readbackWriteGeneration = -1;
    private int _preferredReadbackGeneration;
    private ClusterFrameMetrics? _latestFrameMetrics;
    private Format _outputFormat;
    private ClusterEpochId _pendingReadbackEpoch;
    private bool _hasPendingFrame;
    private int _pendingHistoryCount;
    private bool _created;

    public ClusterRendererSystem(
        IGraphicsBackend backend,
        Device device,
        ClusterRenderResources resources,
        IRenderInstanceBatchSource<RenderInstanceSingleGroup> instances,
        RenderInstancePropertyLayout instanceLayout,
        ClusterMaterialTable materialTable,
        ClusterShaders configuration,
        ClusterRenderTargetMailbox targetMailbox,
        RenderLightSetMailbox lightMailbox,
        ClusterPipelineOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _instanceLayout = instanceLayout ?? throw new ArgumentNullException(nameof(instanceLayout));
        _materialTable = materialTable ?? throw new ArgumentNullException(nameof(materialTable));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _targetMailbox = targetMailbox ?? throw new ArgumentNullException(nameof(targetMailbox));
        _lightMailbox = lightMailbox ?? throw new ArgumentNullException(nameof(lightMailbox));
        _options = options ?? new ClusterPipelineOptions();
    }

    public void OnCreate(ref RenderFrameSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The full Cluster renderer is already created.");
        _options.Validate();

        QueryHandle view = default;
        try
        {
            view = context.World.Query(new QueryDefinitionBuilder().Read<RenderView>());
            _viewQuery = view;
            _gpuLightBuffers = new GpuLightBufferPool(
                _backend,
                _device,
                ReadbackGenerationCount);
            _created = true;
        }
        catch
        {
            _gpuLightBuffers?.Dispose();
            _gpuLightBuffers = null;
            ReleaseIfValid(context.World, view);
            throw;
        }
    }

    internal int AdmitFrameResources()
    {
        if (_hasPendingFrame)
            throw new InvalidOperationException("The previous Cluster frame has not been committed or discarded.");
        if (_readbackWriteGeneration >= 0)
            return 1;
        _readbackWriteGeneration =
            AcquireReadbackGeneration(out int availableGenerationCount);
        return availableGenerationCount;
    }

    internal bool TryAdmitFrameResources(
        out int availableGenerationCount,
        out QueueCompletion[] retirementFences)
    {
        if (_hasPendingFrame)
            throw new InvalidOperationException("The previous Cluster frame has not been committed or discarded.");
        if (_readbackWriteGeneration >= 0)
        {
            availableGenerationCount = 1;
            retirementFences = [];
            return true;
        }
        if (!TryAcquireReadbackGeneration(
                out int generation,
                out availableGenerationCount,
                out retirementFences))
        {
            return false;
        }
        _readbackWriteGeneration = generation;
        return true;
    }

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        if (!_created)
            throw new InvalidOperationException("The full Cluster renderer was not created.");
        if (_hasPendingFrame)
            throw new InvalidOperationException("The previous Cluster frame has not been committed or discarded.");
        ClusterRenderTarget target = _targetMailbox.TakeRequired();
        EnsureRendererEpoch(in target);
        _resources.PumpStreaming();

        RenderInstanceBatches<RenderInstanceSingleGroup>? current = _instances.Current;
        if (current is null || current.GroupCount != 1)
        {
            throw new InvalidOperationException(
                "The full Cluster renderer requires one unclassified instance batch.");
        }
        RenderInstanceBatch batch = current.Groups[0].Batch;
        ClusterMaterialSnapshot materialSnapshot = _materialTable.Current;
        if (materialSnapshot.MaterialCount == 0)
            throw new InvalidOperationException("The Cluster scene has no published materials.");

        ViewCollector viewCollector = _viewCollector;
        RenderLightSet lights = _lightMailbox.TakeRequired();
        viewCollector.Clear();
        try
        {
            context.World.ExecuteQuery(
                _viewQuery,
                ref viewCollector,
                static (QueryCursor cursor, ref ViewCollector state) => state.Collect(cursor));
            if (viewCollector.Views.Count == 0)
            {
                throw new InvalidOperationException(
                    "A Cluster frame requires at least one render view.");
            }
            if (checked((uint)viewCollector.Views.Count) > target.ArrayLayerCount)
            {
                throw new InvalidOperationException(
                    "The Cluster target does not have an array layer for every render view.");
            }
            foreach (RenderView view in viewCollector.Views)
                if (view.ViewportWidth != target.Width || view.ViewportHeight != target.Height)
                {
                    throw new InvalidOperationException(
                        "Every render-view viewport must match the acquired Cluster target dimensions.");
                }
            EnsureHistoryCount(viewCollector.Views.Count);

            using ClusterRenderBinding binding = _resources.Use(
                context.ActiveFrame,
                batch,
                _instanceLayout);
            EnsurePageFaultReadback(binding.PageFaultCapacity);
            EnsureFrameMetricsReadback();
            if (_readbackWriteGeneration < 0)
                _readbackWriteGeneration = AcquireReadbackGeneration();
            var viewUniforms = new ClusterViewUniforms[viewCollector.Views.Count];
            int historyPrepared = 0;
            try
            {
                for (int viewIndex = 0; viewIndex < viewCollector.Views.Count; viewIndex++)
                {
                    RenderView view = viewCollector.Views[viewIndex];
                    ClusterRenderHistory history = _histories[viewIndex];
                    _history = history;
                    ClusterViewUniforms uniforms = ClusterViewUniforms.Create(
                        in view,
                        _options,
                        _pipelines!.CullingEnabled,
                        binding.DispatchExtent,
                        binding.PageFaultCapacity);
                    bool hasHistory = history.Prepare(
                        target.Width,
                        target.Height,
                        in view);
                    historyPrepared++;
                    if (hasHistory)
                    {
                        Matrix4x4 previousView = history.PreviousView;
                        Matrix4x4 previousProjection = history.PreviousProjection;
                        uniforms.PrevViewProj = previousView * previousProjection;
                        uniforms.HasPrevHistory = 1;
                        uniforms.HiZMipCount = checked((uint)history.HiZMipCount);
                        uniforms.HiZInvSize = new Vector2(
                            1.0f / target.Width,
                            1.0f / target.Height);
                        uniforms.PrevView = previousView;
                        uniforms.PrevP00 = previousProjection.M11;
                        uniforms.PrevP11 = previousProjection.M22;
                    }
                    viewUniforms[viewIndex] = uniforms;
                }

                _pendingReadbackEpoch = binding.ReadbackEpoch;
                _pendingHistoryCount = historyPrepared;
                _hasPendingFrame = true;

                RenderGraphFrame graph = context.Graph;
                RecordFrame(
                    ref graph,
                    in target,
                    in binding,
                    materialSnapshot,
                    CollectionsMarshal.AsSpan(viewCollector.Views),
                    viewUniforms,
                    lights);
            }
            catch
            {
                for (int index = 0; index < historyPrepared; index++)
                    _histories[index].Discard();
                ClearPendingFrame();
                throw;
            }
        }
        finally
        {
            viewCollector.Clear();
            lights.Clear();
        }
    }

    /// <summary>Publishes temporal state only after the authored graph submitted successfully.</summary>
    public void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        if (!_hasPendingFrame)
            throw new InvalidOperationException("No authored Cluster frame is waiting for commit.");
        int generation = RequireReadbackWriteGeneration();
        QueueCompletion[] fences = _readbackFences[generation];
        if (fences.Length != completions.Length)
            fences = new QueueCompletion[completions.Length];
        completions.CopyTo(fences);
        ulong readbackSequence = checked(_nextReadbackSequence + 1);
        ulong frameMetricsSequence = _options.EnableFrameMetricsReadback
            ? checked(_frameMetricsSubmittedFrame + 1)
            : _frameMetricsSubmittedFrame;
        for (int index = 0; index < _pendingHistoryCount; index++)
            _histories[index].Commit(fences);
        _readbackFences[generation] = fences;
        _nextReadbackSequence = readbackSequence;
        _readbackSequences[generation] = readbackSequence;
        _pageFaultReadbackEpochs[generation] = _pendingReadbackEpoch;
        _pageFaultReadbackPending[generation] = true;
        if (_options.EnableFrameMetricsReadback)
        {
            _frameMetricsSubmittedFrame = frameMetricsSequence;
            _frameMetricReadbackFrames[generation] = frameMetricsSequence;
            _frameMetricReadbackPending[generation] = true;
        }
        _preferredReadbackGeneration = 1 - generation;
        ClearPendingFrame();
    }

    /// <summary>Rejects authoring state when graph compilation, recording, or submission fails.</summary>
    public void Discard()
    {
        if (!_hasPendingFrame) return;
        for (int index = 0; index < _pendingHistoryCount; index++)
            _histories[index].Discard();
        ClearPendingFrame();
    }

    private void ClearPendingFrame()
    {
        _pendingReadbackEpoch = default;
        _pendingHistoryCount = 0;
        _readbackWriteGeneration = -1;
        _hasPendingFrame = false;
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;
        List<Exception>? failures = null;
        Release(ref _viewQuery, context.World, ref failures);
        Dispose(ref _pipelines, ref failures);
        _history = null;
        for (int index = _histories.Count - 1; index >= 0; index--)
        {
            try { _histories[index].Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _histories.Clear();
        Dispose(ref _dispatchIndirectLayout, ref failures);
        Dispose(ref _drawIndirectLayout, ref failures);
        for (int generation = 0; generation < ReadbackGenerationCount; generation++)
        {
            if (_pageFaultReadbacks[generation] is { } pageFaultReadback)
            {
                try { pageFaultReadback.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            _pageFaultReadbacks[generation] = null;
            _pageFaultReadbackEpochs[generation] = default;
            _pageFaultReadbackPending[generation] = false;

            if (_frameMetricReadbacks[generation] is { } frameMetricReadback)
            {
                try { frameMetricReadback.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            _frameMetricReadbacks[generation] = null;
            _frameMetricReadbackFrames[generation] = 0;
            _frameMetricReadbackPending[generation] = false;
            _readbackFences[generation] = [];
            _readbackSequences[generation] = 0;

        }
        Dispose(ref _gpuLightBuffers, ref failures);
        Dispose(ref _lightCountsBuffer, ref failures);
        Dispose(ref _lightGridBuffer, ref failures);
        Dispose(ref _lightIndicesBuffer, ref failures);
        Dispose(ref _lightGridUniformsBuffer, ref failures);
        _lightStructureWidth = 0;
        _lightStructureHeight = 0;
        _lightStructureDirectionalCount = -1;
        _lightStructurePointCount = -1;
        _lightStructureSpotCount = -1;
        _pageFaultReadbackBytes = [];
        _frameMetricReadbackBytes = [];
        _frameMetricsSubmittedFrame = 0;
        _nextReadbackSequence = 0;
        _readbackWriteGeneration = -1;
        _preferredReadbackGeneration = 0;
        _latestFrameMetrics = null;
        ClearPendingFrame();
        _created = false;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private void EnsureRendererEpoch(in ClusterRenderTarget target)
    {
        if (_pipelines is not null)
        {
            if (_outputFormat != target.Format)
            {
                throw new InvalidOperationException(
                    "The presentation format changed inside a live Cluster renderer epoch.");
            }
            return;
        }

        ClusterPipelineSet? pipelines = null;
        IndirectCommandLayout? dispatchIndirectLayout = null;
        IndirectCommandLayout? drawIndirectLayout = null;
        IndirectArgumentDesc[] dispatchArguments =
            [new(IndirectArgumentType.Dispatch)];
        IndirectArgumentDesc[] drawArguments =
            [new(IndirectArgumentType.Draw)];
        try
        {
            pipelines = new ClusterPipelineSet(
                _backend,
                _device,
                _configuration,
                target.Format);
            dispatchIndirectLayout = _backend.CreateIndirectCommandLayout(
                _device,
                new IndirectCommandLayoutDesc(
                    dispatchArguments,
                    ClusterIndirectAbi.DispatchStride,
                    label: "Cluster dispatch indirect layout"));
            drawIndirectLayout = _backend.CreateIndirectCommandLayout(
                _device,
                new IndirectCommandLayoutDesc(
                    drawArguments,
                    ClusterIndirectAbi.DrawStride,
                    label: "Cluster draw indirect layout"));
            _pipelines = pipelines;
            _dispatchIndirectLayout = dispatchIndirectLayout;
            _drawIndirectLayout = drawIndirectLayout;
            _outputFormat = target.Format;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            Dispose(ref drawIndirectLayout, ref cleanupFailures);
            Dispose(ref dispatchIndirectLayout, ref cleanupFailures);
            Dispose(ref pipelines, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    "Cluster renderer-epoch construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private void EnsureHistoryCount(int count)
    {
        while (_histories.Count < count)
        {
            var history = new ClusterRenderHistory(_backend, _device);
            _histories.Add(history);
        }
    }

    private void EnsurePageFaultReadback(int capacity)
    {
        int byteCount = checked(sizeof(uint) + capacity * sizeof(uint));
        if (_pageFaultReadbacks[0] is not null)
        {
            if (_pageFaultReadbackBytes.Length != byteCount)
            {
                throw new InvalidOperationException(
                    "The Cluster page-fault capacity changed inside a live renderer epoch.");
            }
            return;
        }

        _pageFaultReadbackBytes = CreateReadbackBuffers(
            _pageFaultReadbacks,
            byteCount,
            "Cluster page-fault readback");
    }

    private int AcquireReadbackGeneration() =>
        AcquireReadbackGeneration(out _);

    private int AcquireReadbackGeneration(out int availableGenerationCount)
    {
        if (TryAcquireReadbackGeneration(
                out int generation,
                out availableGenerationCount,
                out _))
        {
            return generation;
        }

        int preferred = _preferredReadbackGeneration;
        int alternate = 1 - preferred;
        int oldest = _readbackSequences[preferred] <= _readbackSequences[alternate]
            ? preferred
            : alternate;
        if (!WaitForAll(_readbackFences[oldest], ReadbackRetirementTimeout))
        {
            throw new TimeoutException(
                $"Cluster readback generation {oldest} did not retire within {ReadbackRetirementTimeout}.");
        }
        DrainCompletedReadbacks();
        if (_pageFaultReadbackPending[oldest])
            throw new InvalidOperationException("A completed Cluster readback generation was not reclaimed.");
        availableGenerationCount =
            (_pageFaultReadbackPending[0] ? 0 : 1) +
            (_pageFaultReadbackPending[1] ? 0 : 1);
        return oldest;
    }

    private bool TryAcquireReadbackGeneration(
        out int generation,
        out int availableGenerationCount,
        out QueueCompletion[] retirementFences)
    {
        DrainCompletedReadbacks();
        int preferred = _preferredReadbackGeneration;
        int alternate = 1 - preferred;
        bool preferredAvailable = !_pageFaultReadbackPending[preferred];
        bool alternateAvailable = !_pageFaultReadbackPending[alternate];
        availableGenerationCount =
            (preferredAvailable ? 1 : 0) + (alternateAvailable ? 1 : 0);
        if (preferredAvailable)
        {
            generation = preferred;
            retirementFences = [];
            return true;
        }
        if (alternateAvailable)
        {
            generation = alternate;
            retirementFences = [];
            return true;
        }

        int oldest = _readbackSequences[preferred] <= _readbackSequences[alternate]
            ? preferred
            : alternate;
        generation = -1;
        retirementFences = _readbackFences[oldest];
        if (retirementFences.Length == 0)
        {
            throw new InvalidOperationException(
                "An unavailable Cluster readback generation has no retirement position.");
        }
        return false;
    }

    private int RequireReadbackWriteGeneration()
    {
        if ((uint)_readbackWriteGeneration >= ReadbackGenerationCount)
        {
            throw new InvalidOperationException(
                "Cluster readback storage has no admitted write generation.");
        }
        return _readbackWriteGeneration;
    }

    private void DrainCompletedReadbacks()
    {
        while (TryGetOldestPendingReadback(out int generation))
        {
            QueueCompletion[] readiness = _readbackFences[generation];
            if (readiness.Length != 0 && !WaitForAll(readiness, TimeSpan.Zero))
                return;

            Buffer pageFaultReadback =
                _pageFaultReadbacks[generation] is { } value
                    ? value
                    : throw new InvalidOperationException(
                        "Cluster page-fault readback ownership was lost.");
            ClusterEpochId epoch = _pageFaultReadbackEpochs[generation];
            if (!epoch.IsValid)
                throw new InvalidOperationException("Cluster page-fault readback epoch was lost.");
            ReadMappedBuffer(pageFaultReadback, _pageFaultReadbackBytes);
            _resources.IngestPageFaultReadback(epoch, _pageFaultReadbackBytes);
            _pageFaultReadbackEpochs[generation] = default;
            _pageFaultReadbackPending[generation] = false;

            if (_frameMetricReadbackPending[generation])
                DrainFrameMetricsReadback(generation);
        }
    }

    private bool TryGetOldestPendingReadback(out int generation)
    {
        generation = -1;
        ulong sequence = ulong.MaxValue;
        for (int candidate = 0; candidate < ReadbackGenerationCount; candidate++)
        {
            if (!_pageFaultReadbackPending[candidate]
                || _readbackSequences[candidate] >= sequence)
            {
                continue;
            }
            generation = candidate;
            sequence = _readbackSequences[candidate];
        }
        return generation >= 0;
    }

    /// <summary>
    /// Reads counters from the latest frame. The caller must have completed its GPU frame before
    /// requesting CPU-visible diagnostics.
    /// </summary>
    public ClusterFrameMetrics CaptureFrameMetrics()
    {
        if (!_created || !_options.EnableFrameMetricsReadback)
        {
            throw new InvalidOperationException(
                "Cluster frame metrics are not enabled for this renderer epoch.");
        }
        DrainCompletedReadbacks();
        return _latestFrameMetrics ?? throw new InvalidOperationException(
            "No completed Cluster frame metrics are available.");
    }

    private void EnsureFrameMetricsReadback()
    {
        if (!_options.EnableFrameMetricsReadback
            || _frameMetricReadbacks[0] is not null)
            return;
        _frameMetricReadbackBytes = CreateReadbackBuffers(
            _frameMetricReadbacks,
            FrameMetricsReadbackByteSize,
            "Cluster frame metrics readback");
    }

    private byte[] CreateReadbackBuffers(
        Buffer?[] slots,
        int byteCount,
        string label)
    {
        if (slots.Length != ReadbackGenerationCount || slots[0] is not null || slots[1] is not null)
            throw new InvalidOperationException("Cluster readback slots are not empty.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var bytes = new byte[byteCount];
        Buffer? first = null;
        Buffer? second = null;
        try
        {
            first = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    checked((ulong)byteCount),
                    BufferUsages.CopyDestination,
                    $"{label} 0"),
                MemoryType.Readback);
            second = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    checked((ulong)byteCount),
                    BufferUsages.CopyDestination,
                    $"{label} 1"),
                MemoryType.Readback);
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            Dispose(ref second, ref cleanupFailures);
            Dispose(ref first, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"{label} creation failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }

        slots[0] = first;
        slots[1] = second;
        return bytes;
    }

    private void DrainFrameMetricsReadback(int generation)
    {
        if (!_frameMetricReadbackPending[generation])
            return;
        Buffer frameMetricReadback = _frameMetricReadbacks[generation] is { } value
            ? value
            : throw new InvalidOperationException("Cluster frame-metrics readback ownership was lost.");
        ReadMappedBuffer(frameMetricReadback, _frameMetricReadbackBytes);
        ReadOnlySpan<byte> bytes = _frameMetricReadbackBytes;
        _latestFrameMetrics = new ClusterFrameMetrics(
            _frameMetricReadbackFrames[generation],
            ReadUInt32(bytes, CandidateCountReadbackOffset),
            ReadUInt32(bytes, CandidateArgsReadbackOffset),
            ReadUInt32(bytes, DrawArgsReadbackOffset + sizeof(uint)),
            ReadUInt32(bytes, DrawArgsReadbackOffset + 2 * sizeof(uint)),
            ReadUInt32(bytes, Phase2CandidateCountReadbackOffset),
            ReadUInt32(bytes, Phase2CandidateArgsReadbackOffset),
            ReadUInt32(bytes, Phase2DrawArgsReadbackOffset + sizeof(uint)),
            ReadUInt32(bytes, Phase2DrawArgsReadbackOffset + 2 * sizeof(uint)),
            ReadUInt32(bytes, RasterReserveReadbackOffset),
            ReadUInt32(bytes, RasterReserveReadbackOffset + sizeof(uint)),
            ReadUInt32(bytes, ShadeBinCountReadbackOffset),
            ReadUInt32(bytes, ShadeReserveReadbackOffset),
            ReadUInt32(bytes, DeformReserveReadbackOffset),
            ReadUInt32(bytes, CachedDeformClustersReadbackOffset),
            ReadUInt32(bytes, CacheAllocationReadbackOffset),
            _options.DeformCacheBytes,
            ReadUInt32(bytes, SoftwareDebugReadbackOffset),
            CountNonZeroVisibilityProbe(bytes));
        _frameMetricReadbackFrames[generation] = 0;
        _frameMetricReadbackPending[generation] = false;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    private static uint CountNonZeroVisibilityProbe(ReadOnlySpan<byte> bytes)
    {
        uint result = 0;
        for (int index = 0; index < VisibilityProbePixelCount; index++)
            if (ReadUInt32(bytes, VisibilityProbeReadbackOffset + index * sizeof(uint)) != 0)
                result++;
        return result;
    }

    private void ReadMappedBuffer(Buffer source, Span<byte> destination)
    {
        BufferRange range = new(0, checked((ulong)destination.Length));
        using MappedBuffer mapping = _backend.Map(source, MapType.Read, range);
        mapping.Invalidate(range);
        mapping.Bytes.CopyTo(destination);
    }

    private bool WaitForAll(ReadOnlySpan<QueueCompletion> completions, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (timeout == TimeSpan.Zero)
        {
            foreach (ref readonly QueueCompletion completion in completions)
            {
                if (!_backend.IsComplete(completion))
                    return false;
            }
            return true;
        }

        long started = Environment.TickCount64;
        foreach (ref readonly QueueCompletion completion in completions)
        {
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            if (_backend.WaitCpu(completion, remaining) != WaitStatus.Completed)
                return false;
        }
        return true;
    }

    private IndirectCommandLayout RequireDispatchIndirectLayout() =>
        _dispatchIndirectLayout ?? throw new InvalidOperationException(
            "The Cluster dispatch indirect layout is unavailable.");

    private IndirectCommandLayout RequireDrawIndirectLayout() =>
        _drawIndirectLayout ?? throw new InvalidOperationException(
            "The Cluster draw indirect layout is unavailable.");

    private static void ReleaseIfValid(RenderWorld world, QueryHandle query)
    {
        if (query.IsValid)
            world.ReleaseQuery(query);
    }

    private static void Release(
        ref QueryHandle query,
        RenderWorld world,
        ref List<Exception>? failures)
    {
        if (!query.IsValid)
            return;
        try { world.ReleaseQuery(query); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        query = default;
    }

    private static void Dispose<T>(ref T? value, ref List<Exception>? failures)
        where T : class, IDisposable
    {
        if (value is null)
            return;
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        value = null;
    }

    private sealed class ViewCollector
    {
        internal List<RenderView> Views { get; } = [];

        internal void Clear() => Views.Clear();

        internal void Collect(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
                Views.Add(row.Read<RenderView>());
        }
    }

}
