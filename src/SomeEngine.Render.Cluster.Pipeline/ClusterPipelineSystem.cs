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
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>
/// Installs and updates one independently instantiable retained Cluster geometry pipeline.
/// </summary>
public sealed partial class ClusterPipelineSystem : ISystem<RenderFrameSystemContext>
{
    private const int ReadbackGenerationCount = 2;
    private static readonly TimeSpan ReadbackRetirementTimeout = TimeSpan.FromSeconds(30);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly RenderDevice _renderDevice;
    private readonly ClusterRenderResources _resources;
    private readonly IRenderInstanceBatchSource<RenderInstanceSingleGroup> _instances;
    private readonly RenderInstancePropertyLayout _instanceLayout;
    private readonly ClusterMaterialAssignments _materialAssignments;
    private readonly ClusterShaders _configuration;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly ClusterPipelineOutputDescription _outputDescription;
    private readonly ClusterRasterExecutor _rasterExecutor;
    private readonly ClusterPipelineOptions _options;
    private QueryHandle _viewQuery;
    private ClusterPipelineSet? _pipelines;
    private ClusterDeformStages? _deformStages;
    private ClusterRasterStages? _rasterStages;
    private ClusterShadeStages? _shadeStages;
    private ClusterDeformStageSet? _installedDeformStages;
    private ClusterRasterStageSet? _installedRasterStages;
    private ClusterShadeStageSet? _installedShadeStages;
    private GraphPassId[] _shadePasses = [];
    private ClusterCullingHistory? _history;
    private ClusterFrameUploads? _uploads;
    private FrameResources _frameResources;
    private readonly List<GraphPassId> _historyInitializationPasses = [];
    private readonly List<ClusterLayerDispatch> _layerDispatches = [];
    private readonly List<GraphPassId> _depthMergePasses = [];
    private readonly List<GraphPassId> _pageHeapReaderPasses = [];
    private readonly ClusterViewUniforms[] _packedViews;
    private GraphPassId _traversalPass;
    private ClusterPipelineOutputs _outputs;
    private ulong _installedMaterialTopologyVersion;
    private bool _cullingEnabled;
    private bool _graphInstalled;
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
    private ulong _frameMetricsSubmittedFrame;
    private ulong _nextReadbackSequence;
    private int _readbackWriteGeneration = -1;
    private int _preferredReadbackGeneration;
    private ClusterFrameMetrics? _latestFrameMetrics;
    private ClusterEpochId _pendingReadbackEpoch;
    private ClusterResidencyPublication? _pendingResidencyPublication;
    private BufferRange[] _pendingResidentPageRanges = [];
    private bool _bindingScratchReadsPageHeap;
    private bool _hasPendingFrame;
    private bool _created;

    public ClusterPipelineSystem(
        IGraphicsBackend backend,
        Device device,
        RenderDevice renderDevice,
        ClusterRenderResources resources,
        IRenderInstanceBatchSource<RenderInstanceSingleGroup> instances,
        RenderInstancePropertyLayout instanceLayout,
        ClusterMaterialAssignments materialAssignments,
        ClusterShaders configuration,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        in ClusterPipelineOutputDescription outputDescription,
        ClusterRasterExecutor rasterExecutor,
        ClusterPipelineOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _renderDevice = renderDevice ?? throw new ArgumentNullException(nameof(renderDevice));
        if (!ReferenceEquals(renderDevice.Device, device))
            throw new ArgumentException("The Cluster pipeline and RenderDevice must use the same Device.", nameof(renderDevice));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _instanceLayout = instanceLayout ?? throw new ArgumentNullException(nameof(instanceLayout));
        _materialAssignments = materialAssignments ?? throw new ArgumentNullException(nameof(materialAssignments));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("The Cluster pipeline and RenderGraph must use the same Device.", nameof(graph));
        _outputDescription = outputDescription;
        _rasterExecutor = rasterExecutor ?? throw new ArgumentNullException(nameof(rasterExecutor));
        _options = options ?? new ClusterPipelineOptions();
        _packedViews = new ClusterViewUniforms[checked((int)outputDescription.ViewCapacity)];
    }

    public ClusterPipelineOutputs Outputs => _outputs.IsValid
        ? _outputs
        : throw new InvalidOperationException("The Cluster retained graph has not been installed.");

