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
    private static readonly SomeEngine.Diagnostics.TraceSite SceneBoundsTrace = new("render", "Cluster.SceneBounds");
    private static readonly SomeEngine.Diagnostics.TraceSite RasterInputsTrace = new("render", "Cluster.RasterInputs");
    private static readonly SomeEngine.Diagnostics.TraceSite RasterStagesTrace = new("render", "Cluster.ResolveRasterStages");
    private static readonly SomeEngine.Diagnostics.TraceSite ShadeStagesTrace = new("render", "Cluster.ResolveShadeStages");
    private static readonly SomeEngine.Diagnostics.TraceSite GraphInstallTrace = new("render", "Cluster.InstallGraph");

    private const int RasterPhaseCount = 4;

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
    private QueryHandle _sceneQuery;
    private ClusterPipelineSet? _pipelines;
    private ClusterRasterStages? _rasterStages;
    private ClusterShadeStages? _shadeStages;
    private ClusterRasterStageSet? _installedRasterStages;
    private ClusterShadeStageSet? _installedShadeStages;
    private ClusterCullingHistory? _history;
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
    private ClusterRenderContext _renderContext;
    private ulong _installedMaterialTopologyVersion;
    private bool _cullingEnabled;
    private bool _graphInstalled;
    private IndirectCommandLayout? _dispatchIndirectLayout;
    private IndirectCommandLayout? _drawIndirectLayout;
    private IndirectCommandLayout? _drawIndexedIndirectLayout;
    private readonly ViewCollector _viewCollector = new();
    private ulong _metricFrameIndex;
    private ClusterFrameMetrics? _latestFrameMetrics;
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
            _sceneQuery = context.World.Query(_instances.EntityQuery);
            EnsurePipelineResources();
            _rasterStages = new ClusterRasterStages(
                _backend,
                _device,
                _visibilityMode,
                _pipelines!.DefaultRasterVertexEncoding,
                _pipelines.SoftwareRaster,
                _pipelines.HardwareRaster,
                new RasterMaterialResources(this, _stageResourceProvider));
            _shadeStages = new ClusterShadeStages(
                _backend,
                _device,
                _stageResourceProvider);
            _history = new ClusterCullingHistory(_outputDescription.ViewCount);
            _created = true;
        }
        catch
        {
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
            ReleaseIfValid(context.World, _sceneQuery);
            _sceneQuery = default;
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
        if (_graphInstalled && _resources.MaximumRasterBatchesPerCluster > GraphData.Resources.BinnedEntriesPerCluster)
            EditGraph((ref RenderGraphEdit edit) => GrowRasterWork(ref edit));
        bool assignmentsChanged = !_graphInstalled ||
            _installedMaterialTopologyVersion != assignments.TopologyVersion;
        bool materialResourcesChanged = CollectMaterialChanges(assignments);
        // User vertex/domain/visibility shaders may consume scalar inputs without
        // changing a resource binding or a raster bin. Such edits still change depth.
        bool materialDepthChanged = assignmentsChanged || assignments.ChangedMaterialIds.Any();
        if (!assignmentsChanged && !materialResourcesChanged)
        {
            ClusterMaterialScalars updated = _materialScalars.Update(_installedShadeStages!, assignments);
            if (!ReferenceEquals(updated, _materialScalars))
            {
                QueueBufferWrites(GraphData.ScalarBytes, updated.Bytes, updated.ByteChanges);
                if (updated.OffsetChanges.Length != 0)
                    QueueBufferWrites(GraphData.ScalarOffsets, updated.Offsets, updated.OffsetChanges);
                _materialScalars = updated;
            }
            RecordMaterialCasterChanges(assignments);
            AcceptMaterialChanges(assignments);
            if (materialDepthChanged) _rasterRevision = checked(_rasterRevision + 1);
            return GraphData.Outputs;
        }
        if (!_graphInstalled)
        {
            _resources.InstanceResources.Install(_graph);
            _resources.Residency.InstallGeometryBuffers(_graph);
        }
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
                GraphData.ScalarBytes = CreateBufferData(ref edit, scalars.Bytes, BufferUsages.ShaderRead, "Cluster material scalars");
                GraphData.ScalarOffsets = CreateBufferData(ref edit, scalars.Offsets, BufferUsages.ShaderRead, "Cluster material scalar offsets");
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
        RecordMaterialCasterChanges(assignments);
        AcceptMaterialChanges(assignments);
        _installedRasterStages = rasterStages;
        _installedShadeStages = shadeStages;
        _rasterStages!.AcceptChanges();
        _shadeStages!.AcceptChanges();
        _cullingEnabled = cullingEnabled;
        _graphInstalled = true;
        if (rasterChanged || materialDepthChanged) _rasterRevision = checked(_rasterRevision + 1);
        return GraphData.Outputs;
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
        ClusterRenderBinding binding = _resources.Use(
            context.ReadScope,
            batch,
            _instanceLayout);
        ClusterCullingHistory history = _history ?? throw new InvalidOperationException(
            "The Cluster culling history was not created.");
        bool historyPrepared = false;
        try
        {
            Span<ClusterViewUniforms> packedViews = _packedViews;
            packedViews.Clear();
            history.Prepare(views[..primaryViewCount], viewEntities[..primaryViewCount]);
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
                    binding.PageFaultCapacity,
                    views[0].WorldOrigin);
                packedViews[viewIndex].GeometryLodScale *= _resources.Residency.StreamingQuality;
                packedViews[viewIndex].TargetPolicy = isTargetView
                    ? _stagedPolicies[viewIndex]
                    : 0u;
                packedViews[viewIndex].TargetViewIndex = isTargetView
                    ? _stagedPolicyViewIndices[viewIndex]
                    : checked((uint)viewIndex);
                if (!isTargetView && history.TryGetPrevious(
                        viewIndex,
                        out Matrix4x4 previousView,
                        out Matrix4x4 previousProjection,
                        out WorldOrigin previousOrigin))
                {
                    ref ClusterViewUniforms uniforms = ref packedViews[viewIndex];
                    Matrix4x4.Invert(previousView, out Matrix4x4 previousInverse);
                    Vector3 previousCamera = new(previousInverse.M41, previousInverse.M42, previousInverse.M43);
                    WorldOrigin previousViewOrigin = new(previousOrigin.X + previousCamera.X,
                        previousOrigin.Y + previousCamera.Y, previousOrigin.Z + previousCamera.Z);
                    Matrix4x4 previousNearView = Matrix4x4.CreateTranslation(previousCamera) * previousView;
                    uniforms.PrevViewProj = previousNearView * previousProjection;
                    uniforms.PreviousWorldTranslation = views[0].WorldOrigin.RelativeTo(previousViewOrigin);
                    uniforms.PreviousDataTranslation = previousOrigin.RelativeTo(views[0].WorldOrigin);
                    uniforms.HasPrevHistory = 1;
                    uniforms.HiZMipCount = checked((uint)ClusterCullingHistory.CalculateMipCount(_outputDescription.Width, _outputDescription.Height));
                    uniforms.HiZInvSize = new Vector2(
                        1.0f / _outputDescription.Width,
                        1.0f / _outputDescription.Height);
                    uniforms.PrevView = previousNearView;
                    uniforms.PreviousWorldToFirstPerson = history.PreviousFirstPersonTransform(viewIndex);
                    uniforms.PrevP00 = previousProjection.M11;
                    uniforms.PrevP11 = previousProjection.M22;
                    previousViewOrigin.Split(out uniforms.PreviousWorldOriginHigh, out uniforms.PreviousWorldOriginLow);
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

            _pendingResidencyPublication = binding.Publication;
            GraphData.Outputs.Geometry.SetFrameRanges(binding.ResidentPageRanges, binding.BvhRange);
            _residentPageReads = GraphData.Outputs.Geometry.ResidentPageReads;
            _hasPendingFrame = true;

            RenderGraphFrame graph = context.Graph;
            graph.ObserveExecution(this);
            graph.SetReadbackTag(GraphData.PageFaultReadback, binding.ReadbackEpoch.Value);
            if (_options.EnableFrameMetricsReadback)
                graph.SetReadbackTag(GraphData.FrameMetricsReadback, binding.ReadbackEpoch.Value);
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
            _resources.InstanceResources.BindFrame(ref graph);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(SceneBoundsTrace))
                BindSceneBounds(ref graph, context.World, batch, policyViewCount != 0, views[0].WorldOrigin);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(ResidencyUploadsTrace))
                RecordResidencyUploads(ref graph, in binding);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(ResidencyReadsTrace))
                DeclareResidencyReads(ref graph, in binding);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(FrameUploadsTrace))
                WriteFrameUploads(
                    ref graph,
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
        FrameResources frame = GetRasterFrameResources(GraphData.Resources, policyTarget);
        _ = pass.Bind(
            graph.CreateTextureSrv(
                frame.PreviousHiZ,
                format: Format.R32Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster hierarchy history HiZ"),
            PipelineSync.ComputeShading);
        _ = pass.Bind(
            graph.CreateBufferUav(
                frame.Phase2CandidateClusters,
                structureStride: CandidateStride,
                label: "Cluster deferred hierarchy work"),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(
            graph.CreateBufferUav(
                frame.Phase2CandidateCount,
                structureStride: sizeof(uint),
                label: "Cluster deferred hierarchy count"),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Complete);
        _ = pass.Bind(graph.CreateBufferUav(frame.Phase2CandidateArgs,
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
        BindWorkStatus(ref graph, ref pass);
    }

    private void BindTargetPolicyResources(
        ref RenderGraphEdit graph,
        GraphPassId passId,
        bool policyTarget, bool workStatusFirst = false)
    {
        PassDefinition pass = graph.GetPass(passId);
        BindPolicyClusterViews(
            ref graph,
            ref pass,
            PipelineSync.ComputeShading,
            policyTarget);
        if (workStatusFirst) BindWorkStatus(ref graph, ref pass);
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
            graph.CreateBufferSrv(
                GraphData.Resources.NeutralPolicyView,
                structureStride: 4 * sizeof(uint),
                label: "Cluster neutral raster page HiZ metadata"),
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

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (!_hasPendingFrame) return;
        if (result.SubmittedCompletions.IsEmpty)
        {
            Discard();
            return;
        }
        try
        {
            bool geometryWritten = result.TryGetPassCompletion(GraphData.Outputs.Geometry.CompletePass, out _);
            _history!.FinishFrame(geometryWritten);
            foreach (GraphBufferId buffer in GraphData.BufferWrites.Keys)
                if (result.TryGetPassCompletion(GraphData.BufferDescriptions[buffer].UploadPass, out _))
                    GraphData.BufferWrites = GraphData.BufferWrites.Remove(buffer);
            if (result.TryGetPassCompletion(GraphData.SceneBounds.Pass, out _))
            {
                _sceneBatch = CurrentInstances;
                _sceneInstanceRevision = _sceneBatch?.ContentRevision ?? 0;
                _sceneGeometryRevision = GeometryRevision;
                _sceneOrigin = _frameSceneOrigin;
            }
            if (_pendingResidencyPublication is { } publication)
            {
                bool uploaded = true;
                if (publication.Meshes.PageUploads.Length != 0)
                    uploaded &= result.TryGetPassCompletion(GraphData.PageUpload, out _);
                if (publication.Meshes.Bvh.Uploads.Length != 0)
                    uploaded &= result.TryGetPassCompletion(GraphData.BvhUpload, out _);
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
        if (GraphData.Outputs.IsValid)
            GraphData.Outputs.Geometry.ClearFrame();
        _pendingResidencyPublication = null;
        _residentPageReads = BufferRangeSet.Empty;
        _framePolicyViews = default;
        _hasPendingFrame = false;
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;
        _materialBindingVersions.Clear();
        List<Exception>? failures = null;
        Release(ref _viewQuery, context.World, ref failures);
        Release(ref _sceneQuery, context.World, ref failures);
        Dispose(ref _shadeStages, ref failures);
        Dispose(ref _rasterStages, ref failures);
        DisposeMaterialSamplers(ref failures);
        Dispose(ref _pipelines, ref failures);
        _history = null;
        Dispose(ref _dispatchIndirectLayout, ref failures);
        Dispose(ref _drawIndirectLayout, ref failures);
        Dispose(ref _drawIndexedIndirectLayout, ref failures);
        _metricFrameIndex = 0;
        _latestFrameMetrics = null;
        AcceptMaterialTextures(_graphData.MaterialTextures.Clear());
        lock (_materialTextureChangeGate) _changedMaterialTextures.Clear();
        _materialScalars = ClusterMaterialScalars.Empty;
        _casterChanges.Clear();
        _casterRevision = 0;
        _lastGeometryChangeSequence = 0;
        _casterStreamingQuality = 1.0f;
        _graphData = new ClusterGraphData();
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
        _graph.CollectCompleted();
        return _latestFrameMetrics ?? throw new InvalidOperationException(
            "No completed Cluster frame metrics are available.");
    }

    private void ReceiveFrameMetrics(ReadOnlySpan<byte> bytes, ulong epoch)
    {
        if (epoch != _resources.Residency.EpochId.Value) return;
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
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(MetricsFrameIndexOffset, sizeof(ulong))),
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
            CountNonZeroVisibilityProbe(bytes))
        {
            ResidencyEpoch = epoch,
            Work = new ClusterWorkMetrics(
                (ClusterWorkOverflow)ReadUInt32(bytes, WorkMetricsReadbackOffset),
                ReadWorkMetric(bytes, 8), ReadWorkMetric(bytes, 16), ReadWorkMetric(bytes, 24),
                ReadWorkMetric(bytes, 32), ReadWorkMetric(bytes, 40), ReadWorkMetric(bytes, 48),
                ReadWorkMetric(bytes, 56), ReadWorkMetric(bytes, 64), ReadWorkMetric(bytes, 72),
                ReadWorkMetric(bytes, 80), ReadWorkMetric(bytes, 88)),
        };
        if (_latestFrameMetrics is not { } latest || metrics.FrameIndex > latest.FrameIndex)
            _latestFrameMetrics = metrics;
    }

    private static ulong ReadWorkMetric(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(WorkMetricsReadbackOffset + offset, sizeof(ulong)));

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
