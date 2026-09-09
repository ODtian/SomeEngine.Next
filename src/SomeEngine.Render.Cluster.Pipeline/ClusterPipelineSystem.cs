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
    private const int RasterEpochCount = 4;
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
    private readonly IClusterStageResourceProvider? _stageResourceProvider;
    private readonly ClusterStreamingFeedbackChannel _streamingFeedback;
    private readonly ClusterPipelineOptions _options;
    private readonly ClusterVisibilityMode _visibilityMode;
    private QueryHandle _viewQuery;
    private ClusterPipelineSet? _pipelines;
    private ClusterRasterStages? _rasterStages;
    private ClusterShadeStages? _shadeStages;
    private ClusterRasterStageSet? _installedRasterStages;
    private ClusterShadeStageSet? _installedShadeStages;
    private GraphPassId[] _shadePasses = [];
    private ClusterCullingHistory? _history;
    private ClusterFrameUploads? _uploads;
    private FrameResources _frameResources;
    private readonly List<GraphPassId> _historyInitializationPasses = [];
    private readonly List<GraphPassId> _stagePasses = [];
    private readonly List<GraphPassId> _geometryStagePasses = [];
    private readonly List<GraphPassId> _shadeStagePasses = [];
    private readonly List<GraphPassId>[] _binningStagePasses = [[], [], [], []];
    private readonly List<GraphPassId>[] _rasterBeforeDepthStagePasses = [[], [], [], []];
    private readonly List<GraphPassId>[] _hardwareRasterStagePasses = [[], [], [], []];
    private readonly List<ClusterSoftwareRasterPassBinding> _softwareRasterPassBindings = [];
    private readonly List<ClusterHardwareRasterPassBinding> _hardwareRasterPassBindings = [];
    private readonly List<ClusterTessellationPassBinding> _tessellationSeedPassBindings = [];
    private readonly List<ClusterTessellationPassBinding> _tessellationSplitPassBindings = [];
    private readonly List<ClusterTessellationPassBinding> _tessellationDicePassBindings = [];
    private readonly List<ClusterTessellationHardwarePassBinding> _tessellationHardwarePassBindings = [];
    private readonly List<ClusterLayerDispatch> _layerDispatches = [];
    private readonly List<GraphPassId> _winnerResolvePasses = [];
    private readonly List<GraphPassId> _pageHeapReaderPasses = [];
    private ClusterViewUniforms[] _packedViews;
    private RenderView[] _stagedViews;
    private Matrix4x4[] _stagedPreviousViewProjections;
    private bool[] _stagedHasPreviousView;
    private uint[] _stagedPolicies;
    private uint[] _stagedPolicyViewIndices;
    private int _stagedViewCount;
    private int _primaryViewCount;
    private GraphBufferSlice _framePolicyViews;
    private GraphPassId _traversalSeedPass;
    private GraphPassId _traversalPass;
    private readonly List<GraphPassId> _traversalLevelPasses = [];
    private GraphPassId _primaryDeferredResetPass;
    private GraphPassId _primaryDeferredResumePass;
    private GraphPassId _primaryPostTraversalPass;
    private readonly List<GraphPassId> _primaryPostTraversalLevelPasses = [];
    private GraphPassId _continuationResetPass;
    private GraphPassId _continuationTraversalSeedPass;
    private GraphPassId _continuationTraversalPass;
    private readonly List<GraphPassId> _continuationTraversalLevelPasses = [];
    private GraphPassId _continuationCullPass;
    private GraphPassId _continuationDeferredResetPass;
    private GraphPassId _continuationDeferredResumePass;
    private GraphPassId _continuationPostTraversalPass;
    private readonly List<GraphPassId> _continuationPostTraversalLevelPasses = [];
    private GraphPassId _continuationPostCullPass;
    private GraphPassId _phaseOneCullPass;
    private GraphPassId _winnerClearPass;
    private GraphPassId _phaseTwoCullPass;
    private GraphPassId _phaseOneWinnerResolvePass;
    private GraphPassId _phaseTwoWinnerResolvePass;
    private GraphPassId _phaseOneHiZPass;
    private GraphPassId _phaseTwoHiZPass;
    private GraphPassId _shadeResolvePass;
    private ClusterPipelineOutputs _outputs;
    private ClusterRenderContext _renderContext;
    private GraphPassId _shadeBeginAfter;
    private GraphPassId _shadeCompleteBefore;
    private GraphPassId _depthCompleteBefore;
    private ulong _installedMaterialTopologyVersion;
    private bool _cullingEnabled;
    private bool _graphInstalled;
    private IndirectCommandLayout? _dispatchIndirectLayout;
    private IndirectCommandLayout? _drawIndirectLayout;
    private IndirectCommandLayout? _drawIndexedIndirectLayout;
    private readonly Buffer?[] _pageFaultReadbacks = new Buffer?[ReadbackGenerationCount];
    private byte[] _pageFaultReadbackBytes = [];
    private readonly ClusterReadbackTicket[] _pageFaultReadbackTickets =
        new ClusterReadbackTicket[ReadbackGenerationCount];
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
    private int _pendingInstanceGeneration = -1;
    private ClusterResidencyPublication? _pendingResidencyPublication;
    private BufferRangeSet _residentPageReads = BufferRangeSet.Empty;
    private bool _bindingScratchReadsPageHeap;
    private bool _recordStagePasses;
    private List<GraphPassId>? _stagePassDestination;
    private List<GraphPassId>? _stagePassGroupDestination;
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
        ClusterStreamingFeedbackChannel streamingFeedback,
        IClusterStageResourceProvider? stageResourceProvider = null,
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
        _streamingFeedback = streamingFeedback ?? throw new ArgumentNullException(nameof(streamingFeedback));
        _stageResourceProvider = stageResourceProvider;
        _options = options ?? new ClusterPipelineOptions();
        _visibilityMode = _options.SelectVisibilityMode(device.Capabilities);
        _packedViews = new ClusterViewUniforms[checked((int)outputDescription.ViewCount)];
        _stagedViews = new RenderView[checked((int)outputDescription.ViewCount)];
        _stagedPreviousViewProjections = new Matrix4x4[_stagedViews.Length];
        _stagedHasPreviousView = new bool[_stagedViews.Length];
        _stagedPolicies = new uint[_stagedViews.Length];
        _stagedPolicyViewIndices = new uint[_stagedViews.Length];
    }

    public ClusterPipelineOutputs Outputs => _outputs.IsValid
        ? _outputs
        : throw new InvalidOperationException("The Cluster retained graph has not been installed.");

    public ulong GeometryRevision => _resources.CaptureDiagnostics().Meshes.ChangeRevision;

    private ulong _rasterRevision;
    public ulong RasterRevision => _rasterRevision;

    public bool HasFiniteGeometryBounds => _installedRasterStages is { HasFiniteBounds: true };

    public RenderInstanceBatch? CurrentInstances => _instances.Current is { GroupCount: 1 } current
        ? current.Groups[0].Batch : null;

    public ClusterVisibilityMode VisibilityMode => _visibilityMode;

    public int CurrentInstanceCount
    {
        get
        {
            RenderInstanceBatches<RenderInstanceSingleGroup>? current = _instances.Current;
            return current is { GroupCount: 1 } ? current.Groups[0].Batch.InstanceCount : 0;
        }
    }

    /// <summary>
    /// Appends views to the one shared Cluster work stream. Policy zero writes the primary dense
    /// visibility output; non-zero policies use a registered context adapter. View kind never
    /// changes the traversal, cull, bin, tessellation, or raster pass topology.
    /// </summary>
    public void StageViews(
        scoped ReadOnlySpan<RenderView> views,
        uint policy = 0u,
        uint firstPolicyView = 0u,
        scoped ReadOnlySpan<Matrix4x4> previousViewProjections = default)
    {
        if (!_created)
            throw new InvalidOperationException("The Cluster pipeline was not created.");
        if (_hasPendingFrame)
            throw new InvalidOperationException("Views cannot be staged while a Cluster frame is pending.");
        int required = checked(_stagedViewCount + views.Length);
        if (views.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(views));
        if (!previousViewProjections.IsEmpty && previousViewProjections.Length != views.Length)
        {
            throw new ArgumentException(
                "Previous view projections must be empty or match the staged view count.",
                nameof(previousViewProjections));
        }
        if (policy == 0u && !previousViewProjections.IsEmpty)
        {
            throw new ArgumentException(
                "Primary view history is owned by ClusterCullingHistory.",
                nameof(previousViewProjections));
        }
        if (policy == 0u)
        {
            if (_stagedViewCount != _primaryViewCount)
                throw new InvalidOperationException(
                    "Primary Cluster views must be staged before policy-backed views.");
            if (required > checked((int)_outputDescription.ViewCount))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(views),
                    $"Cluster output owns exactly {_outputDescription.ViewCount} primary views.");
            }
            _primaryViewCount = required;
        }
        else if (!_renderContext.IsValid || policy != _renderContext.PolicyId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "The view policy has no registered Cluster render context.");
        }
        else if (required - _primaryViewCount > checked((int)_renderContext.MaximumInvocationCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(views),
                $"The target policy admits at most {_renderContext.MaximumInvocationCount} raster invocations.");
        }

        EnsureStagedViewStorage(required);

        views.CopyTo(_stagedViews.AsSpan(_stagedViewCount));
        if (previousViewProjections.IsEmpty)
        {
            _stagedPreviousViewProjections.AsSpan(_stagedViewCount, views.Length).Clear();
            _stagedHasPreviousView.AsSpan(_stagedViewCount, views.Length).Clear();
        }
        else
        {
            previousViewProjections.CopyTo(
                _stagedPreviousViewProjections.AsSpan(_stagedViewCount, views.Length));
            _stagedHasPreviousView.AsSpan(_stagedViewCount, views.Length).Fill(true);
        }
        _stagedPolicies.AsSpan(_stagedViewCount, views.Length).Fill(policy);
        for (int index = 0; index < views.Length; index++)
            _stagedPolicyViewIndices[_stagedViewCount + index] = checked(
                firstPolicyView + (uint)index);
        _stagedViewCount = required;
    }

    private void EnsureStagedViewStorage(int required)
    {
        if (_stagedViews.Length >= required)
            return;
        Array.Resize(ref _packedViews, required);
        Array.Resize(ref _stagedViews, required);
        Array.Resize(ref _stagedPreviousViewProjections, required);
        Array.Resize(ref _stagedHasPreviousView, required);
        Array.Resize(ref _stagedPolicies, required);
        Array.Resize(ref _stagedPolicyViewIndices, required);
    }

    /// <summary>
    /// Supplies one feature-owned raster-invocation stream before the retained graph is installed.
    /// Cluster consumes only flat view, destination, and occlusion metadata; the feature owns all
    /// conversion from its sparse representation and all interpretation of the target output.
    /// </summary>
    public void ConfigureRenderContext(in ClusterRenderContext context)
    {
        if (_graphInstalled)
            throw new InvalidOperationException("The render context must be configured before graph installation.");
        if (!context.IsValid)
            throw new ArgumentException("The render context is incomplete.", nameof(context));
        if (_renderContext.IsValid)
            throw new InvalidOperationException("A policy-backed Cluster render context is already registered.");
        _renderContext = context;
    }

    public void ConfigureShadeBeginAfter(GraphPassId pass)
    {
        if (_graphInstalled)
            throw new InvalidOperationException("The shade dependency must be configured before graph installation.");
        if (pass == default)
            throw new ArgumentException("The shade dependency pass is invalid.", nameof(pass));
        _shadeBeginAfter = pass;
    }

    public GraphPassId ShadeCompletePass => _shadePasses.Length != 0
        ? _shadePasses[^1]
        : _shadeResolvePass;

    public void ConfigureShadeCompleteBefore(GraphPassId pass)
    {
        if (_graphInstalled)
            throw new InvalidOperationException("The shade completion pass must be configured before graph installation.");
        if (pass == default)
            throw new ArgumentException("The shade completion pass is invalid.", nameof(pass));
        _shadeCompleteBefore = pass;
    }

    public void ConfigureDepthCompleteBefore(GraphPassId pass)
    {
        if (_graphInstalled)
            throw new InvalidOperationException("The depth consumer must be configured before graph installation.");
        if (pass == default)
            throw new ArgumentException("A depth consumer pass is required.", nameof(pass));
        _depthCompleteBefore = pass;
    }

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
            _rasterStages = new ClusterRasterStages(
                _backend,
                _device,
                _visibilityMode,
                _pipelines!.DefaultRasterVertexEncoding,
                _pipelines.SoftwareRaster,
                _pipelines.HardwareRaster,
                _stageResourceProvider);
            _shadeStages = new ClusterShadeStages(
                _backend,
                _device,
                _stageResourceProvider);
            _history = new ClusterCullingHistory(
                _backend,
                _device,
                _outputDescription.ViewCount);
            _created = true;
        }
        catch
        {
            _history?.Dispose();
            _history = null;
            _shadeStages?.Dispose();
            _shadeStages = null;
            _rasterStages?.Dispose();
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
        ClusterMaterialAssignmentSet assignments = _materialAssignments.Current;
        if (assignments.MaterialCount == 0)
            throw new InvalidOperationException("The Cluster scene has no published materials.");
        if (_graphInstalled && _traversalLevelPasses.Count < _resources.HierarchyLevels)
            ReconcileTraversal();
        if (_graphInstalled &&
            _installedMaterialTopologyVersion == assignments.TopologyVersion &&
            !_materialsChanged)
        {
            return _outputs;
        }
        ClusterRasterStageSet rasterStages = (_rasterStages ?? throw new InvalidOperationException(
            "The Cluster raster stages were not created.")).Resolve(assignments);
        ReconcileMaterialBuffers(assignments);
        ClusterShadeStageSet shadeStages = (_shadeStages ?? throw new InvalidOperationException(
            "The Cluster shade stages were not created.")).Resolve(assignments);
        bool cullingEnabled = (_pipelines ?? throw new InvalidOperationException(
            "The Cluster pipelines were not created.")).DefaultRasterCullingEnabled &&
            rasterStages.HasFiniteBounds;
        bool rasterChanged = _installedRasterStages is null ||
            !_installedRasterStages.HasSameTopology(rasterStages) || _cullingEnabled != cullingEnabled;
        if (_graphInstalled)
        {
            bool shadeChanged = _installedShadeStages is null ||
                !_installedShadeStages.HasSameTopology(shadeStages);
            if (rasterChanged || shadeChanged)
            {
                ReconcileStageRegion(
                    assignments,
                    rasterStages,
                    shadeStages,
                    rasterChanged,
                    shadeChanged);
            }
            (_shadeScalarOffsets, _shadeScalars) = shadeStages.EncodeScalars();
            _installedMaterialTopologyVersion = assignments.TopologyVersion;
            ObserveMaterials(assignments);
            _installedRasterStages = rasterStages;
            _installedShadeStages = shadeStages;
            _cullingEnabled = cullingEnabled;
            if (rasterChanged) _rasterRevision = checked(_rasterRevision + 1);
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
                uploads.BeginDeclarations();
                InstallGraph(
                    ref edit,
                    in _outputDescription,
                    assignments,
                    rasterStages,
                    shadeStages);
                uploads.RemoveUnusedDeclarations(ref edit);
                RecordUploadFinalization(ref edit);
                edit.Commit();
                uploads.CompleteDeclarations(_graph);
            }
            catch
            {
                uploads.CancelDeclarations();
                throw;
            }
            finally
            {
                edit.Dispose();
            }
            (_shadeScalarOffsets, _shadeScalars) = shadeStages.EncodeScalars();
            _installedMaterialTopologyVersion = assignments.TopologyVersion;
            ObserveMaterials(assignments);
            _installedRasterStages = rasterStages;
            _installedShadeStages = shadeStages;
            _cullingEnabled = cullingEnabled;
            _graphInstalled = true;
            _rasterRevision = checked(_rasterRevision + 1);
            return _outputs;
        }
        catch
        {
            _uploads = null;
            uploads.Dispose();
            _historyInitializationPasses.Clear();
            _stagePasses.Clear();
            _geometryStagePasses.Clear();
            _shadeStagePasses.Clear();
            for (int phase = 0; phase < RasterEpochCount; phase++)
            {
                _binningStagePasses[phase].Clear();
                _rasterBeforeDepthStagePasses[phase].Clear();
                _hardwareRasterStagePasses[phase].Clear();
            }
            _softwareRasterPassBindings.Clear();
            _hardwareRasterPassBindings.Clear();
            _tessellationSeedPassBindings.Clear();
            _tessellationSplitPassBindings.Clear();
            _tessellationDicePassBindings.Clear();
            _tessellationHardwarePassBindings.Clear();
            _layerDispatches.Clear();
            _winnerResolvePasses.Clear();
            _pageHeapReaderPasses.Clear();
            _bindingScratchReadsPageHeap = false;
            _traversalSeedPass = default;
            _traversalPass = default;
            _traversalLevelPasses.Clear();
            _primaryDeferredResetPass = default;
            _primaryDeferredResumePass = default;
            _primaryPostTraversalPass = default;
            _primaryPostTraversalLevelPasses.Clear();
            _continuationResetPass = default;
            _continuationTraversalSeedPass = default;
            _continuationTraversalPass = default;
            _continuationTraversalLevelPasses.Clear();
            _continuationCullPass = default;
            _continuationDeferredResetPass = default;
            _continuationDeferredResumePass = default;
            _continuationPostTraversalPass = default;
            _continuationPostTraversalLevelPasses.Clear();
            _continuationPostCullPass = default;
            _materialInputs.Clear();
            _shadeUniformInputs = [];
            _frameResources = default;
            _outputs = default;
            _shadePasses = [];
            throw;
        }
    }

    private void ReconcileStageRegion(
        ClusterMaterialAssignmentSet assignments,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages,
        bool rasterChanged,
        bool shadeChanged)
    {
        bool geometryChanged = rasterChanged;
        ClusterFrameUploads uploads = _uploads ?? throw new InvalidOperationException(
            "Cluster retained upload storage was not created.");
        GraphPassId[] previousStagePasses = [.. _stagePasses];
        GraphPassId[] previousGeometryStagePasses = [.. _geometryStagePasses];
        GraphPassId[] previousShadeStagePasses = [.. _shadeStagePasses];
        GraphPassId[][] previousBinningStagePasses =
            [[.. _binningStagePasses[0]], [.. _binningStagePasses[1]], [.. _binningStagePasses[2]], [.. _binningStagePasses[3]]];
        GraphPassId[][] previousRasterBeforeDepthStagePasses =
            [[.. _rasterBeforeDepthStagePasses[0]], [.. _rasterBeforeDepthStagePasses[1]], [.. _rasterBeforeDepthStagePasses[2]], [.. _rasterBeforeDepthStagePasses[3]]];
        GraphPassId[][] previousHardwareRasterStagePasses =
            [[.. _hardwareRasterStagePasses[0]], [.. _hardwareRasterStagePasses[1]], [.. _hardwareRasterStagePasses[2]], [.. _hardwareRasterStagePasses[3]]];
        ClusterSoftwareRasterPassBinding[] previousSoftwareRasterPassBindings = [.. _softwareRasterPassBindings];
        ClusterHardwareRasterPassBinding[] previousHardwareRasterPassBindings = [.. _hardwareRasterPassBindings];
        ClusterTessellationPassBinding[] previousTessellationSeedPassBindings = [.. _tessellationSeedPassBindings];
        ClusterTessellationPassBinding[] previousTessellationSplitPassBindings = [.. _tessellationSplitPassBindings];
        ClusterTessellationPassBinding[] previousTessellationDicePassBindings = [.. _tessellationDicePassBindings];
        ClusterLayerDispatch[] previousLayerDispatches = [.. _layerDispatches];
        GraphPassId[] previousWinnerResolvePasses = [.. _winnerResolvePasses];
        GraphPassId[] previousPageHeapReaders = [.. _pageHeapReaderPasses];
        MaterialInput[] previousMaterialInputs = [.. _materialInputs];
        GraphBufferInput[] previousShadeUniformInputs = _shadeUniformInputs;
        ClusterTessellationHardwarePassBinding[] previousTessellationHardwarePassBindings = [.. _tessellationHardwarePassBindings];
        GraphPassId[] previousShadePasses = _shadePasses;
        FrameResources previousFrameResources = _frameResources;

        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            var removal = new HashSet<GraphPassId>();
            if (geometryChanged)
            {
                for (int epoch = 0; epoch < RasterEpochCount; epoch++)
                    foreach (GraphPassId pass in previousBinningStagePasses[epoch]) removal.Add(pass);
                if (rasterChanged)
                {
                    for (int epoch = 0; epoch < RasterEpochCount; epoch++)
                    {
                        foreach (GraphPassId pass in previousRasterBeforeDepthStagePasses[epoch]) removal.Add(pass);
                        foreach (GraphPassId pass in previousHardwareRasterStagePasses[epoch]) removal.Add(pass);
                    }
                }
            }
            if (shadeChanged)
                foreach (GraphPassId pass in previousShadeStagePasses) removal.Add(pass);
            GraphPassId[] removedPasses = [.. removal];
            foreach (GraphPassId pass in removedPasses)
            {
                edit.Remove(pass);
            }

            var removed = new HashSet<GraphPassId>(removedPasses);
            _stagePasses.RemoveAll(removed.Contains);
            _materialInputs.RemoveAll(input => removed.Contains(input.Pass));
            if (geometryChanged)
            {
                _geometryStagePasses.RemoveAll(removed.Contains);
                for (int epoch = 0; epoch < RasterEpochCount; epoch++)
                    _binningStagePasses[epoch].Clear();
                if (rasterChanged)
                {
                    for (int epoch = 0; epoch < RasterEpochCount; epoch++)
                    {
                        _rasterBeforeDepthStagePasses[epoch].Clear();
                        _hardwareRasterStagePasses[epoch].Clear();
                    }
                    _softwareRasterPassBindings.Clear();
                    _hardwareRasterPassBindings.Clear();
                    _tessellationSeedPassBindings.Clear();
                    _tessellationSplitPassBindings.Clear();
                    _tessellationDicePassBindings.Clear();
                    _tessellationHardwarePassBindings.Clear();
                }
            }
            if (shadeChanged)
            {
                _shadeStagePasses.Clear();
                _shadePasses = [];
            }
            _layerDispatches.RemoveAll(dispatch => removed.Contains(dispatch.Pass));
            _pageHeapReaderPasses.RemoveAll(removed.Contains);

            uploads.BeginDeclarations();
            ReconcileStageResources(
                ref edit,
                assignments,
                rasterStages,
                shadeStages);
            if (geometryChanged)
            {
                RecordGeometryStageRegion(
                    ref edit,
                    _frameResources,
                    rasterStages,
                    _frameResources.PackedViews,
                    rasterChanged);
            }
            if (shadeChanged)
            {
                RecordShadeStageRegion(
                    ref edit,
                    _frameResources,
                    shadeStages);
            }
            if (rasterChanged) BindRasterStages(ref edit, rasterStages);
            if (shadeChanged) BindShadeStages(ref edit, shadeStages);
            uploads.RemoveUnusedDeclarations(ref edit);
            RecordUploadFinalization(ref edit);
            edit.Commit();
            uploads.CompleteDeclarations(_graph);
        }
        catch
        {
            uploads.CancelDeclarations();
            _stagePasses.Clear();
            _stagePasses.AddRange(previousStagePasses);
            _geometryStagePasses.Clear();
            _geometryStagePasses.AddRange(previousGeometryStagePasses);
            _shadeStagePasses.Clear();
            _shadeStagePasses.AddRange(previousShadeStagePasses);
            for (int phase = 0; phase < RasterEpochCount; phase++)
            {
                _binningStagePasses[phase].Clear();
                _binningStagePasses[phase].AddRange(previousBinningStagePasses[phase]);
                _rasterBeforeDepthStagePasses[phase].Clear();
                _rasterBeforeDepthStagePasses[phase].AddRange(previousRasterBeforeDepthStagePasses[phase]);
                _hardwareRasterStagePasses[phase].Clear();
                _hardwareRasterStagePasses[phase].AddRange(previousHardwareRasterStagePasses[phase]);
            }
            _softwareRasterPassBindings.Clear();
            _softwareRasterPassBindings.AddRange(previousSoftwareRasterPassBindings);
            _hardwareRasterPassBindings.Clear();
            _hardwareRasterPassBindings.AddRange(previousHardwareRasterPassBindings);
            _tessellationSeedPassBindings.Clear();
            _tessellationSeedPassBindings.AddRange(previousTessellationSeedPassBindings);
            _tessellationSplitPassBindings.Clear();
            _tessellationSplitPassBindings.AddRange(previousTessellationSplitPassBindings);
            _tessellationDicePassBindings.Clear();
            _tessellationDicePassBindings.AddRange(previousTessellationDicePassBindings);
            _layerDispatches.Clear();
            _layerDispatches.AddRange(previousLayerDispatches);
            _winnerResolvePasses.Clear();
            _winnerResolvePasses.AddRange(previousWinnerResolvePasses);
            _pageHeapReaderPasses.Clear();
            _pageHeapReaderPasses.AddRange(previousPageHeapReaders);
            _materialInputs.Clear();
            _materialInputs.AddRange(previousMaterialInputs);
            _shadeUniformInputs = previousShadeUniformInputs;
            _tessellationHardwarePassBindings.Clear();
            _tessellationHardwarePassBindings.AddRange(previousTessellationHardwarePassBindings);
            _shadePasses = previousShadePasses;
            _frameResources = previousFrameResources;
            throw;
        }
        finally
        {
            edit.Dispose();
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
            ReadOnlySpan<RenderView> views;
            int primaryViewCount;
            if (_stagedViewCount != 0)
            {
                views = _stagedViews.AsSpan(0, _stagedViewCount);
                primaryViewCount = _primaryViewCount;
            }
            else
            {
                context.World.ExecuteQuery(
                    _viewQuery,
                    ref viewCollector,
                    static (QueryCursor cursor, ref ViewCollector state) => state.Collect(cursor));
                views = CollectionsMarshal.AsSpan(viewCollector.Views);
                primaryViewCount = views.Length;
            }
            if (primaryViewCount != checked((int)_outputDescription.ViewCount))
            {
                throw new InvalidOperationException(
                    $"The Cluster output requires exactly {_outputDescription.ViewCount} primary views, " +
                    $"but {primaryViewCount} were staged.");
            }
            if (views.Length == 0 || views.Length > _packedViews.Length)
            {
                throw new InvalidOperationException(
                    $"The Cluster pipeline requires 1..{_packedViews.Length} render views.");
            }
            for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                ref readonly RenderView view = ref views[viewIndex];
                if (viewIndex < primaryViewCount &&
                    (view.ViewportWidth != _outputDescription.Width ||
                     view.ViewportHeight != _outputDescription.Height))
                {
                    throw new InvalidOperationException(
                        $"Render view {viewIndex} must match the retained Cluster output dimensions.");
                }
                if (viewIndex >= primaryViewCount && !_renderContext.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Render view {viewIndex} requests a raster-target context, but no target policy is configured.");
                }
            }

            RecordFrame(ref context, batch, views, primaryViewCount);
        }
        finally
        {
            viewCollector.Clear();
            _stagedViews.AsSpan(0, _stagedViewCount).Clear();
            _stagedPolicies.AsSpan(0, _stagedViewCount).Clear();
            _stagedPolicyViewIndices.AsSpan(0, _stagedViewCount).Clear();
            _stagedViewCount = 0;
            _primaryViewCount = 0;
        }
    }

    private void RecordFrame(
        ref RenderFrameSystemContext context,
        RenderInstanceBatch batch,
        scoped ReadOnlySpan<RenderView> views,
        int primaryViewCount)
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
                views[..primaryViewCount]);
            historyPrepared = true;
            for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                bool isTargetView = viewIndex >= primaryViewCount;
                ClusterViewRasterMode rasterMode = _visibilityMode == ClusterVisibilityMode.HardwareOnly
                    ? ClusterViewRasterMode.Hardware
                    : ClusterViewRasterMode.Adaptive;
                packedViews[viewIndex] = ClusterViewUniforms.Create(
                    in views[viewIndex],
                    _options,
                    rasterMode,
                    _cullingEnabled,
                    binding.DispatchExtent,
                    binding.PageFaultCapacity);
                packedViews[viewIndex].TargetPolicy = isTargetView
                    ? _stagedPolicies[viewIndex]
                    : 0u;
                packedViews[viewIndex].TargetViewIndex = isTargetView
                    ? _stagedPolicyViewIndices[viewIndex]
                    : checked((uint)viewIndex);
                if (!isTargetView && history.TryGetPrevious(
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
                else if (isTargetView && _renderContext.IsValid)
                {
                    ref ClusterViewUniforms uniforms = ref packedViews[viewIndex];
                    if (_stagedHasPreviousView[viewIndex])
                    {
                        uniforms.PrevViewProj = _stagedPreviousViewProjections[viewIndex];
                        uniforms.HasPrevHistory = 1u;
                    }
                    uniforms.HiZMipCount = _renderContext.OcclusionMipCount;
                    uniforms.HiZInvSize = new Vector2(
                        1.0f / _renderContext.OcclusionTexelSize,
                        1.0f / _renderContext.OcclusionTexelSize);
                }
            }

            _pendingReadbackEpoch = binding.ReadbackEpoch;
            _pendingInstanceGeneration = binding.InstanceGeneration;
            _pendingResidencyPublication = binding.Publication;
            _outputs.Geometry.SetFrameRanges(binding.ResidentPageRanges, binding.BvhRange);
            _residentPageReads = _outputs.Geometry.ResidentPageReads;
            _hasPendingFrame = true;

            RenderGraphFrame graph = context.Graph;
            int policyViewCount = views.Length - primaryViewCount;
            _framePolicyViews = policyViewCount == 0
                ? new GraphBufferSlice(_frameResources.PackedViews, BufferRange.Whole)
                : graph.Upload(
                    packedViews.Slice(primaryViewCount, policyViewCount),
                    BufferUsages.ShaderRead,
                    "Cluster active policy views");
            if (_renderContext.IsValid)
                _renderContext.InvocationSourceBinder(ref graph, _framePolicyViews);
            BindFrameResources(
                ref graph,
                in binding,
                history);
            BindSceneBounds(ref graph, batch);
            RecordResidencyUploads(ref graph, in binding);
            DeclareResidencyReads(ref graph, in binding);
            WriteFrameUploads(
                ref graph,
                RequireReadbackWriteGeneration(),
                _installedRasterStages ?? throw new InvalidOperationException("Raster stages were not installed."),
                _installedShadeStages ?? throw new InvalidOperationException("Shade stages were not installed."),
                packedViews[..primaryViewCount],
                primaryViewCount);
            UpdateShadeInputs(ref graph, _installedShadeStages!);
            UpdateMaterialInputs(ref graph);
            graph.SetPassData(
                _traversalSeedPass,
                new ClusterDispatchParameters(new DispatchArguments(
                    checked((checked((uint)binding.DispatchExtent) + 63u) / 64u),
                    checked((checked((uint)primaryViewCount) + 31u) / 32u),
                    1)));
            foreach (ClusterLayerDispatch dispatch in _layerDispatches)
            {
                graph.SetPassData(
                    dispatch.Pass,
                    new ClusterDispatchParameters(new DispatchArguments(
                        dispatch.GroupX,
                        dispatch.GroupY,
                        checked((uint)primaryViewCount))));
            }
            foreach (GraphPassId pass in _winnerResolvePasses)
            {
                graph.SetPassData(
                    pass,
                    new ClusterFullscreenParameters(
                        _outputDescription.Width,
                        _outputDescription.Height,
                        checked((uint)primaryViewCount)));
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

    private void BindTargetPolicyResources(ref RenderGraphEdit graph)
    {
        PassDefinition seed = graph.GetPass(_traversalSeedPass);
        BindNeutralInvocationCount(ref graph, ref seed);
        BindHierarchySeedResources(ref graph, _traversalSeedPass, policyTarget: false);
        foreach (GraphPassId traversalLevelPass in _traversalLevelPasses)
            BindTargetPolicyResources(ref graph, traversalLevelPass, policyTarget: false);
        foreach (GraphPassId traversalLevelPass in _primaryPostTraversalLevelPasses)
            BindTargetPolicyResources(ref graph, traversalLevelPass, policyTarget: false);
        BindTargetPolicyResources(ref graph, _phaseOneCullPass, policyTarget: false);
        BindTargetPolicyResources(ref graph, _phaseTwoCullPass, policyTarget: false);

        if (!_renderContext.IsValid)
            return;

        PassDefinition continuationSeed = graph.GetPass(_continuationTraversalSeedPass);
        _renderContext.InvocationCountBinder(ref graph, ref continuationSeed);
        BindHierarchySeedResources(
            ref graph,
            _continuationTraversalSeedPass,
            policyTarget: true);
        foreach (GraphPassId traversalLevelPass in _continuationTraversalLevelPasses)
            BindTargetPolicyResources(ref graph, traversalLevelPass, policyTarget: true);
        foreach (GraphPassId traversalLevelPass in _continuationPostTraversalLevelPasses)
            BindTargetPolicyResources(ref graph, traversalLevelPass, policyTarget: true);
        BindTargetPolicyResources(ref graph, _continuationCullPass, policyTarget: true);
        BindTargetPolicyResources(ref graph, _continuationPostCullPass, policyTarget: true);
    }

    private void BindNeutralInvocationCount(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameResources.NeutralPolicyValue,
                structureStride: sizeof(uint),
                label: "Cluster neutral raster-invocation count"),
            PipelineSync.ComputeShading);
    }

    private void BindHierarchySeedResources(
        ref RenderGraphEdit graph,
        GraphPassId passId,
        bool policyTarget)
    {
        PassDefinition pass = graph.GetPass(passId);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _frameResources.PreviousHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster hierarchy history HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferUav(
                _frameResources.Phase2CandidateClusters,
                structureStride: 12,
                label: "Cluster deferred hierarchy work"),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(
            graph.CreateBufferUav(
                _frameResources.Phase2CandidateCount,
                structureStride: sizeof(uint),
                label: "Cluster deferred hierarchy count"),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Complete);
        BindPolicyClusterViews(
            ref graph,
            ref pass,
            PipelineSync.ComputeShading,
            policyTarget);
        if (policyTarget)
            _renderContext.FilterResourcesBinder(ref graph, ref pass);
        else
            BindNeutralTargetPolicyFilterResources(ref graph, ref pass);
        BindSceneSeed(ref graph, ref pass);
    }

    private void BindTargetPolicyResources(
        ref RenderGraphEdit graph,
        GraphPassId passId,
        bool policyTarget)
    {
        PassDefinition pass = graph.GetPass(passId);
        BindPolicyClusterViews(
            ref graph,
            ref pass,
            PipelineSync.ComputeShading,
            policyTarget);
        if (policyTarget)
            _renderContext.FilterResourcesBinder(ref graph, ref pass);
        else
            BindNeutralTargetPolicyFilterResources(ref graph, ref pass);
    }

    private void BindPolicyClusterViews(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync,
        bool policyTarget = true)
    {
        if (policyTarget)
        {
            _renderContext.InvocationViewsBinder(ref graph, ref pass);
            return;
        }
        GraphBufferId views = _frameResources.PackedViews;
        _ = pass.Bind(
            graph.CreateBufferSrv(
                views,
                structureStride: ClusterViewUniforms.SizeInBytes,
                label: "Cluster active policy view metadata"),
            sync);
    }

    private void BindNeutralTargetPolicyFilterResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameResources.NeutralPolicyView,
                structureStride: 48,
                label: "Cluster neutral raster-target invocation"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _frameResources.PreviousHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster neutral current target HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _frameResources.PreviousHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster neutral previous target HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameResources.NeutralPolicyValue,
            structureStride: sizeof(uint), label: "Cluster neutral region grid"), PipelineSync.ComputeShading);
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
        if (fences.Length != 1)
            fences = new QueueCompletion[1];
        fences[0] = FindQueueCompletion(completions, QueueType.Copy);
        ulong readbackSequence = checked(_nextReadbackSequence + 1);
        ulong frameMetricsSequence = _options.EnableFrameMetricsReadback
            ? checked(_frameMetricsSubmittedFrame + 1)
            : _frameMetricsSubmittedFrame;
        (_history ?? throw new InvalidOperationException("The Cluster culling history was not created."))
            .Commit(completions, currentHiZWritten: true);
        _readbackFences[generation] = fences;
        _nextReadbackSequence = readbackSequence;
        _readbackSequences[generation] = readbackSequence;
        _pageFaultReadbackTickets[generation] = new ClusterReadbackTicket(
            _pendingReadbackEpoch,
            readbackSequence);
        _pageFaultReadbackPending[generation] = true;
        if (_options.EnableFrameMetricsReadback)
        {
            _frameMetricsSubmittedFrame = frameMetricsSequence;
            _frameMetricReadbackFrames[generation] = frameMetricsSequence;
            _frameMetricReadbackPending[generation] = true;
        }
        (_uploads ?? throw new InvalidOperationException(
            "Cluster retained upload storage was not created."))
            .Commit(generation, completions);
        _resources.CommitInstanceReadEndpoints(
            _pendingInstanceGeneration,
            FindQueueCompletion(completions, QueueType.Graphics));
        _preferredReadbackGeneration = 1 - generation;
        _sceneBatch = CurrentInstances;
        _sceneInstanceRevision = _sceneBatch?.ContentRevision ?? 0;
        _sceneGeometryRevision = GeometryRevision;
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
        _uploads?.DiscardFrame();
        if (_outputs.IsValid)
            _outputs.Geometry.ClearFrame();
        _pendingReadbackEpoch = default;
        _pendingInstanceGeneration = -1;
        _pendingResidencyPublication = null;
        _residentPageReads = BufferRangeSet.Empty;
        _framePolicyViews = default;
        _readbackWriteGeneration = -1;
        _hasPendingFrame = false;
    }

    private static QueueCompletion FindQueueCompletion(
        ReadOnlySpan<QueueCompletion> completions,
        QueueType type)
    {
        foreach (ref readonly QueueCompletion completion in completions)
            if (completion.Queue.Type == type)
                return completion;
        throw new InvalidOperationException($"Cluster frame state requires a {type} Queue completion.");
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;
        foreach (var material in _observedMaterials) material.Changed -= MaterialChanged;
        _observedMaterials.Clear();
        _materialsChanged = true;
        List<Exception>? failures = null;
        Release(ref _viewQuery, context.World, ref failures);
        Dispose(ref _uploads, ref failures);
        Dispose(ref _shadeStages, ref failures);
        Dispose(ref _rasterStages, ref failures);
        DisposeMaterialSamplers(ref failures);
        Dispose(ref _pipelines, ref failures);
        Dispose(ref _history, ref failures);
        Dispose(ref _dispatchIndirectLayout, ref failures);
        Dispose(ref _drawIndirectLayout, ref failures);
        Dispose(ref _drawIndexedIndirectLayout, ref failures);
        for (int generation = 0; generation < ReadbackGenerationCount; generation++)
        {
            if (_pageFaultReadbacks[generation] is { } pageFaultReadback)
            {
                try { pageFaultReadback.Dispose(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            _pageFaultReadbacks[generation] = null;
            _pageFaultReadbackTickets[generation] = default;
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
        _stagePasses.Clear();
        _geometryStagePasses.Clear();
        _shadeStagePasses.Clear();
        for (int phase = 0; phase < RasterEpochCount; phase++)
        {
            _binningStagePasses[phase].Clear();
            _rasterBeforeDepthStagePasses[phase].Clear();
            _hardwareRasterStagePasses[phase].Clear();
        }
        _softwareRasterPassBindings.Clear();
        _hardwareRasterPassBindings.Clear();
        _tessellationSeedPassBindings.Clear();
        _tessellationSplitPassBindings.Clear();
        _tessellationDicePassBindings.Clear();
        _tessellationHardwarePassBindings.Clear();
        _materialInputs.Clear();
        _materialBuffers.Clear();
        _shadeUniformInputs = [];
        _shadeScalars = [];
        _shadeScalarOffsets = [];
        _layerDispatches.Clear();
        _winnerResolvePasses.Clear();
        _traversalSeedPass = default;
        _traversalPass = default;
        _traversalLevelPasses.Clear();
        _primaryDeferredResetPass = default;
        _primaryDeferredResumePass = default;
        _primaryPostTraversalPass = default;
        _primaryPostTraversalLevelPasses.Clear();
        _continuationResetPass = default;
        _continuationTraversalSeedPass = default;
        _continuationTraversalPass = default;
        _continuationTraversalLevelPasses.Clear();
        _continuationCullPass = default;
        _continuationDeferredResetPass = default;
        _continuationDeferredResumePass = default;
        _continuationPostTraversalPass = default;
        _continuationPostTraversalLevelPasses.Clear();
        _continuationPostCullPass = default;
        _frameResources = default;
        _outputs = default;
        _renderContext = default;
        _shadeCompleteBefore = default;
        _depthCompleteBefore = default;
        _installedRasterStages = null;
        _installedShadeStages = null;
        _shadePasses = [];
        _installedMaterialTopologyVersion = 0;
        _graphInstalled = false;
        _recordStagePasses = false;
        _stagePassDestination = null;
        _stagePassGroupDestination = null;
        ClearPendingFrame();
        Array.Clear(_stagedViews);
        Array.Clear(_stagedPolicies);
        Array.Clear(_stagedPolicyViewIndices);
        _stagedViewCount = 0;
        _primaryViewCount = 0;
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
        IndirectCommandLayout? drawIndexedIndirectLayout = null;
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
                _visibilityMode);
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
            drawIndexedIndirectLayout = _backend.CreateIndirectCommandLayout(
                _device, new IndirectCommandLayoutDesc([new(IndirectArgumentType.DrawIndexed)],
                    ClusterIndirectAbi.DrawIndexedStride, label: "Cluster indexed draw indirect layout"));
            _drawIndexedIndirectLayout = drawIndexedIndirectLayout;
            _pipelines = pipelines;
            _dispatchIndirectLayout = dispatchIndirectLayout;
            _drawIndirectLayout = drawIndirectLayout;
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            Dispose(ref drawIndexedIndirectLayout, ref cleanupFailures);
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
            ClusterReadbackTicket ticket = _pageFaultReadbackTickets[generation];
            if (!ticket.IsValid)
                throw new InvalidOperationException("Cluster page-fault readback ticket was lost.");
            ReadMappedBuffer(pageFaultReadback, _pageFaultReadbackBytes);
            _streamingFeedback.Publish(ticket, _pageFaultReadbackBytes);
            _pageFaultReadbackTickets[generation] = default;
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
        uint phaseOneFinal = ReadUInt32(
            bytes,
            PhaseOneTessellationCountersReadbackOffset + 2 * sizeof(uint));
        uint phaseOneOverflow = ReadUInt32(
            bytes,
            PhaseOneTessellationCountersReadbackOffset + 3 * sizeof(uint));
        uint phaseOneCulled = ReadUInt32(
            bytes,
            PhaseOneTessellationCountersReadbackOffset + 4 * sizeof(uint));
        uint phaseOneQualityLimited = ReadUInt32(
            bytes,
            PhaseOneTessellationCountersReadbackOffset + 5 * sizeof(uint));
        uint phaseTwoFinal = ReadUInt32(
            bytes,
            PhaseTwoTessellationCountersReadbackOffset + 2 * sizeof(uint));
        uint phaseTwoOverflow = ReadUInt32(
            bytes,
            PhaseTwoTessellationCountersReadbackOffset + 3 * sizeof(uint));
        uint phaseTwoCulled = ReadUInt32(
            bytes,
            PhaseTwoTessellationCountersReadbackOffset + 4 * sizeof(uint));
        uint phaseTwoQualityLimited = ReadUInt32(
            bytes,
            PhaseTwoTessellationCountersReadbackOffset + 5 * sizeof(uint));
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
            SaturatingAdd(
                Math.Min(phaseOneFinal, _options.MaxCandidates),
                Math.Min(phaseTwoFinal, _options.MaxCandidates)),
            SaturatingAdd(phaseOneOverflow, phaseTwoOverflow),
            SaturatingAdd(phaseOneCulled, phaseTwoCulled),
            SaturatingAdd(phaseOneQualityLimited, phaseTwoQualityLimited),
            ReadUInt32(bytes, SoftwareDebugReadbackOffset),
            CountNonZeroVisibilityProbe(bytes));
        _frameMetricReadbackFrames[generation] = 0;
        _frameMetricReadbackPending[generation] = false;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    private static uint SaturatingAdd(uint left, uint right) =>
        left > uint.MaxValue - right ? uint.MaxValue : left + right;

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