    public void OnCreate(ref RenderFrameSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The Cluster pipeline is already created.");
        _options.Validate();

        QueryHandle view = default;
        try
        {
            view = context.World.Query(new QueryDefinitionBuilder().Read<RenderView>());
            _viewQuery = view;
            EnsurePipelineResources();
            _deformStages = new ClusterDeformStages(
                _backend,
                _device,
                _pipelines!.VertexEncoding);
            _rasterStages = new ClusterRasterStages(_pipelines!.HardwareRaster);
            _shadeStages = new ClusterShadeStages(
                _backend,
                _device,
                _pipelines.VertexEncoding);
            _history = new ClusterCullingHistory(
                _backend,
                _device,
                _outputDescription.ViewCapacity);
            _created = true;
        }
        catch
        {
            _history?.Dispose();
            _history = null;
            _shadeStages?.Dispose();
            _shadeStages = null;
            _deformStages?.Dispose();
            _deformStages = null;
            _rasterStages = null;
            _pipelines?.Dispose();
            _pipelines = null;
            _dispatchIndirectLayout?.Dispose();
            _dispatchIndirectLayout = null;
            _drawIndirectLayout?.Dispose();
            _drawIndirectLayout = null;
            ReleaseIfValid(context.World, view);
            throw;
        }
    }

