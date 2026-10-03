using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS.Entities;
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
public sealed partial class ClusterPipelineSystem : ISystem<RenderFrameSystemContext>, IRenderGraphFrameExecutionObserver
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace = new("render", "Cluster.Update");
    private static readonly SomeEngine.Diagnostics.TraceSite MaterialInputsTrace = new("render", "Cluster.MaterialInputs");
    private static readonly SomeEngine.Diagnostics.TraceSite ResidencyReadsTrace = new("render", "Cluster.ResidencyReads");
    private static readonly SomeEngine.Diagnostics.TraceSite ResidencyUploadsTrace = new("render", "Cluster.ResidencyUploads");
    private static readonly SomeEngine.Diagnostics.TraceSite FrameResourcesTrace = new("render", "Cluster.FrameResources");
    private static readonly SomeEngine.Diagnostics.TraceSite FrameUploadsTrace = new("render", "Cluster.FrameUploads");
    private static readonly SomeEngine.Diagnostics.TraceSite InstanceTransferTrace = new("render", "Cluster.InstanceTransfer");
    private static readonly SomeEngine.Diagnostics.TraceSite SceneBoundsTrace = new("render", "Cluster.SceneBounds");
    private static readonly SomeEngine.Diagnostics.TraceSite RasterInputsTrace = new("render", "Cluster.RasterInputs");
    private static readonly SomeEngine.Diagnostics.TraceSite RasterStagesTrace = new("render", "Cluster.ResolveRasterStages");
    private static readonly SomeEngine.Diagnostics.TraceSite ShadeStagesTrace = new("render", "Cluster.ResolveShadeStages");
    private static readonly SomeEngine.Diagnostics.TraceSite GraphInstallTrace = new("render", "Cluster.InstallGraph");

    private const int ReadbackGenerationCount = 2;
    private const int RasterPhaseCount = 4;
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
    private readonly ClusterGraphRegions _regions;
    private readonly GraphRegionId _targetPhaseOneRegion;
    private readonly GraphRegionId _targetPhaseTwoRegion;
    private readonly ClusterPipelineOutputDescription _outputDescription;
    private readonly IGraphResourceProvider? _stageResourceProvider;
    private readonly ClusterStreamingFeedbackChannel _streamingFeedback;
    private readonly ClusterPipelineOptions _options;
    private readonly ClusterVisibilityMode _visibilityMode;
    private QueryHandle _viewQuery;
    private ClusterPipelineSet? _pipelines;
    private ClusterEntryUsageCache? _entryUsages;
    private ClusterRasterStages? _rasterStages;
    private ClusterShadeStages? _shadeStages;
    private ClusterRasterStageSet? _installedRasterStages;
    private ClusterShadeStageSet? _installedShadeStages;
    private ClusterCullingHistory? _history;
    private ClusterFrameUploads? _uploads;
    private RenderInstanceDataTransfer? _instanceDataTransfer;
    private ClusterViewUniforms[] _packedViews;
    private RenderView[] _stagedViews;
    private Entity[] _stagedViewEntities;
    private Matrix4x4[] _stagedPreviousViewProjections;
    private bool[] _stagedHasPreviousView;
    private uint[] _stagedPolicies;
    private uint[] _stagedPolicyViewIndices;
    private int _stagedViewCount;
    private int _primaryViewCount;
    private GraphBufferSlice _framePolicyViews;
    private readonly Dictionary<GraphBufferId, GraphPassId> _residencyUploads = [];
    private ClusterRenderContext _renderContext;
    private ulong _installedMaterialTopologyVersion;
    private bool _cullingEnabled;
    private bool _graphInstalled;
    private IndirectCommandLayout? _dispatchIndirectLayout;
    private IndirectCommandLayout? _drawIndirectLayout;
    private IndirectCommandLayout? _drawIndexedIndirectLayout;
    private readonly Buffer?[] _pageFaultReadbacks = new Buffer?[ReadbackGenerationCount];
    private readonly ClusterReadbackTicket[] _pageFaultReadbackTickets =
        new ClusterReadbackTicket[ReadbackGenerationCount];
    private readonly bool[] _pageFaultReadbackPending = new bool[ReadbackGenerationCount];
    private readonly QueueCompletion[] _pageFaultReadbackCompletions = new QueueCompletion[ReadbackGenerationCount];
    private readonly Buffer?[] _frameMetricReadbacks = new Buffer?[ReadbackGenerationCount];
    private byte[] _frameMetricReadbackBytes = [];
    private readonly ulong[] _frameMetricReadbackFrames = new ulong[ReadbackGenerationCount];
    private readonly bool[] _frameMetricReadbackPending = new bool[ReadbackGenerationCount];
    private readonly QueueCompletion[][] _readbackFences = [[], []];
    private readonly BufferBoundaryState[][] _pageFaultReadbackStates = [[], []];
    private readonly BufferBoundaryState[][] _frameMetricReadbackStates = [[], []];
    private readonly ulong[] _readbackSequences = new ulong[ReadbackGenerationCount];
    private readonly ViewCollector _viewCollector = new();
    private ulong _frameMetricsSubmittedFrame;
    private ulong _nextReadbackSequence;
    private int _readbackWriteGeneration = -1;
    private int _preferredReadbackGeneration;
    private ClusterFrameMetrics? _latestFrameMetrics;
    private ClusterEpochId _pendingReadbackEpoch;
    private QueueCompletion? _pageFaultSubmission;
    private int _pendingInstanceGeneration = -1;
    private ClusterResidencyPublication? _pendingResidencyPublication;
    private BufferRangeSet _residentPageReads = BufferRangeSet.Empty;
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
        in ClusterGraphRegions regions,
        in ClusterPipelineOutputDescription outputDescription,
        ClusterStreamingFeedbackChannel streamingFeedback,
        IGraphResourceProvider? stageResourceProvider = null,
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
        _stagedViewEntities = new Entity[_stagedViews.Length];
        _stagedPreviousViewProjections = new Matrix4x4[_stagedViews.Length];
        _stagedHasPreviousView = new bool[_stagedViews.Length];
        _stagedPolicies = new uint[_stagedViews.Length];
        _stagedPolicyViewIndices = new uint[_stagedViews.Length];
        _regions = regions;
        using RenderGraphEdit edit = graph.BeginEdit();
        _targetPhaseOneRegion = edit.AddRegion("Cluster target first geometry", regions.TargetGeometry);
        IntermediateOcclusionRegion = edit.AddRegion("Cluster target intermediate occlusion", regions.TargetGeometry);
        _targetPhaseTwoRegion = edit.AddRegion("Cluster target second geometry", regions.TargetGeometry);
        edit.Commit();
    }

    public ClusterPipelineOutputs Outputs => GraphData.Outputs.IsValid
        ? GraphData.Outputs
        : throw new InvalidOperationException("The Cluster retained graph has not been installed.");

    public ulong GeometryRevision => _resources.CaptureDiagnostics().Meshes.ChangeRevision;

    private ulong _rasterRevision;
    public ulong RasterRevision => _rasterRevision;

    public bool HasFiniteGeometryBounds => _installedRasterStages is { HasFiniteBounds: true };

    public RenderInstanceBatch? CurrentInstances => _instances.Current is { GroupCount: 1 } current
        ? current.Groups[0].Batch : null;

    public ClusterVisibilityMode VisibilityMode => _visibilityMode;

    /// <summary>Target-view occlusion work between the two geometry phases.</summary>
    public GraphRegionId IntermediateOcclusionRegion { get; }

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
        scoped ReadOnlySpan<Matrix4x4> previousViewProjections = default,
        scoped ReadOnlySpan<Entity> identities = default)
    {
        if (!_created)
            throw new InvalidOperationException("The Cluster pipeline was not created.");
        if (_hasPendingFrame)
            throw new InvalidOperationException("Views cannot be staged while a Cluster frame is pending.");
        int required = checked(_stagedViewCount + views.Length);
        if (views.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(views));
        if (!identities.IsEmpty && identities.Length != views.Length)
            throw new ArgumentException("View identities must match the staged view count.", nameof(identities));
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
        if (identities.IsEmpty) _stagedViewEntities.AsSpan(_stagedViewCount, views.Length).Clear();
        else identities.CopyTo(_stagedViewEntities.AsSpan(_stagedViewCount));
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
        Array.Resize(ref _stagedViewEntities, required);
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
            _entryUsages = new ClusterEntryUsageCache();
            _rasterStages = new ClusterRasterStages(
                _backend,
                _device,
                _visibilityMode,
                _pipelines!.DefaultRasterVertexEncoding,
                _pipelines.SoftwareRaster,
                _pipelines.HardwareRaster,
                _stageResourceProvider,
                _entryUsages);
            _shadeStages = new ClusterShadeStages(
                _backend,
                _device,
                _stageResourceProvider,
                _entryUsages);
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
            _entryUsages?.Dispose();
            _entryUsages = null;
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
        RefreshMaterialTextures();
        if (_graphInstalled && GraphData.TraversalLevels[0].Values.Count() < _resources.HierarchyLevels)
            ReconcileTraversal();
        bool assignmentsChanged = !_graphInstalled ||
            _installedMaterialTopologyVersion != assignments.TopologyVersion;
        bool materialResourcesChanged = CollectMaterialChanges(assignments);
        if (!assignmentsChanged && !materialResourcesChanged)
        {
            ClusterMaterialScalars updated = _materialScalars.Update(_installedShadeStages!, assignments);
            if (!ReferenceEquals(updated, _materialScalars))
            {
                _uploads!.SetChangedValue(GraphData.ScalarBytes, updated.Bytes, updated.ByteChanges);
                if (updated.OffsetChanges.Length != 0)
                    _uploads.SetChangedValue(GraphData.ScalarOffsets, updated.Offsets, updated.OffsetChanges);
                _materialScalars = updated;
            }
            AcceptMaterialChanges(assignments);
            return GraphData.Outputs;
        }
        _uploads ??= new ClusterFrameUploads(_backend, _device, ReadbackGenerationCount);
        if (!_graphInstalled)
            _instanceDataTransfer = _device.Adapter.Type == AdapterType.Discrete
                ? _resources.InstanceResources.GetDataTransfer(_graph, _regions.PrimaryGeometry) : null;
        ClusterRasterStageSet rasterStages = null!;
        ClusterShadeStageSet shadeStages = null!;
        ClusterMaterialScalars scalars = null!;
        bool cullingEnabled = false;
        bool rasterChanged = false;
        EditGraph((ref RenderGraphEdit edit) =>
        {
            using (SomeEngine.Diagnostics.EngineTrace.Begin(RasterStagesTrace))
                rasterStages = _rasterStages!.Resolve(assignments);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(ShadeStagesTrace))
                shadeStages = _shadeStages!.Resolve(assignments);
            scalars = _materialScalars.Update(shadeStages, assignments);
            cullingEnabled = _pipelines!.DefaultRasterCullingEnabled && rasterStages.HasFiniteBounds;
            rasterChanged = _installedRasterStages is null || rasterStages.ChangedBins.Count != 0 ||
                _installedRasterStages.BinIndices.Length != rasterStages.BinIndices.Length || _cullingEnabled != cullingEnabled;
            bool shadeChanged = _installedShadeStages is null || shadeStages.ChangedBins.Count != 0 ||
                _installedShadeStages.BinIndices.Length != shadeStages.BinIndices.Length;
            if (_graphInstalled)
            {
                UpdateScalarBuffers(ref edit, scalars);
                if (rasterChanged || shadeChanged)
                {
                    uint rasterBinCount = GraphData.Resources.RasterBinCount;
                    ReconcileStageResources(ref edit, assignments, rasterStages, shadeStages);
                    if (rasterChanged) GraphData.RasterPhases = ReconcileRasterPhases(ref edit, rasterStages);
                    UpdateRasterBinning(ref edit, GraphData.RasterPhases);
                    UpdateRasterCapacity(ref edit, rasterBinCount, GraphData.RasterPhases);
                    if (shadeChanged)
                    {
                        UpdateShadeBinning(ref edit);
                        GraphData.ShadeGroups = UpdateShadeGroups(ref edit, shadeStages);
                    }
                }
                else UpdateMaterialIndexBuffers(ref edit, rasterStages, shadeStages);
            }
            else
            {
                GraphData.ScalarBytes = _uploads.Declare(ref edit, scalars.Bytes, BufferUsages.ShaderRead, "Cluster material scalars");
                GraphData.ScalarOffsets = _uploads.Declare(ref edit, scalars.Offsets, BufferUsages.ShaderRead, "Cluster material scalar offsets");
                GraphData.ShadeScalars = edit.CreateBufferInput(BufferUsages.ShaderRead);
                GraphData.ShadeScalarOffsets = edit.CreateBufferInput(BufferUsages.ShaderRead);
                using (SomeEngine.Diagnostics.EngineTrace.Begin(GraphInstallTrace))
                    GraphData.RasterPhases = InstallGraph(ref edit, in _outputDescription, assignments, rasterStages, shadeStages);
            }
            var materialResources = UpdateMaterialResources(ref edit, rasterStages, shadeStages);
            GraphData.MaterialBuffers = materialResources.Buffers;
            GraphData.MaterialTextures = materialResources.Textures;
        });
        _materialScalars = scalars;
        _installedMaterialTopologyVersion = assignments.TopologyVersion;
        AcceptMaterialChanges(assignments);
        _installedRasterStages = rasterStages;
        _installedShadeStages = shadeStages;
        _rasterStages!.AcceptChanges();
        _shadeStages!.AcceptChanges();
        _cullingEnabled = cullingEnabled;
        _graphInstalled = true;
        _geometryPassesEnabled = null;
        if (rasterChanged) _rasterRevision = checked(_rasterRevision + 1);
        return GraphData.Outputs;
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
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
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
            ReadOnlySpan<Entity> viewEntities;
            int primaryViewCount;
            if (_stagedViewCount != 0)
            {
                views = _stagedViews.AsSpan(0, _stagedViewCount);
                viewEntities = _stagedViewEntities.AsSpan(0, _stagedViewCount);
                primaryViewCount = _primaryViewCount;
            }
            else
            {
                context.World.ExecuteQuery(
                    _viewQuery,
                    ref viewCollector,
                    static (QueryCursor cursor, ref ViewCollector state) => state.Collect(cursor));
                views = CollectionsMarshal.AsSpan(viewCollector.Views);
                viewEntities = CollectionsMarshal.AsSpan(viewCollector.Entities);
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

            RecordFrame(ref context, batch, views, viewEntities, primaryViewCount);
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
        scoped ReadOnlySpan<Entity> viewEntities,
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
                views[..primaryViewCount], viewEntities[..primaryViewCount]);
            historyPrepared = true;
            for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                bool isTargetView = viewIndex >= primaryViewCount;
                ClusterViewRasterMode rasterMode = _visibilityMode == ClusterVisibilityMode.HardwareOnly ||
                    (isTargetView && _renderContext.HardwareRasterOnly)
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
            GraphData.Outputs.Geometry.SetFrameRanges(binding.ResidentPageRanges, binding.BvhRange);
            _residentPageReads = GraphData.Outputs.Geometry.ResidentPageReads;
            _hasPendingFrame = true;

            RenderGraphFrame graph = context.Graph;
            graph.ObserveExecution(this);
            ClusterRenderDiagnostics diagnostics = _resources.CaptureDiagnostics();
            bool streamWork = diagnostics.Meshes.PublishedMeshes != diagnostics.Meshes.RegisteredMeshes ||
                diagnostics.Residency.HasPendingPublication || diagnostics.Residency.QueuedPageLoads != 0 ||
                diagnostics.Residency.ActivePageLoads != 0 || diagnostics.Residency.PendingPageLoads != 0;
            graph.SetPassOptions(GraphData.Feedback[FeedbackKind.PageFaults],
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true) { SubmitAfter = streamWork });
            int policyViewCount = views.Length - primaryViewCount;
            _framePolicyViews = policyViewCount == 0
                ? new GraphBufferSlice(GraphData.Resources.PackedViews, BufferRange.Whole)
                : graph.Upload(
                    packedViews.Slice(primaryViewCount, policyViewCount),
                    BufferUsages.ShaderRead,
                    "Cluster active policy views");
            if (_renderContext.IsValid)
                _renderContext.InvocationSourceBinder(ref graph, _framePolicyViews);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(FrameResourcesTrace))
                BindFrameResources(
                    ref graph,
                    in binding,
                    history);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(InstanceTransferTrace))
                _instanceDataTransfer?.Stage(ref graph, batch, binding.InstanceData);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(SceneBoundsTrace))
                BindSceneBounds(ref graph, batch, policyViewCount != 0);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(ResidencyUploadsTrace))
                RecordResidencyUploads(ref graph, in binding);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(ResidencyReadsTrace))
                DeclareResidencyReads(ref graph, in binding);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(FrameUploadsTrace))
                WriteFrameUploads(
                    ref graph,
                    RequireReadbackWriteGeneration(),
                    _installedRasterStages ?? throw new InvalidOperationException("Raster stages were not installed."),
                    _installedShadeStages ?? throw new InvalidOperationException("Shade stages were not installed."),
                    packedViews[..primaryViewCount],
                    primaryViewCount);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(RasterInputsTrace))
                UpdateRasterDispatches(ref graph);
            UpdateShadeInputs(ref graph, _installedShadeStages!);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(MaterialInputsTrace))
                UpdateMaterialInputs(ref graph);
            graph.SetPassData(
                GraphData.PrimarySeed.Pass,
                new ClusterDispatchParameters(new DispatchArguments(
                    checked((checked((uint)binding.DispatchExtent) + 63u) / 64u),
                    checked((checked((uint)primaryViewCount) + 31u) / 32u),
                    1)));
            foreach (ClusterLayerDispatch dispatch in GraphData.LayerDispatches)
            {
                graph.SetPassData(
                    dispatch.Pass,
                    new ClusterDispatchParameters(new DispatchArguments(
                        dispatch.GroupX,
                        dispatch.GroupY,
                        checked((uint)primaryViewCount))));
            }
            foreach (ClusterLayerDispatch dispatch in GraphData.WinnerDispatches)
            {
                graph.SetPassData(
                    dispatch.Pass,
                    new ClusterFullscreenParameters(
                        _outputDescription.Width,
                        _outputDescription.Height,
                        checked((uint)primaryViewCount)));
            }
            bool initializeHistory = history.RequiresInitialization;
            graph.SetRegionEnabled(GraphData.HistoryInitialization, initializeHistory);
        }
        catch
        {
            if (binding.Publication is { } publication)
                _resources.RetainResidencyPublication(publication);
            if (historyPrepared)
                history.Discard();
            ClearPendingFrame();
            throw;
        }
    }

    private void BindNeutralInvocationCount(
        ref RenderGraphEdit graph,
        ref PassDefinition pass)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                GraphData.Resources.NeutralPolicyValue,
                range: new BufferRange(2u * sizeof(uint), sizeof(uint)),
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
                GraphData.Resources.PreviousHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster hierarchy history HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferUav(
                GraphData.Resources.Phase2CandidateClusters,
                structureStride: CandidateStride,
                label: "Cluster deferred hierarchy work"),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(
            graph.CreateBufferUav(
                GraphData.Resources.Phase2CandidateCount,
                structureStride: sizeof(uint),
                label: "Cluster deferred hierarchy count"),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(GraphData.Resources.Phase2CandidateArgs,
                label: "Cluster deferred hierarchy dispatch arguments"),
            PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete);
        BindPolicyClusterViews(
            ref graph,
            ref pass,
            PipelineSync.ComputeShading,
            policyTarget);
        if (policyTarget)
            _renderContext.FilterResourcesBinder(ref graph, ref pass, PipelineSync.ComputeShading);
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
            _renderContext.FilterResourcesBinder(ref graph, ref pass, PipelineSync.ComputeShading);
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
            _renderContext.InvocationViewsBinder(ref graph, ref pass, sync);
            return;
        }
        GraphBufferId views = GraphData.Resources.PackedViews;
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
                GraphData.Resources.NeutralPolicyView,
                structureStride: ClusterRenderContext.TargetViewSizeInBytes,
                label: "Cluster neutral raster target view"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                GraphData.Resources.NeutralPolicyView,
                structureStride: sizeof(uint),
                label: "Cluster neutral raster page masks"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                GraphData.Resources.NeutralPolicyView,
                structureStride: 4 * sizeof(uint),
                label: "Cluster neutral raster page table"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                GraphData.Resources.NeutralPolicyView,
                structureStride: 2 * sizeof(uint),
                label: "Cluster neutral raster receiver masks"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                GraphData.Resources.PreviousHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster neutral current target HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                GraphData.Resources.PreviousHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster neutral previous target HiZ"),
            PipelineSync.ComputeShading);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphSubmitted(RenderGraphFrameResult result)
    {
        if (!_hasPendingFrame) return;
        RegisterPageFaultReadback(result);
        DrainCompletedReadbacks();
    }

    private void RegisterPageFaultReadback(RenderGraphFrameResult result)
    {
        if (_pageFaultSubmission is not null ||
            !result.TryGetPassCompletion(GraphData.Feedback[FeedbackKind.PageFaults], out QueueCompletion completion)) return;
        int generation = RequireReadbackWriteGeneration();
        ulong sequence = checked(++_nextReadbackSequence);
        _readbackSequences[generation] = sequence;
        _pageFaultReadbackTickets[generation] = new ClusterReadbackTicket(_pendingReadbackEpoch, sequence);
        _pageFaultReadbackCompletions[generation] = completion;
        _pageFaultReadbackPending[generation] = true;
        _pageFaultSubmission = completion;
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (!_hasPendingFrame) return;
        if (result.SubmittedCompletions.IsEmpty)
        {
            Discard();
            return;
        }
        int generation = RequireReadbackWriteGeneration();
        // The generation includes upload data as well as readbacks. Any accepted queue
        // may still read it, even when neither readback pass reached submission.
        _readbackFences[generation] = result.SubmittedCompletions.ToArray();
        _pageFaultReadbackStates[generation] = result.GetBufferStates(GraphData.Resources.PageFaultReadback).ToArray();
        if (_options.EnableFrameMetricsReadback)
            _frameMetricReadbackStates[generation] = result.GetBufferStates(GraphData.Resources.FrameMetricsReadback).ToArray();
        _preferredReadbackGeneration = generation;
        try
        {
            _history!.FinishFrame(result, GraphData.Resources.PreviousHiZ, GraphData.Resources.CurrentHiZ,
                result.TryGetPassCompletion(GraphData.Outputs.Geometry.CompletePass, out _));
            _uploads!.FinishFrame(generation, result);
            if (_instanceDataTransfer is null)
                _resources.StoreInstanceReadStates(_pendingInstanceGeneration,
                    result.GetBufferStates(GraphData.Resources.InstanceData),
                    result.GetBufferStates(GraphData.Resources.InstanceProperties));
            else
                _resources.InstanceResources.StoreMetadataReadStates(_pendingInstanceGeneration,
                    result.GetBufferStates(GraphData.Resources.InstanceProperties));
            RegisterPageFaultReadback(result);
            DrainCompletedReadbacks();
            if (_options.EnableFrameMetricsReadback &&
                result.TryGetPassCompletion(GraphData.Feedback[FeedbackKind.Candidates], out _) &&
                result.TryGetPassCompletion(GraphData.Feedback[FeedbackKind.FrameMetrics], out _))
            {
                _frameMetricReadbackFrames[generation] = checked(++_frameMetricsSubmittedFrame);
                _frameMetricReadbackPending[generation] = true;
            }
            if (result.TryGetPassCompletion(GraphData.SceneBounds.Pass, out _))
            {
                _sceneBatch = CurrentInstances;
                _sceneInstanceRevision = _sceneBatch?.ContentRevision ?? 0;
                _sceneGeometryRevision = GeometryRevision;
            }
            if (_pendingResidencyPublication is { } publication)
            {
                bool uploaded = true;
                foreach (GraphPassId pass in _residencyUploads.Values)
                    uploaded &= result.TryGetPassCompletion(pass, out _);
                if (uploaded)
                    _resources.CommitResidencyPublication(publication, CaptureRetirementCompletions(publication));
                else
                    _resources.RetainResidencyPublication(publication);
            }
        }
        finally { ClearPendingFrame(); }
    }

    /// <summary>Rejects authoring state when graph compilation, recording, or submission fails.</summary>
    private void Discard()
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
        if (GraphData.Outputs.IsValid)
            GraphData.Outputs.Geometry.ClearFrame();
        _pendingReadbackEpoch = default;
        _pageFaultSubmission = null;
        _pendingInstanceGeneration = -1;
        _pendingResidencyPublication = null;
        _residencyUploads.Clear();
        _residentPageReads = BufferRangeSet.Empty;
        _framePolicyViews = default;
        _readbackWriteGeneration = -1;
        _hasPendingFrame = false;
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;
        _materialBindingVersions.Clear();
        List<Exception>? failures = null;
        Release(ref _viewQuery, context.World, ref failures);
        Dispose(ref _uploads, ref failures);
        Dispose(ref _shadeStages, ref failures);
        Dispose(ref _rasterStages, ref failures);
        Dispose(ref _entryUsages, ref failures);
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
            _pageFaultReadbackStates[generation] = [];
            _frameMetricReadbackStates[generation] = [];
            _pageFaultReadbackTickets[generation] = default;
            _pageFaultReadbackPending[generation] = false;
            _pageFaultReadbackCompletions[generation] = default;

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
        _frameMetricReadbackBytes = [];
        _frameMetricsSubmittedFrame = 0;
        _nextReadbackSequence = 0;
        _readbackWriteGeneration = -1;
        _preferredReadbackGeneration = 0;
        _latestFrameMetrics = null;
        AcceptMaterialTextures(_graphData.MaterialTextures.Clear());
        _materialScalars = ClusterMaterialScalars.Empty;
        _graphData = new ClusterGraphData();
        _instanceDataTransfer = null;
        _renderContext = default;
        _installedRasterStages = null;
        _installedShadeStages = null;
        _installedMaterialTopologyVersion = 0;
        _graphInstalled = false;
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
            if (_pageFaultReadbacks[0]!.Info.Size != (ulong)byteCount)
            {
                throw new InvalidOperationException(
                    "The Cluster page-fault capacity changed inside a live renderer epoch.");
            }
            return;
        }

        CreateReadbackBuffers(
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
            (CanUseReadbackStorage(0) ? 1 : 0) +
            (CanUseReadbackStorage(1) ? 1 : 0);
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
        bool preferredAvailable = CanUseReadbackStorage(preferred);
        bool alternateAvailable = CanUseReadbackStorage(alternate);
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
            if (!_backend.IsComplete(_pageFaultReadbackCompletions[generation]))
                break;

            Buffer pageFaultReadback =
                _pageFaultReadbacks[generation] is { } value
                    ? value
                    : throw new InvalidOperationException(
                        "Cluster page-fault readback ownership was lost.");
            ClusterReadbackTicket ticket = _pageFaultReadbackTickets[generation];
            if (!ticket.IsValid)
                throw new InvalidOperationException("Cluster page-fault readback ticket was lost.");
            using (MappedBuffer mapping = _backend.Map(pageFaultReadback, MapType.Read, BufferRange.Whole))
            {
                mapping.Invalidate(mapping.Range);
                _streamingFeedback.Publish(ticket, mapping.Bytes);
            }
            _pageFaultReadbackTickets[generation] = default;
            _pageFaultReadbackPending[generation] = false;
            _pageFaultReadbackCompletions[generation] = default;
        }
        for (int generation = 0; generation < ReadbackGenerationCount; generation++)
            if (_frameMetricReadbackPending[generation] && WaitForAll(_readbackFences[generation], TimeSpan.Zero))
                DrainFrameMetricsReadback(generation);
    }

    private bool CanUseReadbackStorage(int generation) =>
        !_pageFaultReadbackPending[generation] && !_frameMetricReadbackPending[generation] &&
        WaitForAll(_readbackFences[generation], TimeSpan.Zero);

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
        _frameMetricReadbackBytes = new byte[FrameMetricsReadbackByteSize];
        CreateReadbackBuffers(
            _frameMetricReadbacks,
            FrameMetricsReadbackByteSize,
            "Cluster frame metrics readback");
    }

    private void CreateReadbackBuffers(
        Buffer?[] slots,
        int byteCount,
        string label)
    {
        if (slots.Length != ReadbackGenerationCount || slots[0] is not null || slots[1] is not null)
            throw new InvalidOperationException("Cluster readback slots are not empty.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

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
        var metrics = new ClusterFrameMetrics(
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
        if (_latestFrameMetrics is not { } latest || metrics.FrameIndex > latest.FrameIndex)
            _latestFrameMetrics = metrics;
        _frameMetricReadbackFrames[generation] = 0;
        _frameMetricReadbackPending[generation] = false;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    private static uint SaturatingAdd(uint left, uint right) =>
        left > uint.MaxValue - right ? uint.MaxValue : left + right;

    private uint CountNonZeroVisibilityProbe(ReadOnlySpan<byte> bytes)
    {
        uint result = 0;
        for (int index = 0; index < Math.Min(VisibilityProbePixelCount, _outputDescription.Width); index++)
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
        internal List<Entity> Entities { get; } = [];

        internal void Clear() { Views.Clear(); Entities.Clear(); }

        internal void Collect(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
            {
                Views.Add(row.Read<RenderView>());
                Entities.Add(row.Entity);
            }
        }
    }

}
