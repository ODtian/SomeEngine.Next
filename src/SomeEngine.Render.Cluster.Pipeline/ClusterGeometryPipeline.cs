using SomeEngine.Render;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using System.Collections.Immutable;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;
using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Lighting;
using Buffer = SomeEngine.Graphics.Buffer;
using SomeEngine.Render.Materials;
using EngineTexture = SomeEngine.Resources.Texture;
using SlangShaderSharp;
using System.Runtime.CompilerServices;
using MeshOptimizer;
using SomeEngine.Resources.Geometry;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;
public sealed partial class ClusterGeometryPipeline : ISystem<RenderFrameSystemContext>, IRenderGeometryData
{
    internal readonly ClusterShadingPipeline _shading;
    internal readonly ClusterGraphInstaller _installer;
    internal readonly IRenderBoundsSource? _deformBounds;
    internal ulong _sceneDeformRevision;
    internal void BindDeformBounds(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        BindDeformBounds(ref graph, ref pass, previous: false);
        BindDeformBounds(ref graph, ref pass, previous: true);
    }

    internal void BindDeformBounds(ref RenderGraphEdit graph, ref PassDefinition pass, bool previous)
    {
        if (_deformBounds is { } bounds)
        {
            pass.Bind(graph.CreateBufferSrv(previous ? bounds.PreviousBounds : bounds.CurrentBounds, structureStride: 16), PipelineSync.ComputeShading);
            graph.OrderAfter(pass.Id, bounds.CompletePass);
        }
        else
        {
            pass.Bind(graph.CreateBufferSrv(_installer.GraphData.Resources.NeutralPolicyView, structureStride: 16), PipelineSync.ComputeShading);
        }
    }

    internal static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace = new("render", "Cluster.Update");
    internal static readonly SomeEngine.Diagnostics.TraceSite MaterialInputsTrace = new("render", "Cluster.MaterialInputs");
    internal static readonly SomeEngine.Diagnostics.TraceSite ResidencyReadsTrace = new("render", "Cluster.ResidencyReads");
    internal static readonly SomeEngine.Diagnostics.TraceSite ResidencyUploadsTrace = new("render", "Cluster.ResidencyUploads");
    internal static readonly SomeEngine.Diagnostics.TraceSite FrameResourcesTrace = new("render", "Cluster.FrameResources");
    internal static readonly SomeEngine.Diagnostics.TraceSite FrameUploadsTrace = new("render", "Cluster.FrameUploads");
    internal static readonly SomeEngine.Diagnostics.TraceSite SceneBoundsTrace = new("render", "Cluster.SceneBounds");
    internal static readonly SomeEngine.Diagnostics.TraceSite RasterInputsTrace = new("render", "Cluster.RasterInputs");
    internal static readonly SomeEngine.Diagnostics.TraceSite RasterStagesTrace = new("render", "Cluster.ResolveRasterStages");
    internal static readonly SomeEngine.Diagnostics.TraceSite ShadeStagesTrace = new("render", "Cluster.ResolveShadeStages");
    internal static readonly SomeEngine.Diagnostics.TraceSite GraphInstallTrace = new("render", "Cluster.InstallGraph");
    internal const int RasterPhaseCount = 5;
    internal readonly IGraphicsBackend _backend;
    internal readonly ShaderLibrary _shaders;
    internal readonly Device _device;
    internal readonly ClusterRenderResources _resources;
    internal readonly IRenderInstanceBatchSource<RenderInstanceSingleGroup> _instances;
    internal readonly RenderInstancePropertyLayout _instanceLayout;
    internal ClusterShaders _configuration;
    internal readonly ClusterGraphRegions _regions;
    internal readonly GraphRegionId _targetPhaseOneRegion;
    internal readonly GraphRegionId _targetPhaseTwoRegion;
    internal readonly ClusterPipelineOutputDescription _outputDescription;

    internal readonly RenderFeedback<ClusterPageDemand> _streamingFeedback;
    internal ClusterPipelineOptions _options;
    internal readonly ClusterVisibilityMode _visibilityMode;
    internal QueryHandle _viewQuery;
    internal QueryHandle _sceneQuery;
    internal ClusterPipelineSet? _pipelines;
    internal ClusterCullingHistory? _history;
    internal RenderRasterContext _renderContext;
    internal bool _cullingEnabled;
    internal IndirectCommandLayout? _dispatchIndirectLayout;
    internal IndirectCommandLayout? _drawIndirectLayout;
    internal IndirectCommandLayout? _drawIndexedIndirectLayout;
    internal ulong _metricFrameIndex;
    public WorldOrigin PreviousBoundsOrigin => _sceneOrigin;
    internal ClusterFrameMetrics? _latestFrameMetrics;
    internal bool _created;
    public ClusterGeometryPipeline(IGraphicsBackend backend, Device device, RenderDevice renderDevice, ShaderLibrary shaders, ClusterRenderResources resources, IRenderInstanceBatchSource<RenderInstanceSingleGroup> instances, RenderInstancePropertyLayout instanceLayout, ClusterMaterialAssignments materialAssignments, ClusterShaders configuration, global::SomeEngine.RenderGraph.RenderGraph graph, in ClusterGraphRegions regions, in ClusterPipelineOutputDescription outputDescription, RenderFeedback<ClusterPageDemand> streamingFeedback, ClusterShadingPipeline shading, ClusterGraphInstaller installer, IGraphResourceProvider? stageResourceProvider = null, ClusterPipelineOptions? options = null, IRenderBoundsSource? deformBounds = null)
    {
        _shading = shading;
        _installer = installer;
        shading.Attach(this, installer);
        installer.Attach(this, shading);
        _shaders = shaders;
        _deformBounds = deformBounds;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (!ReferenceEquals(renderDevice.Device, device))
            throw new ArgumentException("The Cluster pipeline and RenderDevice must use the same Device.", nameof(renderDevice));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _instanceLayout = instanceLayout ?? throw new ArgumentNullException(nameof(instanceLayout));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("The Cluster pipeline and RenderGraph must use the same Device.", nameof(graph));
        _outputDescription = outputDescription;
        _streamingFeedback = streamingFeedback ?? throw new ArgumentNullException(nameof(streamingFeedback));

        _options = options ?? new ClusterPipelineOptions();
        _visibilityMode = _options.SelectVisibilityMode(device.Capabilities);
        using RenderGraphEdit edit = graph.BeginEdit();
        RenderGraphEdit graphEdit = edit;
        _regions = ClusterGraphInstaller.CreateGraphRegions(ref graphEdit, regions);
        _targetPhaseOneRegion = regions.TargetPhaseOne != default ? regions.TargetPhaseOne : edit.AddRegion("Cluster target first geometry", _regions.TargetGeometry);
        _targetOcclusionRegion = regions.TargetOcclusion != default ? regions.TargetOcclusion : edit.AddRegion("Cluster target intermediate occlusion", _regions.TargetGeometry);
        _targetPhaseTwoRegion = regions.TargetPhaseTwo != default ? regions.TargetPhaseTwo : edit.AddRegion("Cluster target second geometry", _regions.TargetGeometry);
        edit.Commit();
    }

    public RenderGeometryResources Geometry => Outputs.Geometry;
    public RenderWorld World => _resources.World;
    public IRenderInstanceBatchSource<RenderInstanceSingleGroup> Instances => _instances;
    public ClusterPipelineOutputs Outputs => _installer.GraphData.Outputs.IsValid ? _installer.GraphData.Outputs : throw new InvalidOperationException("The Cluster retained graph has not been installed.");
    public ulong GeometryRevision => _resources.CaptureDiagnostics().Meshes.ChangeRevision;

    internal ulong _rasterRevision;
    public ulong RasterRevision => _rasterRevision;
    public bool HasFiniteGeometryBounds => _installer._installedRasterStages is { HasFiniteBounds: true };
    public RenderInstanceBatch? CurrentInstances => _instances.Current is { GroupCount: 1 } current ? current.Groups[0].Batch : null;
    public ClusterVisibilityMode VisibilityMode => _visibilityMode;
    public ClusterShadingPath ShadingPath => _options.ShadingPath;
    public uint DebugMode => _options.DebugMode;
    /// <summary>The shading path committed by the last successful graph reconciliation.</summary>
    public ClusterShadingPath InstalledShadingPath => _installer._installedShadeStages?.ShadingPath ?? throw new InvalidOperationException("The Cluster retained graph has not been installed.");

    /// <summary>Selects saved shaders at the next frame-boundary ReconcileGraph call.</summary>
    public void SetShadingPath(ClusterShadingPath path)
    {
        if (path != ClusterShadingPath.Deferred && path != ClusterShadingPath.Forward)
            throw new ArgumentOutOfRangeException(nameof(path));
        if (_options.ShadingPath != path)
            _options = _options with
            {
                ShadingPath = path
            };
    }

    /// <summary>Selects a saved debug specialization at the next frame-boundary ReconcileGraph call.</summary>
    public void SetDebugMode(uint mode)
    {
        if (_options.DebugMode != mode)
            _options = _options with
            {
                DebugMode = mode
            };
    }

    internal readonly GraphRegionId _targetOcclusionRegion;
    public GraphReadbackId PageFaultReadback => _installer.GraphData.PageFaultReadback;

    public int CurrentInstanceCount
    {
        get
        {
            RenderInstanceBatches<RenderInstanceSingleGroup>? current = _instances.Current;
            return current is { GroupCount: 1 } ? current.Groups[0].Batch.InstanceCount : 0;
        }
    }

    /// <summary>
    /// Supplies feature-owned raster views and refreshes their target groups at a cold boundary.
    /// Cluster consumes only flat view, destination, and occlusion metadata; the feature owns all
    /// conversion from its sparse representation and all interpretation of the target output.
    /// </summary>
    public void ConfigureRenderContext(in RenderRasterContext context)
    {
        if (!context.IsValid)
            throw new ArgumentException("The render context is incomplete.", nameof(context));
        _renderContext = context;
        if (_installer._graphInstalled) _installer.ReconfigureRenderContext();
    }