    /// <summary>
    /// Installs this pipeline's long-lived resources, views, and passes. Call at a frame boundary
    /// after material preparation and before beginning the next RenderGraph frame.
    /// </summary>
    public ClusterPipelineOutputs ReconcileGraph()
    {
        if (!_created)
            throw new InvalidOperationException("The Cluster pipeline was not created.");
        _resources.RequireVertexEncoding(_pipelines!.VertexEncoding);
        ClusterMaterialAssignmentSet assignments = _materialAssignments.Current;
        if (assignments.MaterialCount == 0)
            throw new InvalidOperationException("The Cluster scene has no published materials.");
        if (_graphInstalled &&
            _installedMaterialTopologyVersion == assignments.TopologyVersion &&
            _installedShadeStages is { IsCurrent: true })
        {
            return _outputs;
        }
        ClusterDeformStageSet deformStages = (_deformStages ?? throw new InvalidOperationException(
            "The Cluster deform stages were not created.")).Resolve(assignments);
        ClusterRasterStageSet rasterStages = (_rasterStages ?? throw new InvalidOperationException(
            "The Cluster raster stages were not created.")).Resolve(assignments);
        ClusterShadeStageSet shadeStages = (_shadeStages ?? throw new InvalidOperationException(
            "The Cluster shade stages were not created.")).Resolve(assignments);
        bool cullingEnabled = _pipelines.CullingEnabled && deformStages.HasFiniteBounds;
        if (_graphInstalled)
        {
            if (_installedMaterialTopologyVersion != assignments.TopologyVersion ||
                _installedDeformStages is null ||
                !_installedDeformStages.HasSameTopology(deformStages) ||
                _installedShadeStages is null ||
                !_installedShadeStages.HasSameTopology(shadeStages))
            {
                throw new InvalidOperationException(
                    "Cluster material-stage topology changed after retained graph installation.");
            }
            _installedDeformStages = deformStages;
            _installedRasterStages = rasterStages;
            _installedShadeStages = shadeStages;
            _cullingEnabled = cullingEnabled;
            return _outputs;
        }

        var uploads = new ClusterFrameUploads(
            _backend,
            _device,
            ReadbackGenerationCount);
        try
        {
            _uploads = uploads;
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                InstallGraph(
                    ref edit,
                    in _outputDescription,
                    assignments,
                    deformStages,
                    rasterStages,
                    shadeStages);
                edit.Commit();
            }
            finally
            {
                edit.Dispose();
            }
            _installedMaterialTopologyVersion = assignments.TopologyVersion;
            _installedDeformStages = deformStages;
            _installedRasterStages = rasterStages;
            _installedShadeStages = shadeStages;
            _cullingEnabled = cullingEnabled;
            _graphInstalled = true;
            return _outputs;
        }
        catch
        {
            _uploads = null;
            uploads.Dispose();
            _historyInitializationPasses.Clear();
            _layerDispatches.Clear();
            _depthMergePasses.Clear();
            _pageHeapReaderPasses.Clear();
            _bindingScratchReadsPageHeap = false;
            _traversalPass = default;
            _frameResources = default;
            _outputs = default;
            _shadePasses = [];
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
            throw new InvalidOperationException("The Cluster pipeline was not created.");
        if (!_graphInstalled)
            throw new InvalidOperationException("ReconcileGraph must install Cluster before frame execution.");
        if (_hasPendingFrame)
            throw new InvalidOperationException("The previous Cluster frame has not been committed or discarded.");
        _resources.PumpStreaming();

        RenderInstanceBatches<RenderInstanceSingleGroup>? current = _instances.Current;
        if (current is null || current.GroupCount != 1)
        {
            throw new InvalidOperationException(
                "The Cluster pipeline requires one unclassified instance batch.");
        }
        RenderInstanceBatch batch = current.Groups[0].Batch;
        ClusterMaterialAssignmentSet materialAssignments = _materialAssignments.Current;
        if (materialAssignments.TopologyVersion != _installedMaterialTopologyVersion)
            throw new InvalidOperationException("Cluster material topology was not reconciled before the frame.");

        ViewCollector viewCollector = _viewCollector;
        viewCollector.Clear();
        try
        {
            context.World.ExecuteQuery(
                _viewQuery,
                ref viewCollector,
                static (QueryCursor cursor, ref ViewCollector state) => state.Collect(cursor));
            if (viewCollector.Views.Count == 0 ||
                viewCollector.Views.Count > _packedViews.Length)
            {
                throw new InvalidOperationException(
                    $"The Cluster pipeline requires 1..{_packedViews.Length} render views.");
            }
            ReadOnlySpan<RenderView> views = CollectionsMarshal.AsSpan(viewCollector.Views);
            for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                ref readonly RenderView view = ref views[viewIndex];
                if (view.ViewportWidth != _outputDescription.Width ||
                    view.ViewportHeight != _outputDescription.Height)
                {
                    throw new InvalidOperationException(
                        $"Render view {viewIndex} must match the retained Cluster output dimensions.");
                }
            }

            RecordFrame(ref context, batch, views);
        }
        finally
        {
            viewCollector.Clear();
        }
    }

    private void RecordFrame(
        ref RenderFrameSystemContext context,
        RenderInstanceBatch batch,
        scoped ReadOnlySpan<RenderView> views)
    {
        using ClusterRenderBinding binding = _resources.Use(
            context.ActiveFrame,
            batch,
            _instanceLayout);
        EnsurePageFaultReadback(binding.PageFaultCapacity);
        EnsureFrameMetricsReadback();
        if (_readbackWriteGeneration < 0)
            _readbackWriteGeneration = AcquireReadbackGeneration();
        ClusterCullingHistory history = _history ?? throw new InvalidOperationException(
            "The Cluster culling history was not created.");
        bool historyPrepared = false;
        try
        {
            Span<ClusterViewUniforms> packedViews = _packedViews;
            packedViews.Clear();
            history.Prepare(
                _outputDescription.Width,
                _outputDescription.Height,
                views);
            historyPrepared = true;
            for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                packedViews[viewIndex] = ClusterViewUniforms.Create(
                    in views[viewIndex],
                    _options,
                    _cullingEnabled,
                    binding.DispatchExtent,
                    binding.PageFaultCapacity);
                if (history.TryGetPrevious(
                        viewIndex,
                        out Matrix4x4 previousView,
                        out Matrix4x4 previousProjection))
                {
                    ref ClusterViewUniforms uniforms = ref packedViews[viewIndex];
                    uniforms.PrevViewProj = previousView * previousProjection;
                    uniforms.HasPrevHistory = 1;
                    uniforms.HiZMipCount = checked((uint)history.HiZMipCount);
                    uniforms.HiZInvSize = new Vector2(
                        1.0f / _outputDescription.Width,
                        1.0f / _outputDescription.Height);
                    uniforms.PrevView = previousView;
                    uniforms.PrevP00 = previousProjection.M11;
                    uniforms.PrevP11 = previousProjection.M22;
                }
            }

            _pendingReadbackEpoch = binding.ReadbackEpoch;
            _pendingResidencyPublication = binding.Publication;
            _pendingResidentPageRanges = binding.ResidentPageRanges;
            _hasPendingFrame = true;

            RenderGraphFrame graph = context.Graph;
            BindFrameResources(
                ref graph,
                in binding,
                history);
            RecordResidencyUploads(ref graph, in binding);
            DeclareResidencyReads(ref graph, in binding);
            WriteFrameUploads(
                ref graph,
                RequireReadbackWriteGeneration(),
                _installedDeformStages ?? throw new InvalidOperationException("Deform stages were not installed."),
                _installedRasterStages ?? throw new InvalidOperationException("Raster stages were not installed."),
                _installedShadeStages ?? throw new InvalidOperationException("Shade stages were not installed."),
                packedViews);
            BindShadeStages(
                ref graph,
                _installedShadeStages);
            graph.SetPassData(
                _traversalPass,
                new ClusterDispatchParameters(new DispatchArguments(
                    checked((checked((uint)binding.DispatchExtent) + 63u) / 64u),
                    checked((uint)views.Length),
                    1)));
            foreach (ClusterLayerDispatch dispatch in _layerDispatches)
            {
                graph.SetPassData(
                    dispatch.Pass,
                    new ClusterDispatchParameters(new DispatchArguments(
                        dispatch.GroupX,
                        dispatch.GroupY,
                        checked((uint)views.Length))));
            }
            foreach (GraphPassId pass in _depthMergePasses)
            {
                graph.SetPassData(
                    pass,
                    new ClusterFullscreenParameters(
                        _outputDescription.Width,
                        _outputDescription.Height,
                        checked((uint)views.Length)));
            }
            bool initializeHistory = history.RequiresInitialization;
            foreach (GraphPassId pass in _historyInitializationPasses)
                graph.SetPassEnabled(pass, initializeHistory);
        }
        catch
        {
            if (historyPrepared)
                history.Discard();
            ClearPendingFrame();
            throw;
        }
    }

    /// <summary>Publishes temporal state only after the authored graph submitted successfully.</summary>
    public void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        if (!_hasPendingFrame)
            throw new InvalidOperationException("No authored Cluster frame is waiting for commit.");
        int generation = RequireReadbackWriteGeneration();
        if (_pendingResidencyPublication is { } publication)
        {
            QueueCompletion[][] retirements = CaptureRetirementCompletions(publication);
            _resources.CommitResidencyPublication(publication, retirements);
            _pendingResidencyPublication = null;
        }
        QueueCompletion[] fences = _readbackFences[generation];
        if (fences.Length != completions.Length)
            fences = new QueueCompletion[completions.Length];
        completions.CopyTo(fences);
        ulong readbackSequence = checked(_nextReadbackSequence + 1);
        ulong frameMetricsSequence = _options.EnableFrameMetricsReadback
            ? checked(_frameMetricsSubmittedFrame + 1)
            : _frameMetricsSubmittedFrame;
        (_history ?? throw new InvalidOperationException("The Cluster culling history was not created."))
            .Commit(fences);
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
        if (_pendingResidencyPublication is { } publication)
            _resources.RetainResidencyPublication(publication);
        _history?.Discard();
        ClearPendingFrame();
    }

    private void ClearPendingFrame()
    {
        _pendingReadbackEpoch = default;
        _pendingResidencyPublication = null;
        _pendingResidentPageRanges = [];
        _readbackWriteGeneration = -1;
        _hasPendingFrame = false;
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;
        List<Exception>? failures = null;
        Release(ref _viewQuery, context.World, ref failures);
        Dispose(ref _uploads, ref failures);
        Dispose(ref _shadeStages, ref failures);
        Dispose(ref _deformStages, ref failures);
        DisposeMaterialSamplers(ref failures);
        _rasterStages = null;
        Dispose(ref _pipelines, ref failures);
        Dispose(ref _history, ref failures);
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
        _pageFaultReadbackBytes = [];
        _frameMetricReadbackBytes = [];
        _frameMetricsSubmittedFrame = 0;
        _nextReadbackSequence = 0;
        _readbackWriteGeneration = -1;
        _preferredReadbackGeneration = 0;
        _latestFrameMetrics = null;
        _historyInitializationPasses.Clear();
        _layerDispatches.Clear();
        _depthMergePasses.Clear();
        _traversalPass = default;
        _frameResources = default;
        _outputs = default;
        _installedDeformStages = null;
        _installedRasterStages = null;
        _installedShadeStages = null;
        _shadePasses = [];
        _installedMaterialTopologyVersion = 0;
        _graphInstalled = false;
        ClearPendingFrame();
        _created = false;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private void EnsurePipelineResources()
    {
        if (_pipelines is not null)
            return;

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
                _configuration);
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