    public void OnCreate(ref RenderFrameSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The Cluster pipeline is already created.");
        _options.Validate();
        QueryHandle view = default;
        try
        {
            view = context.World.Query(new QueryDefinitionBuilder().Read<RenderView>().Optional<RenderViewTarget>(QueryAccess.Read).Optional<RenderPreviousView>(QueryAccess.Read));
            _viewQuery = view;
            _sceneQuery = context.World.Query(_instances.EntityQuery);
            _instanceChanges = new RenderInstanceChangeData(context.World, _instances, _sceneQuery);
            EnsurePipelineResources();
            _shading._rasterStages = new ClusterRasterStages(_backend, _device, _shading._renderDevice.Pipelines, _visibilityMode, new ClusterShadingPipeline.RasterMaterialResources(this, _shading._stageResourceProvider));
            _shading._shadeStages = new ClusterShadeStages(_backend, _device, _shading._renderDevice.Pipelines, _shading._stageResourceProvider);
            _history = new ClusterCullingHistory(_outputDescription.ViewCount);
            _created = true;
        }
        catch
        {
            _history = null;
            _shading._shadeStages?.Dispose();
            _shading._shadeStages = null;
            _shading._rasterStages?.Dispose();
            _shading._rasterStages = null;
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

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        if (!_created)
            throw new InvalidOperationException("The Cluster pipeline was not created.");
        if (!_installer._graphInstalled)
            throw new InvalidOperationException("ReconcileGraph must install Cluster before frame execution.");
        RenderInstanceBatches<RenderInstanceSingleGroup>? current = _instances.Current;
        if (current is null || current.GroupCount != 1)
        {
            throw new InvalidOperationException("The Cluster pipeline requires one unclassified instance batch.");
        }

        RenderInstanceBatch batch = current.Groups[0].Batch;
        ClusterMaterialAssignmentSet materialAssignments = _shading._materialAssignments.Current;
        if (materialAssignments.TopologyVersion != _installer._installedMaterialTopologyVersion)
            throw new InvalidOperationException("Cluster material topology was not reconciled before the frame.");
        var views = new ViewCollector();
        context.World.ExecuteQuery(_viewQuery, ref views, static (QueryCursor query, ref ViewCollector data) => data.Collect(query));
        views.Values.Sort(static (left, right) =>
        {
            int target = left.Target.Kind.CompareTo(right.Target.Kind);
            if (target != 0) return target;
            return left.Target.Kind == ViewTargetKind.Screen
                ? left.Entity.Index.CompareTo(right.Entity.Index)
                : left.Target.TargetIndex.CompareTo(right.Target.TargetIndex);
        });
        RenderView[] viewData = views.Values.Select(value => value.View).ToArray();
        Entity[] entities = views.Values.Select(value => value.Entity).ToArray();
        RenderViewTarget[] targets = views.Values.Select(value => value.Target).ToArray();
        RenderPreviousView? [] previous = views.Values.Select(value => value.Previous).ToArray();
        int primaryViewCount = targets.Count(value => value.Kind == ViewTargetKind.Screen);
        RecordFrame(ref context, batch, viewData, entities, primaryViewCount, targets, previous);
    }

    internal void RecordFrame(ref RenderFrameSystemContext context, RenderInstanceBatch batch, scoped ReadOnlySpan<RenderView> views, scoped ReadOnlySpan<Entity> viewEntities, int primaryViewCount, ReadOnlySpan<RenderViewTarget> targets, ReadOnlySpan<RenderPreviousView?> previous)
    {
        ClusterRenderBinding binding = _resources.Use(batch, _instanceLayout);
        RenderGraphFrame graph = context.Graph;
        bool writeGeometry = BeginGeometryFrame(ref graph, binding.Changes);
        FrameData frameData = GetFrameData(ref graph);
        frameData.Origin = views[0].WorldOrigin;
        graph.ObserveExecution(frameData);
        ClusterCullingHistory history = _history ?? throw new InvalidOperationException("The Cluster culling history was not created.");
        {
            Span<ClusterViewUniforms> packedViews = new ClusterViewUniforms[views.Length];
            WorldOrigin dataOrigin = views[0].WorldOrigin;
            ClusterCullingHistory.HistorySubmission historyData = history.Prepare(ref graph, views[..primaryViewCount], viewEntities[..primaryViewCount]);
            for (int viewIndex = 0; viewIndex < views.Length; viewIndex++)
            {
                bool isTargetView = targets[viewIndex].Kind == ViewTargetKind.ShadowPages;
                ClusterViewRasterMode rasterMode = _visibilityMode == ClusterVisibilityMode.HardwareOnly || (isTargetView && _renderContext.HardwareRasterOnly) ? ClusterViewRasterMode.Hardware : ClusterViewRasterMode.Adaptive;
                packedViews[viewIndex] = ClusterViewUniforms.Create(in views[viewIndex], _options, rasterMode, _cullingEnabled, binding.DispatchExtent, binding.PageFaultCapacity, dataOrigin);
                packedViews[viewIndex].UpdateFrameData(in views[viewIndex], rasterMode, _cullingEnabled, binding.DispatchExtent, binding.PageFaultCapacity, _resources.Residency.StreamingQuality);
                packedViews[viewIndex].TargetViewIndex = targets[viewIndex].Kind == ViewTargetKind.Screen ? checked((uint)viewIndex) : targets[viewIndex].TargetIndex;
                if (viewIndex < primaryViewCount && historyData.TryGetPrevious(viewIndex, out Matrix4x4 previousView, out Matrix4x4 previousProjection, out WorldOrigin previousOrigin))
                {
                    ref ClusterViewUniforms uniforms = ref packedViews[viewIndex];
                    Matrix4x4.Invert(previousView, out Matrix4x4 previousInverse);
                    Vector3 previousCamera = new(previousInverse.M41, previousInverse.M42, previousInverse.M43);
                    WorldOrigin previousViewOrigin = new(previousOrigin.X + previousCamera.X, previousOrigin.Y + previousCamera.Y, previousOrigin.Z + previousCamera.Z);
                    Matrix4x4 previousNearView = Matrix4x4.CreateTranslation(previousCamera) * previousView;
                    uniforms.PrevViewProj = previousNearView * previousProjection;
                    uniforms.PreviousWorldTranslation = views[0].WorldOrigin.RelativeTo(previousViewOrigin);
                    uniforms.PreviousDataTranslation = previousOrigin.RelativeTo(views[0].WorldOrigin);
                    uniforms.HasPrevHistory = 1;
                    uniforms.HiZMipCount = checked((uint)ClusterCullingHistory.CalculateMipCount(_outputDescription.Width, _outputDescription.Height));
                    uniforms.HiZInvSize = new Vector2(1.0f / _outputDescription.Width, 1.0f / _outputDescription.Height);
                    uniforms.PrevView = previousNearView;
                    uniforms.PreviousWorldToFirstPerson = historyData.PreviousFirstPersonTransform(viewIndex);
                    uniforms.PrevP00 = previousProjection.M11;
                    uniforms.PrevP11 = previousProjection.M22;
                    previousViewOrigin.Split(out uniforms.PreviousWorldOriginHigh, out uniforms.PreviousWorldOriginLow);
                }
                else if (isTargetView && _renderContext.IsValid)
                {
                    ref ClusterViewUniforms uniforms = ref packedViews[viewIndex];
                    if (previous[viewIndex] is { } previousTarget)
                    {
                        uniforms.PrevViewProj = previousTarget.ViewProjection;
                        uniforms.HasPrevHistory = 1u;
                    }

                    uniforms.HiZMipCount = _renderContext.OcclusionMipCount;
                    uniforms.HiZInvSize = new Vector2(1.0f / _renderContext.OcclusionTexelSize, 1.0f / _renderContext.OcclusionTexelSize);
                }
                else if (targets[viewIndex].Kind == ViewTargetKind.LayerTiles)
                {
                    packedViews[viewIndex].HiZMipCount = checked((uint)ClusterCullingHistory.CalculateMipCount(_outputDescription.Width, _outputDescription.Height));
                    packedViews[viewIndex].HiZInvSize = new Vector2(1.0f / _outputDescription.Width, 1.0f / _outputDescription.Height);
                }
            }

            _installer.GraphData.Outputs.Geometry.SetFrameRanges(ref graph, binding.ResidentPageRanges, binding.BvhRanges);
            if (_installer.GraphData.LayerTileSize != 0u)
                _installer.GraphData.LayerGeometry.SetFrameRanges(ref graph, binding.ResidentPageRanges, binding.BvhRanges);
            graph.SetReadbackTag(_installer.GraphData.PageFaultReadback, graph.FrameIdentity);
            graph.SetReadbackTag(_installer.GraphData.RasterBatchReadback, graph.FrameIdentity);
            if (_options.EnableFrameMetricsReadback)
                graph.SetReadbackTag(_installer.GraphData.FrameMetricsReadback, binding.ReadbackEpoch.Value);
            int policyViewCount = 0;
            foreach (RenderViewTarget target in targets)
                if (target.Kind == ViewTargetKind.ShadowPages) policyViewCount++;
            GraphBufferSlice policyViews = policyViewCount == 0 ? new GraphBufferSlice(_installer.GraphData.Resources.PackedViews, BufferRange.Whole) : graph.Upload(packedViews.Slice(primaryViewCount, policyViewCount), BufferUsages.ShaderRead, "Cluster active policy views");
            if (_renderContext.IsValid)
                _renderContext.DispatchSourceBinder(ref graph, policyViews);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(MaterialInputsTrace))
                _shading.UpdateMaterialInputs(ref graph);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(FrameResourcesTrace))
                _installer.BindFrameResources(ref graph, in binding, history);
            _resources.InstanceResources.Upload(ref graph);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(SceneBoundsTrace))
                BindSceneBounds(ref graph, context.World, batch, policyViewCount != 0, views[0].WorldOrigin);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(ResidencyUploadsTrace))
                _installer.RecordResidencyUploads(ref graph, in binding, writeGeometry);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(ResidencyReadsTrace))
                _installer.DeclareResidencyReads(ref graph, in binding);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(FrameUploadsTrace))
                _installer.WriteFrameUploads(ref graph, _installer._installedRasterStages ?? throw new InvalidOperationException("Raster stages were not installed."), _installer._installedShadeStages ?? throw new InvalidOperationException("Shade stages were not installed."), packedViews[..primaryViewCount], primaryViewCount);
            if (_installer.GraphData.LayerTileSize != 0u)
                RecordLayerViews(ref graph, packedViews[(primaryViewCount + policyViewCount)..], binding.DispatchExtent);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(RasterInputsTrace))
                UpdateRasterDispatches(ref graph);
            ulong hierarchyNodes = 0;
            foreach (BufferRange range in binding.BvhRanges)
                hierarchyNodes += range.Size / ClusterBvh.NodeBytes;
            ulong work = Math.Min(_options.MaxCandidates, Math.Max(hierarchyNodes, checked((ulong)binding.DispatchExtent)));
            uint groupBudget = Math.Max(1u, Math.Max(_device.Capabilities.ShaderLaneCount, _device.Capabilities.Limits.MaximumComputeThreadsPerGroup) / 64u);
            uint workerGroups = Math.Max(1u, Math.Min(groupBudget, checked((uint)((work + 15u) / 16u))));
            foreach (GraphPassId phase in _installer.GraphData.Resources.Work.Phases)
                if (phase != default)
                    graph.SetPassData(phase, new ClusterDispatchParameters(new DispatchArguments(workerGroups, 1, 1)));
            _shading.UpdateShadeInputs(ref graph, _installer._installedShadeStages!);
            uint viewGroups = RenderRasterContext.GetViewWorkGroupCount(checked((uint)primaryViewCount));
            graph.SetPassData(_installer.GraphData.PrimarySeed.Pass, new ClusterDispatchParameters(new DispatchArguments(checked((checked((uint)binding.DispatchExtent) + 63u) / 64u), Math.Min(viewGroups, 65535u), Math.Max(1u, (viewGroups + 65534u) / 65535u))));
            foreach (ClusterLayerDispatch dispatch in _installer.GraphData.LayerDispatches)
            {
                graph.SetPassData(dispatch.Pass, new ClusterDispatchParameters(new DispatchArguments(dispatch.GroupX, dispatch.GroupY, checked((uint)primaryViewCount))));
            }

            foreach (ClusterLayerDispatch dispatch in _installer.GraphData.WinnerDispatches)
            {
                graph.SetPassData(dispatch.Pass, new ClusterFullscreenParameters(_outputDescription.Width, _outputDescription.Height, checked((uint)primaryViewCount)));
            }

            bool initializeHistory = history.RequiresInitialization;
            graph.SetRegionEnabled(_installer.GraphData.HistoryInitialization, initializeHistory);
        }
    }

    internal void BindNeutralDispatchCount(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.Resources.NeutralPolicyValue, range: new BufferRange(0u, 3u * sizeof(uint)), structureStride: sizeof(uint), label: "Cluster neutral raster-invocation count"), PipelineSync.ComputeShading);
    }

    internal void BindRasterTargetResource(ref RenderGraphEdit graph, ref PassDefinition pass,
        string name, ResourceBindingType type, bool target)
    {
        if (!target && _shading.BindNeutralRasterTargetResource(ref graph, ref pass, new(name, type, 0))) return;
        if (_shading._stageResourceProvider is { } provider && provider.TryResolve(name, type, out GraphResourceBinder binder))
        {
            binder(ref graph, ref pass, PipelineSync.ComputeShading);
            return;
        }
        throw new InvalidDataException($"Cluster raster target resource '{name}' has no installed binding.");
    }

    internal void BindPolicyClusterViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync, bool policyTarget = true)
    {
        if (policyTarget)
        {
            _renderContext.DispatchViewsBinder(ref graph, ref pass, sync);
            return;
        }

        GraphBufferId views = _installer.GraphData.Resources.PackedViews;
        _ = pass.Bind(graph.CreateBufferSrv(views, structureStride: ClusterViewUniforms.SizeInBytes, label: "Cluster active policy view metadata"), sync);
    }

    internal FrameData GetFrameData(ref RenderGraphFrame graph) => graph.GetFrameData(this, () => new FrameData(this));

    internal sealed class FrameData : IRenderGraphFrameExecutionObserver
    {
        private readonly ClusterGeometryPipeline _pipeline;
        internal readonly ClusterGraphInstaller.ClusterGraphData Source;
        internal readonly RenderInstanceBatch Batch;
        internal readonly ulong InstanceRevision, GeometryRevision, RasterRevision, DeformRevision, MetricFrameIndex;
        internal readonly List<(GraphBufferId Buffer, ClusterGraphInstaller.BufferWrite Write)> Writes = [];
        internal ClusterSceneUniforms SceneUniforms;
        internal WorldOrigin Origin;
        internal GraphTextureId CurrentHiZ;

        internal FrameData(ClusterGeometryPipeline pipeline)
        {
            _pipeline = pipeline;
            Source = pipeline._installer.GraphData;
            Batch = pipeline.CurrentInstances!;
            InstanceRevision = Batch.ContentRevision;
            GeometryRevision = pipeline.GeometryRevision;
            RasterRevision = pipeline.RasterRevision;
            DeformRevision = pipeline._deformBounds?.Revision ?? 0;
            MetricFrameIndex = pipeline._options.EnableFrameMetricsReadback ? pipeline._metricFrameIndex + 1 : pipeline._metricFrameIndex;
        }

        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            Source.Resources.Work.Initialized = true;
            Source.BufferWrites = Source.BufferWrites.Clear();
            Source.Outputs = Source.Outputs with { CurrentHiZ = CurrentHiZ };
            _pipeline._sceneBatch = Batch;
            _pipeline._sceneInstanceRevision = InstanceRevision;
            _pipeline._sceneGeometryRevision = GeometryRevision;
            _pipeline._sceneDeformRevision = DeformRevision;
            _pipeline._sceneRasterRevision = RasterRevision;
            _pipeline._sceneOrigin = Origin;
            _pipeline._metricFrameIndex = MetricFrameIndex;
        }
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;
        _shading._materialBindingVersions.Clear();
        List<Exception>? failures = null;
        Release(ref _viewQuery, context.World, ref failures);
        Release(ref _sceneQuery, context.World, ref failures);
        Dispose(ref _shading._shadeStages, ref failures);
        Dispose(ref _shading._rasterStages, ref failures);
        _shading.DisposeMaterialSamplers(ref failures);
        Dispose(ref _pipelines, ref failures);
        _history = null;
        Dispose(ref _dispatchIndirectLayout, ref failures);
        Dispose(ref _drawIndirectLayout, ref failures);
        Dispose(ref _drawIndexedIndirectLayout, ref failures);
        _metricFrameIndex = 0;
        _latestFrameMetrics = null;
        _shading.AcceptMaterialTextures(_installer._graphData.MaterialTextures.Clear(), shutdown: true);
        _shading._missingTextureInputsToPrepare = false;
        _shading._materialScalars = ClusterMaterialScalars.Empty;
        _installer._graphData = new ClusterGraphInstaller.ClusterGraphData();
        _renderContext = default;
        _installer._installedRasterStages = null;
        _installer._installedShadeStages = null;
        _installer._installedMaterialTopologyVersion = 0;
        _installer._graphInstalled = false;
        _created = false;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    internal void EnsurePipelineResources()
    {
        if (_pipelines is not null)
            return;
        ClusterPipelineSet? pipelines = null;
        IndirectCommandLayout? dispatchIndirectLayout = null;
        IndirectCommandLayout? drawIndirectLayout = null;
        IndirectCommandLayout? drawIndexedIndirectLayout = null;
        IndirectArgumentDesc[] dispatchArguments = [new(IndirectArgumentType.Dispatch)];
        IndirectArgumentDesc[] drawArguments = [new(IndirectArgumentType.Draw)];
        try
        {
            pipelines = new ClusterPipelineSet(_backend, _device, _shading._renderDevice.Pipelines, _configuration, _visibilityMode);
            dispatchIndirectLayout = _backend.CreateIndirectCommandLayout(_device, new IndirectCommandLayoutDesc(dispatchArguments, ClusterIndirectLayout.DispatchStride, label: "Cluster dispatch indirect layout"));
            drawIndirectLayout = _backend.CreateIndirectCommandLayout(_device, new IndirectCommandLayoutDesc(drawArguments, ClusterIndirectLayout.DrawStride, label: "Cluster draw indirect layout"));
            drawIndexedIndirectLayout = _backend.CreateIndirectCommandLayout(_device, new IndirectCommandLayoutDesc([new(IndirectArgumentType.DrawIndexed)], ClusterIndirectLayout.DrawIndexedStride, label: "Cluster indexed draw indirect layout"));
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
                throw new AggregateException("Cluster renderer-epoch construction failed and cleanup also reported failures.", cleanupFailures);
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
            throw new InvalidOperationException("Cluster frame metrics are not enabled for this renderer epoch.");
        }

        _installer._graph.CollectCompleted();
        return _latestFrameMetrics ?? throw new InvalidOperationException("No completed Cluster frame metrics are available.");
    }

    internal void ReceiveFrameMetrics(ReadOnlySpan<byte> bytes, ulong epoch)
    {
        if (epoch != _resources.Residency.EpochId.Value)
            return;
        uint phaseOneFinal = ReadUInt32(bytes, ClusterGraphInstaller.PhaseOneTessellationCountersReadbackOffset + 2 * sizeof(uint));
        uint phaseOneOverflow = ReadUInt32(bytes, ClusterGraphInstaller.PhaseOneTessellationCountersReadbackOffset + 3 * sizeof(uint));
        uint phaseOneCulled = ReadUInt32(bytes, ClusterGraphInstaller.PhaseOneTessellationCountersReadbackOffset + 4 * sizeof(uint));
        uint phaseOneQualityLimited = ReadUInt32(bytes, ClusterGraphInstaller.PhaseOneTessellationCountersReadbackOffset + 5 * sizeof(uint));
        uint phaseTwoFinal = ReadUInt32(bytes, ClusterGraphInstaller.PhaseTwoTessellationCountersReadbackOffset + 2 * sizeof(uint));
        uint phaseTwoOverflow = ReadUInt32(bytes, ClusterGraphInstaller.PhaseTwoTessellationCountersReadbackOffset + 3 * sizeof(uint));
        uint phaseTwoCulled = ReadUInt32(bytes, ClusterGraphInstaller.PhaseTwoTessellationCountersReadbackOffset + 4 * sizeof(uint));
        uint phaseTwoQualityLimited = ReadUInt32(bytes, ClusterGraphInstaller.PhaseTwoTessellationCountersReadbackOffset + 5 * sizeof(uint));
        var metrics = new ClusterFrameMetrics(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(ClusterGraphInstaller.MetricsFrameIndexOffset, sizeof(ulong))), ReadUInt32(bytes, ClusterGraphInstaller.CandidateCountReadbackOffset), ReadUInt32(bytes, ClusterGraphInstaller.CandidateArgsReadbackOffset), ReadUInt32(bytes, ClusterGraphInstaller.DrawArgsReadbackOffset + sizeof(uint)), ReadUInt32(bytes, ClusterGraphInstaller.DrawArgsReadbackOffset + 2 * sizeof(uint)), ReadUInt32(bytes, ClusterGraphInstaller.Phase2CandidateCountReadbackOffset), ReadUInt32(bytes, ClusterGraphInstaller.Phase2CandidateArgsReadbackOffset), ReadUInt32(bytes, ClusterGraphInstaller.Phase2DrawArgsReadbackOffset + sizeof(uint)), ReadUInt32(bytes, ClusterGraphInstaller.Phase2DrawArgsReadbackOffset + 2 * sizeof(uint)), ReadUInt32(bytes, ClusterGraphInstaller.RasterReserveReadbackOffset), ReadUInt32(bytes, ClusterGraphInstaller.RasterReserveReadbackOffset + sizeof(uint)), ReadUInt32(bytes, ClusterGraphInstaller.ShadeBinCountReadbackOffset), ReadUInt32(bytes, ClusterGraphInstaller.ShadeReserveReadbackOffset), SaturatingAdd(Math.Min(phaseOneFinal, _options.MaxCandidates), Math.Min(phaseTwoFinal, _options.MaxCandidates)), SaturatingAdd(phaseOneOverflow, phaseTwoOverflow), SaturatingAdd(phaseOneCulled, phaseTwoCulled), SaturatingAdd(phaseOneQualityLimited, phaseTwoQualityLimited), ReadUInt32(bytes, ClusterGraphInstaller.SoftwareDebugReadbackOffset), CountNonZeroVisibilityProbe(bytes))
        {
            ResidencyEpoch = epoch,
            Work = new ClusterWorkMetrics((RenderGeometryWorkFlags)ReadUInt32(bytes, ClusterGraphInstaller.WorkMetricsReadbackOffset), ReadWorkMetric(bytes, 8), ReadWorkMetric(bytes, 16), ReadWorkMetric(bytes, 24), ReadWorkMetric(bytes, 32), ReadWorkMetric(bytes, 40), ReadWorkMetric(bytes, 48), ReadWorkMetric(bytes, 56), ReadWorkMetric(bytes, 64), ReadWorkMetric(bytes, 72), ReadWorkMetric(bytes, 80), ReadWorkMetric(bytes, 88))
        };
        if (_latestFrameMetrics is not { } latest || metrics.FrameIndex > latest.FrameIndex)
            _latestFrameMetrics = metrics;
    }

    internal static ulong ReadWorkMetric(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(ClusterGraphInstaller.WorkMetricsReadbackOffset + offset, sizeof(ulong)));
    internal static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));
    internal static uint SaturatingAdd(uint left, uint right) => left > uint.MaxValue - right ? uint.MaxValue : left + right;
    internal uint CountNonZeroVisibilityProbe(ReadOnlySpan<byte> bytes)
    {
        uint result = 0;
        for (int index = 0; index < Math.Min(ClusterGraphInstaller.VisibilityProbePixelCount, _outputDescription.Width); index++)
            if (ReadUInt32(bytes, ClusterGraphInstaller.VisibilityProbeReadbackOffset + index * sizeof(uint)) != 0)
                result++;
        return result;
    }

    internal IndirectCommandLayout RequireDispatchIndirectLayout() => _dispatchIndirectLayout ?? throw new InvalidOperationException("The Cluster dispatch indirect layout is unavailable.");
    internal IndirectCommandLayout RequireDrawIndirectLayout() => _drawIndirectLayout ?? throw new InvalidOperationException("The Cluster draw indirect layout is unavailable.");
    internal static void ReleaseIfValid(RenderWorld world, QueryHandle query)
    {
        if (query.IsValid)
            world.ReleaseQuery(query);
    }

    internal static void Release(ref QueryHandle query, RenderWorld world, ref List<Exception>? failures)
    {
        if (!query.IsValid)
            return;
        try
        {
            world.ReleaseQuery(query);
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }

        query = default;
    }

    internal static void Dispose<T>(ref T? value, ref List<Exception>? failures)
        where T : class, IDisposable
    {
        if (value is null)
            return;
        try
        {
            value.Dispose();
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }

        value = null;
    }

    internal sealed class ViewCollector
    {
        internal readonly List<(RenderView View, Entity Entity, RenderViewTarget Target, RenderPreviousView? Previous)> Values = [];
        internal void Collect(QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                RenderViewTarget target = row.TryRead<RenderViewTarget>(out var value) ? value : default;
                RenderPreviousView? previous = row.TryRead<RenderPreviousView>(out var history) ? history : null;
                Values.Add((row.Read<RenderView>(), row.Entity, target, previous));
            }
        }
    }

    internal sealed class CullWork
    {
        internal GraphBufferId Nodes, Clusters, NodeReady, ClusterReady, Arguments;
        internal GraphRegionId Initialization;
        internal GraphPassId Initialize;
        internal GraphPassId[] Phases = new GraphPassId[RasterPhaseCount];
        internal bool Initialized;
    }

    internal CullWork CreateCullWork(ref RenderGraphEdit graph, ulong capacity)
    {
        var work = new CullWork();
        work.Nodes = Create(ref graph, "Cluster node queue", capacity * ClusterGraphInstaller.TraversalNodeStride);
        work.Clusters = Create(ref graph, "Cluster cluster queue", capacity * ClusterGraphInstaller.CandidateStride);
        work.NodeReady = Create(ref graph, "Cluster node readiness", capacity * sizeof(uint));
        work.ClusterReady = Create(ref graph, "Cluster batch readiness", ((capacity + 63u) / 64u) * sizeof(uint));
        work.Arguments = Create(ref graph, "Cluster work queue counts", ClusterGraphInstaller.TraversalArgumentStride, BufferUsages.Indirect);
        work.Initialization = graph.AddRegion("Initialize Cluster work queues", _regions.PrimaryGeometry);
        work.Initialize = graph.AddCopyFramePass("Initialize Cluster work queue data", PassQueueSelection.AnyOfType(QueueType.Graphics), new[] { work.Nodes, work.Clusters, work.NodeReady, work.ClusterReady }, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), static (ref PassDefinition pass, ref GraphBufferId[] buffers) =>
        {
            foreach (GraphBufferId buffer in buffers)
                pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, static (ref CopyPassCommandScope commands, in GraphBufferId[] buffers) =>
        {
            foreach (GraphBufferId id in buffers)
            {
                var buffer = commands.GetBuffer(id);
                commands.ClearBuffer(buffer, BufferRange.Whole);
            }
        }, region: work.Initialization);
        return work;
        static GraphBufferId Create(ref RenderGraphEdit graph, string label, ulong bytes, BufferUsages extra = BufferUsages.None) => graph.CreatePersistentBuffer(new BufferDesc(Math.Max(bytes, 4), BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | extra, label));
    }

    internal GraphPassId RecordPersistentCull(ref RenderGraphEdit graph, in ClusterGraphInstaller.FrameResources frame, GraphBufferId views, uint mode, GraphBufferId drawArguments, GraphBufferId base0, GraphBufferId base1, GraphBufferId base2, bool target, int phase, string label, GraphRegionId region, bool layerTiles = false)
    {
        uint maximumViews = target ? _renderContext.MaximumDispatchCount : frame.ViewCount;
        ClusterComputePipeline traversal = _pipelines!.GetTraversal(target, maximumViews > 1u, layerTiles);
        GraphBufferSlice parameters = _installer.UploadUniform(ref graph, new ClusterTraversalLevelUniforms { MaxNodes = _options.MaxCandidates, TraversalMode = mode }, $"Cluster {label} work parameters");
        var dispatch = new ClusterDispatchParameters(new DispatchArguments(1, 1, 1));
        GraphPassId id = graph.AddComputeFramePass($"Cull Cluster {label} work", PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch, new PassOptions(RepeatCommands: true), traversal.Pipeline, traversal.ParameterLayout, default, static (ref PassDefinition _, ref ClusterDispatchParameters _) =>
        {
        }, ClusterDispatchParameters.Record, region: region);
        PassDefinition pass = graph.GetPass(id);
        foreach (ref readonly ShaderParameterResource input in traversal.ParameterResources.Resources)
        {
            if (ClusterShadingPipeline.BindInactiveStageResource(ref pass, input)) continue;
            if (!layerTiles && (input.Name is "LayerCullMaterials" or "LayerCullParameters" or "LayerClusterTileWindows"))
            {
                pass.Bind(GraphParameterResourceBinding.Null(input.Type));
                continue;
            }
            if (input.Name.StartsWith("RasterTarget", StringComparison.Ordinal))
            {
                BindRasterTargetResource(ref graph, ref pass, input.Name, input.Type, target);
                continue;
            }

            switch (input.Name)
            {
                case "ClusterCurrentBounds":
                case "ClusterPreviousBounds":
                    BindDeformBounds(ref graph, ref pass, input.Name == "ClusterPreviousBounds");
                    break;
                case "Views":
                    Read(ref graph, ref pass, views, ClusterViewUniforms.SizeInBytes);
                    break;
                case "GlobalBVH":
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(ClusterGraphInstaller.CreateReadOnlyBufferView(graph, frame.Bvh)));
                    break;
                case "PageHeap":
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(ClusterGraphInstaller.CreateReadOnlyBufferView(graph, frame.PageHeap)));
                    break;
                case "InstanceData":
                    Read(ref graph, ref pass, frame.InstanceData);
                    break;
                case "InstanceProperties":
                    pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading);
                    break;
                case "MaterialBounds":
                    Read(ref graph, ref pass, frame.MaterialBounds, sizeof(uint));
                    break;
                case "ClusterRasterStages":
                    Read(ref graph, ref pass, frame.RasterBinIndices, sizeof(uint));
                    break;
                case "LayerCullMaterials":
                    pass.Bind(graph.CreateBufferSrv(_installer.GraphData.LayerRasterMaterials!, structureStride: 8), PipelineSync.ComputeShading);
                    break;
                case "LayerCullParameters":
                    GraphBufferSlice layerParameters = _installer.UploadUniform(ref graph,
                        new LayerCullParameters(_installer.GraphData.LayerTileSize, _installer.GraphData.LayerHardwareWindowTiles), "Layer culling tile dimensions");
                    pass.Bind(graph.CreateBufferCbv(layerParameters.Buffer, layerParameters.Range), PipelineSync.ComputeShading);
                    break;
                case "LayerClusterTileWindows":
                    Write(ref graph, ref pass, frame.LayerTileWindows, 16, GraphAccessMode.Write);
                    break;
                case "CandidateArgs":
                    Write(ref graph, ref pass, frame.CandidateArgs);
                    break;
                case "CandidateClusters":
                    Write(ref graph, ref pass, frame.CandidateClusters, ClusterGraphInstaller.CandidateStride);
                    break;
                case "CandidateCount":
                    Write(ref graph, ref pass, frame.CandidateCount, sizeof(uint));
                    break;
                case "PageFaultBuffer":
                    Write(ref graph, ref pass, frame.PageFaults);
                    break;
                case "TraversalLevel":
                    pass.Bind(graph.CreateBufferCbv(parameters.Buffer, parameters.Range), PipelineSync.ComputeShading);
                    break;
                case "TraversalNodes":
                    Write(ref graph, ref pass, frame.Work.Nodes, ClusterGraphInstaller.TraversalNodeStride);
                    break;
                case "NodeIndirectArgs":
                    Write(ref graph, ref pass, frame.Work.Arguments);
                    break;
                case "TraversalReady":
                    Write(ref graph, ref pass, frame.Work.NodeReady, sizeof(uint));
                    break;
                case "ClusterBatchReady":
                    Write(ref graph, ref pass, frame.Work.ClusterReady, sizeof(uint));
                    break;
                case "WorkStatus":
                    BindWorkStatus(ref graph, ref pass);
                    break;
                case "HierarchyHiZ":
                    pass.Bind(graph.CreateTextureSrv(mode == 2u ? frame.CurrentHiZ : frame.PreviousHiZ, format: Format.R32Float, dimension: TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading);
                    break;
                case "DeferredWork":
                    Write(ref graph, ref pass, frame.Phase2CandidateClusters, ClusterGraphInstaller.CandidateStride, GraphAccessMode.Write);
                    break;
                case "DeferredWorkCount":
                    Write(ref graph, ref pass, frame.Phase2CandidateCount, sizeof(uint));
                    break;
                case "DeferredWorkDispatchArgs":
                    Write(ref graph, ref pass, frame.Phase2CandidateArgs);
                    break;
                case "PolicyClusterViews":
                    BindPolicyClusterViews(ref graph, ref pass, PipelineSync.ComputeShading, target);
                    break;
                case "DrawArgs":
                    Write(ref graph, ref pass, drawArguments);
                    break;
                case "BaseDrawArgs0":
                    Read(ref graph, ref pass, base0);
                    break;
                case "BaseDrawArgs1":
                    Read(ref graph, ref pass, base1);
                    break;
                case "BaseDrawArgs2":
                    Read(ref graph, ref pass, base2);
                    break;
                case "VisibleClusters":
                    Write(ref graph, ref pass, frame.VisibleClusters, ClusterGraphInstaller.VisibleClusterStride, phase % 2 == 0 ? GraphAccessMode.Write : GraphAccessMode.ReadWrite);
                    break;
                default:
                    throw new InvalidDataException($"Cluster work input '{input.Name}' has no resource.");
            }
        }

        frame.Work.Phases[phase] = id;
        return id;
        static void Read(ref RenderGraphEdit graph, ref PassDefinition pass, GraphBufferId buffer, uint stride = 0) => pass.Bind(graph.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading);
        static void Write(ref RenderGraphEdit graph, ref PassDefinition pass, GraphBufferId buffer, uint stride = 0, GraphAccessMode access = GraphAccessMode.ReadWrite) => pass.Bind(graph.CreateBufferUav(buffer, structureStride: stride), PipelineSync.ComputeShading, access, WriteCoverage.Partial, ResourceContentState.Defined, stride is ClusterGraphInstaller.CandidateStride or ClusterGraphInstaller.TraversalNodeStride ? BufferContentScope.CountBoundedPrefix : BufferContentScope.FullRange);
    }

    public const string DeformationTimeGroup = "Cluster deformation";
    public const string DeformationEvaluationTimeGroup = "Cluster deformation evaluation";
    internal readonly record struct DeformDrawData(GraphBufferId Count, GraphBufferId Previous0, GraphBufferId Previous1, GraphBufferId Previous2);
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    internal readonly record struct DeformUniforms(uint Capacity, uint MaxVisibleClusters, uint CurrentBin, uint Padding = 0);
    internal void RecordDeformReset(ref RenderGraphEdit graph, in ClusterGraphInstaller.FrameResources frame)
    {
        GraphPassId reset = graph.AddCopyFramePass("Reset Cluster deformation capacity", PassQueueSelection.AnyOfType(QueueType.Graphics), new ClusterTessellationResetParameters(frame.DeformAllocation), new PassOptions(RepeatCommands: true), static (ref PassDefinition pass, ref ClusterTessellationResetParameters data) => pass.Write(data.Counters, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete), ClusterTessellationResetParameters.Record, region: _regions.PrimaryGeometry);
        graph.SetPassTimeGroup(reset, DeformationTimeGroup);
    }

    internal void RecordDeformation(ref RenderGraphEdit graph, ClusterRasterDispatch material, DeformDrawData draw, GraphRegionId region)
    {
        ClusterDeformation deformation = material.Deformation!;
        GraphBufferSlice uniforms = _installer.UploadUniform(ref graph, new DeformUniforms(_options.MaxDeformCacheBytes, _options.MaxCandidates, material.BinIndex), "Cluster user deformation parameters");
        _rasterInputs?.Uniforms.Add(uniforms);
        for (int index = 0; index < deformation.Stages.Length; index++)
        {
            ClusterDeformStage stage = deformation.Stages[index];
            string name = $"{ClusterDeformation.Entries[index]} bin {material.BinIndex}";
            GraphPassId passId;
            if (index is 0 or 3)
                passId = _installer.AddComputePass(ref graph, name, stage.Pipeline, new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)), 0, ClusterDispatchParameters.Record, region: region);
            else
            {
                var dispatch = new ClusterIndirectDispatchParameters(RequireDispatchIndirectLayout(), _installer.GraphData.Resources.DeformArguments, index == 5 ? 12u : 0u);
                passId = _installer.AddComputePass(ref graph, name, stage.Pipeline, dispatch, 0, static (ref PassDefinition pass, ref ClusterIndirectDispatchParameters data) => pass.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
            }

            PassDefinition pass = graph.GetPass(passId);
            BindDeformResources(ref graph, ref pass, stage, material.Material, uniforms, draw, index);
            graph.SetPassTimeGroup(passId, index == 5 ? DeformationEvaluationTimeGroup : DeformationTimeGroup);
        }
    }

    internal void BindDeformResources(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterDeformStage stage, Material material, GraphBufferSlice uniforms, DeformDrawData draw, int operation)
    {
        ReadOnlySpan<ShaderParameterResource> resources = stage.Resources.Resources.Resources;
        ClusterGraphInstaller.FrameResources frame = _installer.GraphData.Resources;
        for (int index = 0; index < resources.Length; index++)
        {
            ref readonly ShaderParameterResource resource = ref resources[index];
            if (ClusterShadingPipeline.BindInactiveStageResource(ref pass, resource))
                continue;
            GraphBufferId buffer = default;
            uint stride = 0;
            GraphAccessMode mode = GraphAccessMode.Read;
            switch (resource.Name)
            {
                case "DeformUniforms":
                    pass.Bind(uniforms, PipelineSync.ComputeShading);
                    continue;
                case "InstanceProperties":
                    pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading);
                    continue;
                case "MaterialScalarRegion":
                    pass.Bind(graph.CreateBufferSrv(_installer.GraphData.ShadeScalars), PipelineSync.ComputeShading);
                    continue;
                case "MaterialScalarOffsets":
                    pass.Bind(graph.CreateBufferSrv(_installer.GraphData.ShadeScalarOffsets, structureStride: 4), PipelineSync.ComputeShading);
                    continue;
                case "PageHeap":
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(graph.CreateBufferSrv(frame.PageHeap)));
                    continue;
                case "InstanceData":
                    buffer = frame.InstanceData;
                    break;
                case "DeformDrawArgs":
                    buffer = draw.Count;
                    break;
                case "DeformReadOffset0":
                    buffer = draw.Previous0;
                    break;
                case "DeformReadOffset1":
                    buffer = draw.Previous1;
                    break;
                case "DeformReadOffset2":
                    buffer = draw.Previous2;
                    break;
                case "DeformRasterBins":
                    buffer = frame.RasterBinIndices;
                    stride = 4;
                    break;
                case "DeformVisibleClusters":
                    buffer = frame.VisibleClusters;
                    stride = ClusterGraphInstaller.VisibleClusterStride;
                    break;
                case "DeformCacheWrite":
                    buffer = frame.DeformCache;
                    mode = GraphAccessMode.Write;
                    break;
                case "DeformRegionsWrite":
                    buffer = frame.DeformRegions;
                    stride = 8;
                    mode = operation == 1 ? GraphAccessMode.Write : operation == 4 ? GraphAccessMode.ReadWrite : GraphAccessMode.Read;
                    break;
                case "DeformRequests":
                    buffer = frame.DeformRequests;
                    stride = 8;
                    mode = operation == 1 ? GraphAccessMode.Write : GraphAccessMode.Read;
                    break;
                case "DeformPrefixOffsets":
                    buffer = frame.DeformPrefixOffsets;
                    stride = 4;
                    mode = operation == 2 ? GraphAccessMode.Write : GraphAccessMode.Read;
                    break;
                case "DeformPrefixGroups":
                    buffer = frame.DeformPrefixGroups;
                    stride = 8;
                    mode = operation == 2 ? GraphAccessMode.Write : operation == 3 ? GraphAccessMode.ReadWrite : GraphAccessMode.Read;
                    break;
                case "DeformAllocation":
                    buffer = frame.DeformAllocation;
                    stride = 4;
                    mode = GraphAccessMode.ReadWrite;
                    break;
                case "DeformArguments":
                    buffer = frame.DeformArguments;
                    mode = GraphAccessMode.Write;
                    break;
                default:
                    if (stage.Resources.FeatureBinders[index] is { } feature)
                        feature(ref graph, ref pass, PipelineSync.ComputeShading);
                    else
                        _shading.BindStageMaterialResource(ref graph, ref pass, resource, stage.Resources.MaterialBindings[index], material, PipelineSync.ComputeShading);
                    continue;
            }

            if (resource.Type == ResourceBindingType.BufferUav)
                pass.Bind(GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(buffer, structureStride: stride), PipelineSync.ComputeShading, mode, resource.Name == "DeformArguments" ? WriteCoverage.Complete : WriteCoverage.Partial, ResourceContentState.Defined, resource.Name == "DeformArguments" ? BufferContentScope.FullRange : BufferContentScope.CountBoundedRanges));
            else
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges));
        }
    }

    internal bool BindDeformRead(ref RenderGraphEdit graph, ref PassDefinition pass, in ShaderParameterResource resource, PipelineSync sync)
    {
        if (!ClusterDeformResourceNames.IsRead(resource.Name))
            return false;
        bool regions = resource.Name == "VertexDeformRegions";
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(regions ? _installer.GraphData.Resources.DeformRegions : _installer.GraphData.Resources.DeformCache, structureStride: regions ? 8u : 0u), sync, BufferContentScope.CountBoundedRanges));
        return true;
    }

    internal sealed class PageFaultReceiver(ClusterGeometryPipeline pipeline, int capacity, ClusterEpochId epoch) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            int workOffset = checked(sizeof(uint) + capacity * 16);
            var work = (RenderGeometryWorkFlags)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(workOffset, sizeof(uint)));
            RenderGeometryWorkFlags rejected = work & ~RenderGeometryWorkFlags.RasterBatches;
            if (rejected != RenderGeometryWorkFlags.None)
                throw new ClusterWorkOverflowException(rejected, pipeline._options.MaxCandidates);
            ClusterStreamingFeedback.WriteReadback(pipeline._streamingFeedback, epoch.Value, tag, bytes[..workOffset], bytes[(workOffset + sizeof(uint))..]);
        }
    }

    internal sealed class FrameMetricsReceiver(ClusterGeometryPipeline pipeline) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => pipeline.ReceiveFrameMetrics(bytes, tag);
    }

    internal bool BeginGeometryFrame(ref RenderGraphFrame frame, ClusterResidentGeometry? changes)
    {
        GeometryFrame data = frame.GetFrameData(_resources, () => new GeometryFrame(this, changes));
        frame.ObserveExecution(data);
        bool first = data.Readbacks.Count == 0;
        if (!first && changes is { Meshes.LeafFlagFixups.Length: > 0 })
        {
            GraphPassId fixups = data.First._installer.GraphData.LeafFlagPass;
            frame.OrderAfter(_installer.GraphData.SceneBounds.Pass, fixups);
            foreach (GraphPassId traversal in _installer.GraphData.Resources.Work.Phases)
                if (traversal != default)
                    frame.OrderAfter(traversal, fixups);
        }

        data.Readbacks.Add(_installer.GraphData.PageFaultReadback);
        return first;
    }

    internal sealed class GeometryFrame(ClusterGeometryPipeline first, ClusterResidentGeometry? changes) : IRenderGraphFrameExecutionObserver
    {
        internal ClusterGeometryPipeline First { get; } = first;
        internal HashSet<GraphReadbackId> Readbacks { get; } = [];

        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            int count = 0;
            foreach (GraphReadbackId readback in Readbacks)
                if (submission.TryGetReadbackCompletion(readback, out _))
                    count++;
            ClusterStreamingFeedback.WriteFrame(First._streamingFeedback, First._resources.EpochId.Value, submission.FrameIdentity, count);
            if (changes is not null)
                First._resources.CommitResidencyChanges(changes, submission.FrameIdentity);
        }
    }

    public const string RasterTimeGroup = "Cluster raster";
    internal RasterInputs? _rasterInputs;
    internal sealed class RasterInputs
    {
        internal ImmutableArray<ClusterShadingPipeline.IndexedTextureRead> TextureReads = [];
        internal readonly List<GraphBufferId> Buffers = [];
        internal readonly List<GraphBufferSlice> Uniforms = [];
        internal GraphBufferSlice? SoftwareUniforms;
        internal RasterBinning? PatchBinning;
        internal MeshRaster? MeshDraw;
    }

    internal sealed record MeshRaster(GraphPassId Pass, uint Bin, GraphBufferAccessId Access, ClusterTessellationRasterPassData Data);
    internal readonly record struct TessellationSplitKey(ClusterComputePipeline Pipeline, ClusterCommandBindingKey Bindings);
    internal sealed record RasterBin(ClusterRasterDispatch Stage)
    {
        internal RasterInputs Inputs { get; } = new();
    }

    internal sealed class RasterBinComparer : IEqualityComparer<RasterBin>
    {
        internal static readonly RasterBinComparer Instance = new();
        public bool Equals(RasterBin? left, RasterBin? right) => ReferenceEquals(left, right) || left is not null && right is not null && SameRasterStage(left.Stage, right.Stage) && left.Inputs.TextureReads.AsSpan().SequenceEqual(right.Inputs.TextureReads.AsSpan());
        public int GetHashCode(RasterBin value) => value.Stage.BinIndex.GetHashCode();
    }

    internal sealed record TessellationSplitStage(ClusterRasterDispatch Stage, bool SharedBins)
    {
        internal RasterInputs Inputs { get; } = new();
    }

    internal sealed class TessellationSplitComparer : IEqualityComparer<TessellationSplitStage>
    {
        internal static readonly TessellationSplitComparer Instance = new();
        public bool Equals(TessellationSplitStage? left, TessellationSplitStage? right) => ReferenceEquals(left, right) || left is not null && right is not null && left.SharedBins == right.SharedBins && SameRasterStage(left.Stage, right.Stage) && left.Inputs.TextureReads.AsSpan().SequenceEqual(right.Inputs.TextureReads.AsSpan());
        public int GetHashCode(TessellationSplitStage value) => value.Stage.BinIndex.GetHashCode();
    }

    internal sealed record TessellationGroup(GraphRegionMap<uint, RasterBin> Seeds, GraphRegionMap<int, TessellationSplitStage> Split);
    internal sealed record RasterPhase(GraphRegionId Region, GraphRegionMap<uint, RasterBin> Bins, GraphRegionMap<TessellationSplitKey, bool> Groups, ImmutableDictionary<TessellationSplitKey, TessellationGroup> TessellationGroups, GraphRegionMap<int, RasterInputs> Patches, bool PhaseOne, bool PolicyTarget)
    {
        internal bool LayerTarget { get; init; }
        internal GraphRegionMap<uint, RasterBin> Deformation { get; init; } = null !;
        internal DeformDrawData DrawData { get; init; }
    }

    internal static bool SameRasterStage(ClusterRasterDispatch left, ClusterRasterDispatch right) => left.BinIndex == right.BinIndex && ReferenceEquals(left.Material, right.Material) && left.Key.Equals(right.Key) && left.CommandBindings.Equals(right.CommandBindings) && left.HasSameBindings(right);
    internal void ReleaseRasterInputs(ref RenderGraphEdit graph, RasterInputs inputs)
    {
        foreach (GraphBufferId buffer in inputs.Buffers)
            _installer.RemoveBufferData(ref graph, buffer);
        foreach (GraphBufferSlice uniform in inputs.Uniforms)
            _installer.RemoveParameters(uniform);
    }

    internal GraphRegionMap<uint, RasterBin> ReconcileRasterBins(ref RenderGraphEdit graph, GraphRegionMap<uint, RasterBin> previous, RasterBin[] bins, GraphRegionBuilder<uint, RasterBin> build)
    {
        var desired = bins.ToDictionary(static bin => bin.Stage.BinIndex, static bin => new RasterBin(bin.Stage) { Inputs = { TextureReads = bin.Inputs.TextureReads } });
        foreach (RasterBin bin in previous.Values)
            if (!desired.TryGetValue(bin.Stage.BinIndex, out RasterBin? next) || !RasterBinComparer.Instance.Equals(bin, next))
                ReleaseRasterInputs(ref graph, bin.Inputs);
        return previous.Reconcile(ref graph, desired, build);
    }

    internal ImmutableDictionary<int, RasterPhase> ReconcileRasterPhases(ref RenderGraphEdit graph, ClusterRasterStageSet stages, ClusterShadeStageSet shadeStages)
    {
        RasterBin[] bins = CreateRasterBins(stages, shadeStages);
        var phases = _installer.GraphData.RasterPhases;
        foreach ((int phase, RasterPhase data)in _installer.GraphData.RasterPhases)
            phases = phases.SetItem(phase, ReconcileRasterPhase(ref graph, data, bins));
        return phases;
    }

    internal RasterBin[] CreateRasterBins(ClusterRasterStageSet stages, ClusterShadeStageSet shadeStages)
    {
        var reads = _shading.CollectIndexedTextureReads(stages.BinIndices, shadeStages, 0x07ffffffu);
        return stages.Dispatches.Select(stage => new RasterBin(stage) { Inputs = { TextureReads = stage.ReadsMaterialScalars ? reads.GetValueOrDefault(stage.BinIndex, []) : [] } }).ToArray();
    }

    internal RasterPhase ReconcileRasterPhase(ref RenderGraphEdit graph, RasterPhase data, RasterBin[] bins)
    {
        if (data.LayerTarget) bins = bins.Where(static bin => bin.Stage.Material.Layer.HasValue && bin.Stage.Tessellation is not null).ToArray();
        else if (!data.PolicyTarget) bins = bins.Where(static bin => !bin.Stage.Material.Layer.HasValue).ToArray();
        var deformation = ReconcileRasterBins(ref graph, data.Deformation, bins.Where(static bin => bin.Stage.Deformation is not null).ToArray(), (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
        {
            _rasterInputs = value.Inputs;
            try
            {
                RecordDeformation(ref edit, value.Stage, data.DrawData, region);
            }
            finally
            {
                _rasterInputs = null;
            }
        });
        var splitStages = bins.Where(static bin => bin.Stage.Tessellation is not null).GroupBy(static bin => new TessellationSplitKey(bin.Stage.Tessellation!.Split, bin.Stage.Tessellation.SplitBindings)).ToDictionary(static group => group.Key, static group => group.ToArray());
        var tessellationGroups = data.TessellationGroups;
        foreach ((TessellationSplitKey key, TessellationGroup group)in data.TessellationGroups)
        {
            if (splitStages.ContainsKey(key))
                continue;
            foreach (RasterBin seed in group.Seeds.Values)
                ReleaseRasterInputs(ref graph, seed.Inputs);
            foreach (TessellationSplitStage split in group.Split.Values)
                ReleaseRasterInputs(ref graph, split.Inputs);
            tessellationGroups = tessellationGroups.Remove(key);
        }

        GraphRegionMap<TessellationSplitKey, bool> groups = data.Groups.Reconcile(ref graph, splitStages.Keys.Select(static key => new KeyValuePair<TessellationSplitKey, bool>(key, true)), (ref RenderGraphEdit edit, GraphRegionId region, TessellationSplitKey key, bool value) =>
        {
            GraphRegionId reset = edit.AddRegion("Reset", region);
            GraphRegionId seeds = edit.AddRegion("Seeds", region);
            GraphRegionId split = edit.AddRegion("Split", region);
            _installer.RecordTessellationLevelReset(ref edit, _installer.GraphData.Resources, splitStages[key][0].Stage.BinIndex, data.PhaseOne, reset);
            tessellationGroups = tessellationGroups.Add(key, new TessellationGroup(new GraphRegionMap<uint, RasterBin>(seeds, RasterBinComparer.Instance), new GraphRegionMap<int, TessellationSplitStage>(split, TessellationSplitComparer.Instance)));
        });
        foreach ((TessellationSplitKey key, RasterBin[] seeds)in splitStages)
        {
            TessellationGroup group = tessellationGroups[key];
            GraphRegionMap<uint, RasterBin> seedRegions = ReconcileRasterBins(ref graph, group.Seeds, seeds, (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
            {
                _rasterInputs = value.Inputs;
                try
                {
                    _installer.RecordTessellationSeed(ref edit, _installer.GraphData.Resources, value.Stage, data.PhaseOne, data.PolicyTarget, region, data.DrawData, layerTiles: data.LayerTarget);
                    _installer.RecordTessellationSeed(ref edit, _installer.GraphData.Resources, value.Stage, data.PhaseOne, data.PolicyTarget, region, data.DrawData, true, data.LayerTarget);
                }
                finally
                {
                    _rasterInputs = null;
                }
            });
            // Keep the representative while its bin survives; additional seeds reuse its bindings.
            TessellationSplitStage? before = group.Split.Values.FirstOrDefault();
            ClusterRasterDispatch representative = seeds.Select(static bin => bin.Stage).FirstOrDefault(stage => stage.BinIndex == before?.Stage.BinIndex) ?? seeds[0].Stage;
            var next = new TessellationSplitStage(representative, seeds.Length > 1)
            {
                Inputs =
                {
                    TextureReads = seeds.SelectMany(static bin => bin.Inputs.TextureReads).Distinct().OrderBy(static read => read.Material.MaterialId).ThenBy(static read => read.Property).ToImmutableArray()
                }
            };
            if (before is not null && !TessellationSplitComparer.Instance.Equals(before, next))
                ReleaseRasterInputs(ref graph, before.Inputs);
            GraphRegionMap<int, TessellationSplitStage> splitRegions = group.Split.Reconcile(ref graph, [new KeyValuePair<int, TessellationSplitStage>(0, next)], (ref RenderGraphEdit edit, GraphRegionId region, int key, TessellationSplitStage value) =>
            {
                _rasterInputs = value.Inputs;
                try
                {
                    _installer.RecordTessellationSplit(ref edit, _installer.GraphData.Resources, value.Stage, data.PhaseOne, data.PolicyTarget, value.SharedBins, region, data.LayerTarget);
                }
                finally
                {
                    _rasterInputs = null;
                }
            });
            tessellationGroups = tessellationGroups.SetItem(key, new TessellationGroup(seedRegions, splitRegions));
        }

        RasterInputs? patchInputs = data.Patches.Values.FirstOrDefault();
        if (splitStages.Count == 0 && patchInputs is not null)
            ReleaseRasterInputs(ref graph, patchInputs);
        GraphRegionMap<int, RasterInputs> patches = data.Patches.Reconcile(ref graph, splitStages.Count == 0 ? [] : [new KeyValuePair<int, RasterInputs>(0, patchInputs ?? new RasterInputs())], (ref RenderGraphEdit edit, GraphRegionId region, int key, RasterInputs value) =>
        {
            _rasterInputs = value;
            try
            {
                _installer.RecordTessellationBinning(ref edit, _installer.GraphData.Resources, _installer.GraphData.Resources.TessellationFinalPatches, _installer.GraphData.Resources.TessellationWorkPatches, _installer.GraphData.Resources.TessellationSourceDispatchArgs, 2u * sizeof(uint), data.PhaseOne ? "phase one final patches" : "phase two final patches", region);
            }
            finally
            {
                _rasterInputs = null;
            }
        });
        GraphRegionMap<uint, RasterBin> binRegions = ReconcileRasterBins(ref graph, data.Bins, bins, (ref RenderGraphEdit edit, GraphRegionId region, uint bin, RasterBin value) =>
        {
            _rasterInputs = value.Inputs;
            try
            {
                ClusterRasterDispatch[] single = [value.Stage];
                if (data.LayerTarget)
                {
                    _installer.RecordLayerTessellationHardware(ref edit, _installer.GraphData.Resources, value.Stage, region);
                    return;
                }
                if (_visibilityMode != ClusterVisibilityMode.HardwareOnly)
                    _installer.RecordTessellationDice(ref edit, _installer.GraphData.Resources, single, data.PhaseOne, data.PolicyTarget, region);
                _installer.RecordSoftwareRaster(ref edit, _installer.GraphData.Resources, single, _installer.GraphData.Resources.PackedViews, data.PhaseOne, data.PolicyTarget, region);
                _installer.RecordTessellationHardwareRaster(ref edit, _installer.GraphData.Resources, single, data.PhaseOne, data.PolicyTarget, region);
                _installer.RecordHardwareRaster(ref edit, _installer.GraphData.Resources, single, _installer.GraphData.Resources.PackedViews, data.PhaseOne, data.PolicyTarget, region);
                _installer.RecordSpilledRaster(ref edit, _installer.GraphData.Resources, value.Stage, data.DrawData, data.PhaseOne, data.PolicyTarget, region);
            }
            finally
            {
                _rasterInputs = null;
            }
        });
        return data with
        {
            Deformation = deformation,
            Groups = groups,
            TessellationGroups = tessellationGroups,
            Patches = patches,
            Bins = binRegions
        };
    }

    internal sealed record RasterBinning(GraphBufferSlice Uniforms, GraphPassId Reset, GraphPassId Reserve, bool PolicyTarget)
    {
        internal bool LayerTarget { get; init; }
        internal GraphPassId Prepare { get; init; }
        internal ScreenBinningScanResources BatchScan { get; init; }
        internal ScreenBinningScanResources SoftwareScan { get; init; }
        internal GraphRegionId ScanRegion { get; init; }
    }
    internal void UpdateRasterBinning(ref RenderGraphEdit graph, ImmutableDictionary<int, RasterPhase> phases)
    {
        Span<ClusterRasterBinningUniforms> uniforms = stackalloc ClusterRasterBinningUniforms[1];
        foreach (RasterBinning? binning in _installer.GraphData.RasterBinning)
            if (binning is not null)
            {
                uniforms[0] = new ClusterRasterBinningUniforms
                {
                    RasterMaxBins = _installer.GraphData.Resources.RasterBinCount,
                    MaterialAddressCapacity = _installer.GraphData.Resources.MaterialAddressCapacity,
                    MaxVisibleClusters = _options.MaxCandidates,
                    CollectMetrics = _options.EnableFrameMetricsReadback ? 1u : 0u,
                    PolicyTarget = binning.LayerTarget ? 2u : binning.PolicyTarget ? 1u : 0u,
                    ScreenWidth = checked((uint)_installer.GraphData.Resources.Width),
                    ScreenHeight = checked((uint)_installer.GraphData.Resources.Height),
                    ViewCount = _installer.GraphData.Resources.ViewCount,
                    BatchCapacity = _installer.GraphData.Resources.RasterBatchCapacity,
                    DepthBucketsEnabled = _installer.GraphData.Resources.DepthBucketsEnabled ? 1u : 0u
                };
                _installer.UpdateParameters(ref graph, binning.Uniforms, MemoryMarshal.AsBytes(uniforms));
            }

        Span<ClusterTessellationBinUniforms> patches = stackalloc ClusterTessellationBinUniforms[1];
        patches[0] = new ClusterTessellationBinUniforms
        {
            BinCount = checked(_installer.GraphData.Resources.RasterBinCount * 8u),
            MaxPatchItems = _options.MaxCandidates,
            SourceCounterOffset = 2u * sizeof(uint)
        };
        foreach (RasterPhase phase in phases.Values)
            if (phase.Patches.Values.FirstOrDefault()?.PatchBinning is { } binning)
                _installer.UpdateParameters(ref graph, binning.Uniforms, MemoryMarshal.AsBytes(patches));
    }

    internal ulong MeshRasterOffset(uint bin) => checked((ulong)_installer.GraphData.Resources.RasterBinCount * 8u * ClusterIndirectLayout.DrawIndexedBytes + (ulong)bin * ClusterIndirectLayout.DispatchBytes);
    internal void UpdateRasterCapacity(ref RenderGraphEdit graph, uint previousCount, ImmutableDictionary<int, RasterPhase> phases)
    {
        if (previousCount == _installer.GraphData.Resources.RasterBinCount)
            return;
        Span<ClusterSoftwareRasterUniforms> software = stackalloc ClusterSoftwareRasterUniforms[1];
        foreach (RasterPhase phase in phases.Values)
            foreach (RasterBin bin in phase.Bins.Values)
            {
                if (bin.Inputs.SoftwareUniforms is { } uniforms)
                {
                    software[0] = new ClusterSoftwareRasterUniforms
                    {
                        ScreenWidth = checked((uint)_installer.GraphData.Resources.Width),
                        ScreenHeight = checked((uint)_installer.GraphData.Resources.Height),
                        MaxBins = _installer.GraphData.Resources.RasterBinCount,
                        DebugDump = _options.EnableFrameMetricsReadback ? 1u : 0u,
                        CurrentBin = bin.Stage.BinIndex,
                        EarlyDepthEnabled = bin.Stage.UsesEarlyDepth ? 1u : 0u,
                        MaxVisibleClusters = _options.MaxCandidates
                    };
                    _installer.UpdateParameters(ref graph, uniforms, MemoryMarshal.AsBytes(software));
                }

                if (bin.Inputs.MeshDraw is { } mesh)
                    graph.SetBufferAccess(mesh.Access, GraphAccessMode.Read, WriteCoverage.Partial, PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument, new BufferRange(MeshRasterOffset(mesh.Bin), ClusterIndirectLayout.DispatchBytes));
            }
        _installer.ResizeRasterScans(ref graph);
    }

    internal void UpdateRasterDispatches(ref RenderGraphFrame graph)
    {
        foreach (RasterPhase phase in _installer.GraphData.RasterPhases.Values)
        {
            if (phase.TessellationGroups.IsEmpty)
                continue;
            foreach (RasterBin bin in phase.Bins.Values)
                if (bin.Inputs.MeshDraw is { } mesh)
                    graph.SetPassDataIfChanged(mesh.Pass, mesh.Data with { IndirectOffset = MeshRasterOffset(mesh.Bin) });
        }

        var reset = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (_installer.GraphData.Resources.RasterBinCount + 63u) / 64u), 1u, 1u));
        var reserve = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (_installer.GraphData.Resources.RasterBinCount + 3u) / 4u), 1u, 1u));
        var prepare = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (_installer.GraphData.Resources.RasterBinCount + 256u) / 256u), 1u, 1u));
        foreach (RasterBinning? binning in _installer.GraphData.RasterBinning)
        {
            if (binning is null)
                continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
            graph.SetPassDataIfChanged(binning.Prepare, prepare);
        }

        uint patchBinCount = checked(_installer.GraphData.Resources.RasterBinCount * 8u);
        reset = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (patchBinCount + 63u) / 64u), 1u, 1u));
        reserve = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u, (patchBinCount + 127u) / 128u), 1u, 1u));
        foreach (RasterPhase phase in _installer.GraphData.RasterPhases.Values)
        {
            if (phase.Patches.Values.FirstOrDefault()?.PatchBinning is not { } binning)
                continue;
            graph.SetPassDataIfChanged(binning.Reset, reset);
            graph.SetPassDataIfChanged(binning.Reserve, reserve);
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct ClusterSceneUniforms
    {
        internal uint InstanceCount;
        internal uint HasPreviousBounds;
        internal uint UseInstanceGroups;
        private uint Padding;
        internal Vector3 PreviousWorldTranslation;
        private float TranslationPadding;
    }

    internal RenderInstanceBatch? _sceneBatch;
    internal ulong _sceneInstanceRevision;
    internal ulong _sceneGeometryRevision;
    internal ulong _sceneRasterRevision;
    internal WorldOrigin _sceneOrigin;
    internal void InstallSceneBounds(ref RenderGraphEdit graph)
    {
        uint capacity = checked((uint)_resources.InstanceResources.Options.RowCapacity);
        _installer.GraphData.SceneChunks = graph.CreatePersistentBuffer(new BufferDesc(_options.UseInstanceGroups ? (ulong)((capacity + 63u) / 64u) * 32u : 32u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Cluster spatial chunk bounds"));
        _installer.GraphData.InstanceWorldBounds = graph.CreatePersistentBuffer(new BufferDesc((ulong)capacity * 64u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Cluster current and previous instance bounds"));
        var clear = new ClusterBufferClearParameters(_installer.GraphData.InstanceWorldBounds);
        _installer.GraphData.SceneInitialization = graph.AddRegion("Cluster instance bounds initialization", _regions.PrimaryGeometry);
        graph.AddCopyFramePass("Initialize Cluster instance bounds", PassQueueSelection.AnyOfType(QueueType.Copy), clear, default, static (ref PassDefinition pass, ref ClusterBufferClearParameters data) => _ = pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete), ClusterBufferClearParameters.Record, region: _installer.GraphData.SceneInitialization);
        _installer.GraphData.SceneBoundsRegion = graph.AddRegion("Cluster scene bounds", _regions.PrimaryGeometry);
        GraphPassId bounds = _installer.AddComputePass(ref graph, "Update Cluster spatial scene bounds", _pipelines!.SceneBounds, new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)), 0, ClusterDispatchParameters.Record, region: _installer.GraphData.SceneBoundsRegion);
        _installer.GraphData.SceneBounds = new ClusterLayerDispatch(bounds, 1, 1);
        _installer.GraphData.SceneBoundsCommit = _installer.AddComputePass(ref graph, "Commit Cluster instance bounds",
            _pipelines.SceneBoundsCommit, new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)), 0,
            ClusterDispatchParameters.Record, region: _regions.Feedback);
        _installer.GraphData.SceneInstances = graph.CreateBufferInput(BufferUsages.ShaderRead);
        _installer.GraphData.SceneInstanceData = _installer.CreateBufferData(ref graph, ClusterBytes.Zero(_options.UseInstanceGroups ? checked((int)Math.Max(capacity, 1u) * sizeof(uint)) : sizeof(uint)), BufferUsages.ShaderRead, "Cluster spatial instance indices");
        _installer.GraphData.SceneUniforms = _installer.CreateParameters(ref graph, new byte[32], "Cluster scene bounds parameters");
        PassDefinition pass = graph.GetPass(_installer.GraphData.SceneBounds.Pass);
        _ = pass.Bind(_installer.GraphData.SceneUniforms, PipelineSync.ComputeShading);
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(graph.CreateBufferSrv(_installer.GraphData.Resources.Bvh, label: "Cluster scene BVH")));
        _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.Resources.InstanceData), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferCbv(_installer.GraphData.Resources.InstanceProperties, _installer.GraphData.Resources.InstancePropertiesRange), PipelineSync.ComputeShading);
        BindDeformBounds(ref graph, ref pass);
        _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.SceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_installer.GraphData.SceneChunks, structureStride: 16), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_installer.GraphData.InstanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.Resources.MaterialBounds, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
        PassDefinition commit = graph.GetPass(_installer.GraphData.SceneBoundsCommit);
        foreach (ref readonly ShaderParameterResource resource in _pipelines.SceneBoundsCommit.ParameterResources.Resources)
        {
            if (ClusterShadingPipeline.BindInactiveStageResource(ref commit, resource)) continue;
            if (resource.Name == "Scene") commit.Bind(_installer.GraphData.SceneUniforms, PipelineSync.ComputeShading);
            else commit.Bind(graph.CreateBufferUav(_installer.GraphData.InstanceWorldBounds, structureStride: 16),
                PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        }
        _sceneBatch = null;
    }

    internal void BindSceneBounds(ref RenderGraphFrame graph, RenderWorld world, RenderInstanceBatch batch, bool hasTargetViews, WorldOrigin worldOrigin)
    {
        bool hasPrevious = ReferenceEquals(_sceneBatch, batch);
        bool changed = !hasPrevious || _sceneInstanceRevision != batch.ContentRevision || _sceneGeometryRevision != GeometryRevision || _sceneRasterRevision != RasterRevision || _sceneOrigin != worldOrigin || !_resources.GeometryBuilds(ref graph).IsEmpty || _sceneDeformRevision != (_deformBounds?.Revision ?? 0);
        // Movement changes bounds, not the instance membership of the spatial groups.
        // Keep their order until batch replacement changes the row mapping; the GPU refits
        // the existing groups below. Geometry residency changes also preserve that mapping.
        if (_options.UseInstanceGroups && !hasPrevious)
        {
            int count = batch.InstanceCount;
            var scenePositions = new Vector3[count];
            var sceneRemap = new uint[count];
            var sceneInstances = new uint[count];
            var positions = new ScenePositions(_instances.Current!, scenePositions);
            world.ExecuteQuery(_sceneQuery, ref positions, static (QueryCursor query, ref ScenePositions data) => data.Collect(query));
            if (count != 0)
                Meshopt.SpatialSortRemap(sceneRemap, MemoryMarshal.Cast<Vector3, float>(scenePositions), 12u);
            for (uint index = 0; index < count; index++)
                sceneInstances[sceneRemap[index]] = index;
            if (count != 0)
            {
                graph.WriteBuffer(_installer.GraphData.SceneInstanceData, 0, MemoryMarshal.AsBytes(sceneInstances.AsSpan()));
            }
        }

        graph.SetBufferInput(_installer.GraphData.SceneInstances, new GraphBufferSlice(_installer.GraphData.SceneInstanceData, new BufferRange(0, _options.UseInstanceGroups ? checked((ulong)Math.Max(batch.InstanceCount, 1) * sizeof(uint)) : sizeof(uint))));
        GetFrameData(ref graph).SceneUniforms = new ClusterSceneUniforms
        {
            InstanceCount = checked((uint)batch.InstanceCount),
            HasPreviousBounds = hasPrevious ? 1u : 0u,
            UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
            PreviousWorldTranslation = hasPrevious ? _sceneOrigin.RelativeTo(worldOrigin) : Vector3.Zero
        };
        graph.SetPassData(_installer.GraphData.SceneBounds.Pass, new ClusterDispatchParameters(new DispatchArguments((checked((uint)batch.InstanceCount) + 63u) / 64u, 1, 1)));
        graph.SetPassData(_installer.GraphData.SceneBoundsCommit, new ClusterDispatchParameters(
            new DispatchArguments((checked((uint)batch.InstanceCount) + 63u) / 64u, 1, 1)));
        bool hasGeometry = batch.InstanceCount != 0 && _installer.GraphData.Outputs.Geometry.GetFrameRanges(ref graph).HasBvhData;
        graph.SetRegionEnabled(_installer.GraphData.SceneBoundsRegion, changed && hasGeometry);
        graph.SetPassEnabled(_installer.GraphData.SceneBoundsCommit, changed && hasGeometry);
        SetGeometryPassesEnabled(ref graph, hasGeometry, hasGeometry && hasTargetViews);
        graph.SetRegionEnabled(_installer.GraphData.SceneInitialization, _sceneBatch is null);
    }

    internal struct ScenePositions(RenderInstanceBatches<RenderInstanceSingleGroup> batches, Vector3[] positions)
    {
        private int _entityIndex;
        internal void Collect(QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                Vector3 position = row.Read<RenderTransform>().Position;
                foreach (var address in batches.RowsForEntity(_entityIndex++))
                    positions[address.Row] = position;
            }
        }
    }

    internal void SetGeometryPassesEnabled(ref RenderGraphFrame graph, bool hasGeometry, bool hasTargetGeometry)
    {
        CullWork work = _installer.GraphData.Resources.Work;
        graph.SetRegionEnabled(work.Initialization, !work.Initialized);
        bool scatter = hasGeometry && ClusterShadingPipeline.CalculateShadeBinItemCapacity(_installer.GraphData.Resources) == 0u;
        graph.SetRegionEnabled(_installer.GraphData.PrimaryGeometry, hasGeometry);
        graph.SetRegionEnabled(_regions.TargetGeometry, hasTargetGeometry);
        if (_installer.GraphData.TargetFirstReference != default)
            graph.SetRegionEnabled(_installer.GraphData.TargetFirstReference, hasTargetGeometry);
        if (_installer.GraphData.TargetSecondReference != default)
            graph.SetRegionEnabled(_installer.GraphData.TargetSecondReference, hasTargetGeometry);
        graph.SetRegionEnabled(_installer.GraphData.ShadeBinning, hasGeometry);
        graph.SetRegionEnabled(_installer.GraphData.ShadeGroups.Parent, hasGeometry);
        graph.SetRegionEnabled(_installer.GraphData.ShadeScatter, scatter);
    }

    internal void InstallLeafFlagFixups(ref RenderGraphEdit graph)
    {
        _installer.GraphData.LeafFlagRegion = graph.AddRegion("Cluster streaming fixups", _regions.PrimaryGeometry);
        _installer.GraphData.LeafFlagPass = _installer.AddComputePass(ref graph, "Apply Cluster streaming leaf flags", _pipelines!.LeafFlagFixups, new ClusterDispatchParameters(new DispatchArguments(0, 1, 1)), 0, ClusterDispatchParameters.Record, region: _installer.GraphData.LeafFlagRegion);
        _installer.GraphData.LeafFlagParameters = graph.CreateBufferInput(BufferUsages.Constant);
        _installer.GraphData.LeafFlagData = graph.CreateBufferInput(BufferUsages.ShaderRead);
        _installer.GraphData.LeafFlagEmptyData = _installer.CreateBufferData(ref graph, ClusterBytes.Zero(8), BufferUsages.ShaderRead, "Cluster empty fixup records");
        _installer.GraphData.LeafFlagEmptyParameters = _installer.CreateBufferData(ref graph, ClusterBytes.Zero(16), BufferUsages.Constant, "Cluster empty fixup parameters");
        PassDefinition pass = graph.GetPass(_installer.GraphData.LeafFlagPass);
        _ = pass.Bind(_installer.GraphData.LeafFlagParameters, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.LeafFlagData, structureStride: 8), PipelineSync.ComputeShading);
        pass.Bind(GraphParameterResourceBinding.WritableBufferDescriptor(graph.CreateBufferUav(_installer.GraphData.Resources.PageHeap)));
        graph.OrderAfter(_installer.GraphData.LeafFlagPass, _installer.GraphData.PageUpload);
        graph.OrderAfter(_installer.GraphData.SceneBounds.Pass, _installer.GraphData.LeafFlagPass);
    }

    internal void BindLeafFlagFixups(ref RenderGraphFrame graph, ClusterResidentGeometry? residencyUpdates)
    {
        ReadOnlySpan<ClusterLeafFlagFixup> data = residencyUpdates?.Meshes.LeafFlagFixups ?? [];
        if (data.IsEmpty)
        {
            graph.SetBufferInput(_installer.GraphData.LeafFlagParameters, new GraphBufferSlice(_installer.GraphData.LeafFlagEmptyParameters, BufferRange.Whole));
            graph.SetBufferInput(_installer.GraphData.LeafFlagData, new GraphBufferSlice(_installer.GraphData.LeafFlagEmptyData, BufferRange.Whole));
            graph.SetRegionEnabled(_installer.GraphData.LeafFlagRegion, false);
            return;
        }

        Span<uint> parameters = stackalloc uint[4];
        parameters.Clear();
        parameters[0] = checked((uint)data.Length);
        graph.SetBufferInput(_installer.GraphData.LeafFlagParameters, graph.Upload(parameters, BufferUsages.Constant, "Cluster streaming fixup parameters"));
        graph.SetBufferInput(_installer.GraphData.LeafFlagData, graph.Upload(data, BufferUsages.ShaderRead, "Cluster streaming fixup records"));
        graph.SetPassData(_installer.GraphData.LeafFlagPass, new ClusterDispatchParameters(new DispatchArguments((uint)((data.Length + 63) / 64), 1, 1)));
        graph.SetRegionEnabled(_installer.GraphData.LeafFlagRegion, !data.IsEmpty);
        PassDefinition pass = graph.GetPass(_installer.GraphData.LeafFlagPass);
        uint[] addresses = data.ToArray().Select(static fixup => fixup.ClusterByteAddress + 60u).ToArray();
        Array.Sort(addresses);
        uint first = addresses[0], end = first + sizeof(uint);
        for (int index = 1; index <= addresses.Length; index++)
        {
            if (index < addresses.Length && addresses[index] <= end)
            {
                end = addresses[index] + sizeof(uint);
                continue;
            }

            _ = pass.ReadWrite(_installer.GraphData.Resources.PageHeap, new BufferRange(first, end - first), PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            if (index < addresses.Length)
            {
                first = addresses[index];
                end = first + sizeof(uint);
            }
        }
    }

    internal void BindWorkStatus(ref RenderGraphEdit graph, ref PassDefinition pass) => _ = pass.Bind(graph.CreateBufferUav(_installer.GraphData.Resources.WorkStatus), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    internal void RecordWorkStatus(ref RenderGraphEdit graph, in ClusterGraphInstaller.FrameResources frame)
    {
        foreach (GraphBufferId buffer in new[] { frame.WorkStatus, frame.RasterBatchDemand })
        {
            var clear = new ClusterBufferClearParameters(buffer);
            graph.AddCopyFramePass(buffer == frame.WorkStatus ? "Reset Cluster work overflow status" : "Reset Cluster raster batch demand", PassQueueSelection.AnyCompatible(), clear, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), static (ref PassDefinition pass, ref ClusterBufferClearParameters data) => _ = pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete), ClusterBufferClearParameters.Record, region: _regions.PrimaryGeometry);
        }
    }

    public ClusterPipelineOutputs ReconcileGraph()
    {
        ClusterMaterialChange change = _shading._materialAssignments.TakeChanges();
        _installer.Apply(change);
        _installer.GrowRasterWork();
        return _installer.GraphData.Outputs;
    }

    public bool HasGeometryChanges => _resources.Residency.GetBinding().Changes is not null;
    public float StreamingQuality => _resources.Residency.StreamingQuality;

    public void UninstallGraph() => _installer.UninstallGraph();

    public void ReplaceShader(Shader previous, Shader next)
    {
        if (!_configuration.Operations.Any(operation => operation.Shaders.Any(shader => ReferenceEquals(shader, previous))))
            return;
        var configuration = new ClusterShaders
        {
            Name = _configuration.Name,
            Operations = _configuration.Operations.Select(operation => operation with
            {
                Shaders = operation.Shaders.Select(shader => ReferenceEquals(shader, previous) ? next : shader).ToImmutableList(),
                Entries = operation.Entries.Select((entry, index) => ReferenceEquals(operation.Shaders[index], previous)
                    ? next.RequireSlangEntry(next.RequireSlangTarget(_device.Capabilities.CompileTarget), entry.Name, entry.Stage)
                    : entry).ToImmutableList(),
            }).ToImmutableList(),
        };
        var pipelines = new ClusterPipelineSet(_backend, _device, _shading._renderDevice.Pipelines, configuration, _visibilityMode);
        if (_pipelines is { } previousPipelines)
            _installer.ReplacePipelines(previousPipelines.Replacements(pipelines), previousPipelines.RasterBinScanShader, pipelines.RasterBinScanShader);
        _configuration = configuration;
        _pipelines = pipelines;
    }
    internal RenderInstanceChangeData _instanceChanges = null !;
    public RenderInstanceChangeData InstanceChanges => _instanceChanges;

    public bool HasFirstPersonWorldMeshes() => _instanceChanges.HasFirstPersonWorldMeshes();
    public bool CollectChangedInstances(ulong afterRevision, List<uint> rows, List<uint> streamingRows, out ulong revision)
    {
        if (_resources.Residency.GetBinding().Changes is { } changes)
            _instanceChanges.RecordGeometryChanges(changes.Sequence, changes.Meshes.Bvh.ChangedMeshes, _resources.LodChanges.Span.ToArray().Select(change => change.Mesh));
        _instanceChanges.SetStreamingQuality(_resources.Residency.StreamingQuality);
        return _instanceChanges.CollectChangedInstances(afterRevision, rows, streamingRows, out revision);
    }

    public ReadOnlySpan<DepthCacheClass> CollectInstanceCacheInputs(List<Vector4> lodBounds, out uint version)
    {
        var changes = _resources.LodChanges.Span.ToArray().Select(change => new RenderMeshLodChange(change.RootNode, change.Bounds)).ToArray();
        return _instanceChanges.CollectInstanceCacheInputs(changes, lodBounds, out version);
    }

    public ReadOnlySpan<DepthCacheClass> CollectInstanceCacheInputs(List<Vector4> lodBounds) => CollectInstanceCacheInputs(lodBounds, out _);
    public void CollectInstanceEntities(Span<Entity> entities, uint afterVersion, List<uint> changedRows) => _instanceChanges.CollectInstanceEntities(entities, afterVersion, changedRows);
}
