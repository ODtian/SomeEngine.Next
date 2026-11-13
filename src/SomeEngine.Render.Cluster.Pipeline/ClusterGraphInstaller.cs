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
public sealed partial class ClusterGraphInstaller
{
    private readonly ClusterRasterBatchFeedback _rasterBatchFeedback = new();
    internal ClusterRasterStageSet? _installedRasterStages;
    internal ClusterShadeStageSet? _installedShadeStages;
    internal ulong _installedMaterialTopologyVersion;
    internal ClusterGeometryPipeline _geometry = null !;
    internal ClusterShadingPipeline _shading = null !;
    public ClusterGraphInstaller(global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        _graph = graph;
    }

    internal void Attach(ClusterGeometryPipeline geometry, ClusterShadingPipeline shading)
    {
        _geometry = geometry;
        _shading = shading;
    }

    internal void ReplacePipelines(IReadOnlyDictionary<global::SomeEngine.Graphics.Pipeline, ClusterPipeline> replacements, Shader previousRasterShader, Shader nextRasterShader)
    {
        EditGraph((ref RenderGraphEdit edit) =>
        {
            foreach (ClusterGeometryPipeline.RasterBinning? binning in GraphData.RasterBinning)
            {
                if (binning is null) continue;
                ScreenBinningScanPasses.ReplaceShader(ref edit,binning.BatchScan,_shading._renderDevice.Pipelines,previousRasterShader,nextRasterShader);
                ScreenBinningScanPasses.ReplaceShader(ref edit,binning.SoftwareScan,_shading._renderDevice.Pipelines,previousRasterShader,nextRasterShader);
            }
            foreach (GraphPassId id in GraphData.Passes)
            {
                PassDefinition pass = edit.GetPass(id);
                if (pass.Pipeline is { } current && replacements.TryGetValue(current, out ClusterPipeline? replacement))
                {
                    pass.SetPipeline(replacement.Pipeline);
                    if (replacement.ParameterLayout != VariableLayoutReflection.Null)
                        pass.SetParameterBlock(replacement.ParameterLayout);
                }
            }
        });
    }

    internal readonly record struct BufferWrite(BufferRange Range, ClusterBytes Bytes);
    internal readonly record struct ShaderParameters(int Offset, int Capacity);
    internal GraphBufferId CreateBufferData(ref RenderGraphEdit edit, ClusterBytes bytes, BufferUsages usages, string label)
    {
        var description = new BufferDesc(checked((ulong)Math.Max(bytes.Length, 4)), usages | BufferUsages.CopyDestination, label);
        GraphBufferId buffer = edit.CreatePersistentBuffer(description);
        GraphPassId upload = edit.AddBufferUploadPass(buffer);
        GraphData.BufferDescriptions = GraphData.BufferDescriptions.Add(buffer, (description, upload));
        QueueBufferWrites(buffer, bytes, [new BufferRange(0, checked((ulong)bytes.Length))]);
        return buffer;
    }

    internal GraphBufferId CreateBufferData(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> bytes, BufferUsages usages, string label) => CreateBufferData(ref edit, ClusterBytes.FromBytes(bytes), usages, label);
    internal void UpdateBufferData(ref RenderGraphEdit edit, GraphBufferId buffer, ClusterBytes bytes, scoped ReadOnlySpan<BufferRange> changes)
    {
        var data = GraphData.BufferDescriptions[buffer];
        BufferDesc description = data.Description;
        ulong size = checked((ulong)Math.Max(bytes.Length, 4));
        if (description.Size != size)
        {
            description = description with
            {
                Size = size
            };
            edit.SetBufferDescription(buffer, description);
            GraphData.BufferDescriptions = GraphData.BufferDescriptions.SetItem(buffer, (description, data.UploadPass));
            GraphData.BufferWrites = GraphData.BufferWrites.Remove(buffer);
            QueueBufferWrites(buffer, bytes, [new BufferRange(0, checked((ulong)bytes.Length))]);
        }
        else
            QueueBufferWrites(buffer, bytes, changes);
    }

    internal void QueueBufferWrites(GraphBufferId buffer, ClusterBytes bytes, scoped ReadOnlySpan<BufferRange> changes)
    {
        var writes = GraphData.BufferWrites.GetValueOrDefault(buffer, ImmutableList<BufferWrite>.Empty);
        if (changes.Length > 1)
        {
            var builder = writes.ToBuilder();
            foreach (BufferRange range in changes)
            {
                if (range.Size == 0)
                    continue;
                builder.Add(new BufferWrite(range, bytes));
            }

            writes = builder.ToImmutable();
        }
        else
        {
            foreach (BufferRange range in changes)
            {
                if (range.Size == 0)
                    continue;
                writes = writes.Add(new BufferWrite(range, bytes));
            }
        }

        GraphData.BufferWrites = GraphData.BufferWrites.SetItem(buffer, writes);
    }

    internal void RemoveBufferData(ref RenderGraphEdit edit, GraphBufferId buffer)
    {
        edit.Remove(buffer);
        GraphData.BufferDescriptions = GraphData.BufferDescriptions.Remove(buffer);
        GraphData.BufferWrites = GraphData.BufferWrites.Remove(buffer);
    }

    internal GraphBufferSlice CreateParameters(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> bytes, string label)
    {
        ShaderParameters parameters = AllocateParameters(ref edit, bytes);
        GraphBufferSlice input = new(GraphData.ShaderParameterBytes, new BufferRange(checked((ulong)parameters.Offset), checked((ulong)parameters.Capacity)));
        return input;
    }

    internal ShaderParameters AllocateParameters(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> bytes)
    {
        int alignment = checked((int)Math.Max(1u, _geometry._device.Capabilities.Limits.ConstantBufferAlignment));
        int capacity = checked((bytes.Length + alignment - 1) / alignment * alignment);
        int offset;
        if (GraphData.ParameterFreeOffsets.TryGetValue(capacity, out var available) && !available.IsEmpty)
        {
            offset = available.Peek();
            GraphData.ParameterFreeOffsets = GraphData.ParameterFreeOffsets.SetItem(capacity, available.Pop());
        }
        else
        {
            offset = GraphData.ParameterByteCount;
            GraphData.ParameterByteCount = checked(offset + capacity);
            var description = new BufferDesc(checked((ulong)GraphData.ParameterByteCount), BufferUsages.Constant, "Cluster shader parameters");
            if (GraphData.ShaderParameterBytes == default)
                GraphData.ShaderParameterBytes = edit.CreateTransientBuffer(description, MemoryType.Upload);
            else
                edit.SetBufferDescription(GraphData.ShaderParameterBytes, description, MemoryType.Upload);
        }

        var parameters = new ShaderParameters(offset, capacity);
        WriteParameterData(parameters, bytes);
        return parameters;
    }

    internal void UpdateParameters(ref RenderGraphEdit edit, GraphBufferSlice input, scoped ReadOnlySpan<byte> bytes)
    {
        ShaderParameters parameters = new(checked((int)input.Range.Offset), checked((int)input.Range.Size));
        DevelopmentCheck.Require(bytes.Length <= parameters.Capacity, "Each shader parameter record keeps the size of its declared type.");
        WriteParameterData(parameters, bytes);
    }

    internal void WriteParameterData(ShaderParameters parameters, scoped ReadOnlySpan<byte> bytes)
    {
        byte[] record = new byte[parameters.Capacity];
        bytes.CopyTo(record);
        GraphData.ParameterData = GraphData.ParameterData.WithChanges(GraphData.ParameterByteCount, [(parameters.Offset, record)]);
    }

    internal void ReleaseParameters(ShaderParameters parameters) => GraphData.ParameterFreeOffsets = GraphData.ParameterFreeOffsets.SetItem(parameters.Capacity, GraphData.ParameterFreeOffsets.GetValueOrDefault(parameters.Capacity, ImmutableStack<int>.Empty).Push(parameters.Offset));
    internal void RemoveParameters(GraphBufferSlice input)
    {
        ReleaseParameters(new(checked((int)input.Range.Offset), checked((int)input.Range.Size)));
    }

    internal readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    internal bool _graphInstalled;
    internal ClusterGraphData _graphData = new();
    internal ClusterGraphData? _editingGraphData;
    internal ClusterGraphData GraphData => _editingGraphData ?? _graphData;

    internal enum FeedbackKind
    {
        Candidates,
        FrameMetrics
    }

    internal sealed class ClusterGraphData
    {
        internal ImmutableDictionary<GraphBufferId, (BufferDesc Description, GraphPassId UploadPass)> BufferDescriptions = ImmutableDictionary<GraphBufferId, (BufferDesc, GraphPassId)>.Empty;
        internal ImmutableDictionary<GraphBufferId, ImmutableList<BufferWrite>> BufferWrites = ImmutableDictionary<GraphBufferId, ImmutableList<BufferWrite>>.Empty;
        internal ClusterBytes ParameterData = ClusterBytes.Zero(0);
        internal GraphBufferId ShaderParameterBytes;
        internal int ParameterByteCount;
        internal ImmutableDictionary<int, ImmutableStack<int>> ParameterFreeOffsets = ImmutableDictionary<int, ImmutableStack<int>>.Empty;
        internal GraphPassId[] Passes = [];
        internal GraphReadbackId PageFaultReadback;
        internal GraphReadbackId RasterBatchReadback;
        internal GraphReadbackId FrameMetricsReadback;
        internal GraphPassId PageUpload;
        internal GraphPassId BvhUpload;
        internal GraphRegionId LeafFlagRegion;
        internal GraphPassId LeafFlagPass;
        internal GraphBufferInput LeafFlagParameters = null !;
        internal GraphBufferInput LeafFlagData = null !;
        internal GraphBufferId LeafFlagEmptyData;
        internal GraphBufferId LeafFlagEmptyParameters;
        internal GraphRegionId TargetFirstReference;
        internal GraphRegionId TargetSecondReference;
        internal GraphRegionId PrimaryGeometry;
        internal GraphRegionId HistoryInitialization;
        internal GraphRegionId SceneInitialization;
        internal GraphRegionId SceneBoundsRegion;
        internal GraphRegionId ShadeBinning;
        internal GraphRegionId ShadeCountRegion;
        internal GraphPassId ShadeCountPass;
        internal GraphRegionId ShadeScatter;
        internal FrameResources Resources;
        internal ClusterPipelineOutputs Outputs;
        internal ClusterLayerDispatch SceneBounds;
        internal GraphPassId SceneBoundsCommit;
        internal ClusterLayerDispatch PrimarySeed;
        internal ClusterLayerDispatch[] ShadeBinDispatches = [];
        internal ImmutableDictionary<FeedbackKind, GraphPassId> Feedback = ImmutableDictionary<FeedbackKind, GraphPassId>.Empty;
        internal ImmutableDictionary<int, ClusterGeometryPipeline.RasterPhase> RasterPhases = ImmutableDictionary<int, ClusterGeometryPipeline.RasterPhase>.Empty;
        internal ClusterGeometryPipeline.RasterBinning? [] RasterBinning = new ClusterGeometryPipeline.RasterBinning? [ClusterGeometryPipeline.RasterPhaseCount];
        internal GraphRegionMap<uint, ClusterShadingPipeline.ShadeGroup> ShadeGroups = null !;
        internal List<ClusterLayerDispatch> LayerDispatches = [];
        internal List<ClusterLayerDispatch> WinnerDispatches = [];
        internal GraphBufferSlice ShadeBinUniforms;
        internal ImmutableDictionary<(Material Material, uint Property), ClusterShadingPipeline.MaterialInput> MaterialInputs = ImmutableDictionary<(Material Material, uint Property), ClusterShadingPipeline.MaterialInput>.Empty;
        internal ImmutableHashSet<(Material Material, uint Property)> MaterialInputChanges = ImmutableHashSet<(Material Material, uint Property)>.Empty;
        internal ClusterShadingPipeline.MaterialTexture? MissingMaterialTexture;
        internal GraphBufferInput ShadeScalarOffsets = null !;
        internal GraphBufferInput ShadeScalars = null !;
        internal GraphBufferId ScalarBytes;
        internal GraphBufferId ScalarOffsets;
        internal GraphBufferSlice PrimarySeedUniforms;
        internal GraphBufferSlice TargetSeedUniforms;
        internal GraphBufferSlice LayerSeedUniforms;
        internal ClusterLayerDispatch LayerSeed;
        internal GraphRegionId LayerCullingRegion;
        internal uint LayerTileSize;
        internal uint LayerHardwareWindowTiles;
        internal GraphBufferInput? LayerRasterMaterials;
        internal RenderTessellationRasterRegistration? LayerTessellation;
        internal RenderGeometryResources LayerGeometry = null!;
        internal ClusterGeometryPipeline.RasterInputs TargetInputs = new();
        internal GraphBufferId SceneChunks;
        internal GraphBufferId InstanceWorldBounds;
        internal GraphBufferId SceneInstanceData;
        internal GraphBufferInput SceneInstances = null !;
        internal GraphBufferSlice SceneUniforms;
        internal ImmutableDictionary<SomeEngine.Graphics.Buffer, ClusterShadingPipeline.MaterialBuffer> MaterialBuffers = ImmutableDictionary<SomeEngine.Graphics.Buffer, ClusterShadingPipeline.MaterialBuffer>.Empty;
        internal ImmutableDictionary<SomeEngine.Resources.Texture, ClusterShadingPipeline.MaterialTexture> MaterialTextures = ImmutableDictionary<SomeEngine.Resources.Texture, ClusterShadingPipeline.MaterialTexture>.Empty;
        internal ClusterGraphData Copy()
        {
            var data = (ClusterGraphData)MemberwiseClone();
            data.LayerDispatches = new(LayerDispatches);
            data.WinnerDispatches = new(WinnerDispatches);
            data.RasterBinning = (ClusterGeometryPipeline.RasterBinning? [])RasterBinning.Clone();
            return data;
        }
    }

    internal delegate void GraphEditWork(ref RenderGraphEdit edit);
    internal void EditGraph(GraphEditWork update)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        _editingGraphData = _graphData.Copy();
        try
        {
            update(ref edit);
            HashSet<GraphRegionId> regions =
            [
                _geometry._regions.PrimaryGeometry,
                _geometry._regions.Shading,
                _geometry._regions.Feedback,
                _geometry._regions.LayerVisibility,
            ];
            if (_editingGraphData.TargetFirstReference != default) regions.Add(_editingGraphData.TargetFirstReference);
            if (_editingGraphData.TargetSecondReference != default) regions.Add(_editingGraphData.TargetSecondReference);
            foreach (ClusterGeometryPipeline.RasterPhase phase in _editingGraphData.RasterPhases.Values)
                regions.Add(phase.Region);
            var passes = new List<GraphPassId>();
            foreach (GraphRegionId region in regions) passes.AddRange(edit.GetPasses(region));
            _editingGraphData.Passes = passes.Distinct().ToArray();
            edit.Commit();
            _shading.AcceptMaterialTextures(_editingGraphData.MaterialTextures, _editingGraphData.MissingMaterialTexture);
            _graphData = _editingGraphData;
        }
        finally
        {
            edit.Dispose();
            _editingGraphData = null;
        }
    }

    internal bool _graphRemoved;
    internal static ClusterGraphRegions CreateGraphRegions(ref RenderGraphEdit edit, in ClusterGraphRegions regions) => new(
        edit.AddRegion("Cluster pipeline geometry", regions.PrimaryGeometry),
        edit.AddRegion("Cluster pipeline targets", regions.TargetGeometry),
        edit.AddRegion("Cluster pipeline shading", regions.Shading),
        edit.AddRegion("Cluster pipeline feedback", regions.Feedback),
        LayerVisibility: edit.AddRegion("Cluster layer geometry", regions.LayerVisibility),
        LayerShading: regions.LayerShading,
        LayerComposition: regions.LayerComposition);
    /// <summary>
    /// Removes this pipeline at a frame boundary, after consumers have detached its outputs.
    /// Submitted work retires through RenderGraph; shared geometry remains installed.
    /// An uninstalled pipeline cannot be installed again. Dispose its frame-system container afterwards.
    /// </summary>
    public void UninstallGraph()
    {
        if (_graphRemoved)
            return;
        using RenderGraphEdit editScope = _graph.BeginEdit();
        RenderGraphEdit edit = editScope;
        foreach (ClusterGeometryPipeline.RasterBinning? binning in GraphData.RasterBinning)
            if (binning is not null) RemoveRasterScans(ref edit,binning);
        if (GraphData.TargetFirstReference != default) edit.Remove(GraphData.TargetFirstReference);
        if (GraphData.TargetSecondReference != default) edit.Remove(GraphData.TargetSecondReference);
        edit.Remove(_geometry._regions.PrimaryGeometry);
        edit.Remove(_geometry._regions.TargetGeometry);
        edit.Remove(_geometry._regions.Shading);
        edit.Remove(_geometry._regions.Feedback);
        edit.Remove(_geometry._regions.LayerVisibility);
        if (_graphInstalled)
        {
            foreach (var data in GraphData.BufferDescriptions.Values) edit.Remove(data.UploadPass);
            FrameResources frame = GraphData.Resources;
            HashSet<GraphBufferId> buffers = [frame.WorkStatus, frame.FrameMetricsBuffer, frame.PackedViews, frame.RasterBinIndices, frame.MaterialBounds, frame.ShadeBinIndices, frame.ReadOffsetZero, frame.CandidateArgs, frame.NeutralPolicyValue, frame.NeutralPolicyCounter, frame.NeutralPolicyView, frame.TraversalNodes0, frame.TraversalArguments0, frame.CandidateArgsInitialization, frame.CandidateClusters, frame.CandidateCount, frame.DrawArgs, frame.PageFaults, frame.Phase2CandidateArgs, frame.Phase2CandidateClusters, frame.Phase2CandidateCount, frame.Phase2DrawArgs, frame.ContinuationDrawArgs, frame.ContinuationPostDrawArgs, frame.VisibleClusters, frame.TargetDeferredWork, frame.TargetDeferredCount, frame.TargetDeferredArgs, frame.TargetVisibleClusters, frame.RasterBinMeta, frame.DepthBuckets, frame.BinnedClusters, frame.RasterBatchOffsets, frame.RasterSoftwareOffsets, frame.RasterSpillClusters, frame.RasterSpillArguments, frame.RasterBatchDemand, frame.HardwareIndirectArgs, frame.RasterWorkArguments, frame.SoftwareDispatchArgs, frame.RasterReserveCounters, frame.TessellationCounters, frame.PhaseOneTessellationCounters, frame.TessellationWorkPatches, frame.TessellationWorkPatchesAlternate, frame.TessellationFinalPatches, frame.TessellationLevelCounts, frame.TessellationSourceDispatchArgs, frame.TessellationSeedDispatchArgs, frame.TessellationBinCounts, frame.TessellationBinOffsets, frame.TessellationBinScatterCounts, frame.TessellationBinReserveCounter, frame.TessellationBinDispatchArgs, frame.TessellationHardwareDrawArgs, frame.TessellationPatternIndices, frame.SoftwareDebug, frame.ShadeBinCounts, frame.ShadeBinOffsets, frame.ShadeIndirectArgs, frame.ShadeScatterCounts, frame.ShadeReserveCounters, frame.PixelCoordinates, frame.VisibilityWinnerBuffer, GraphData.ShaderParameterBytes, GraphData.SceneChunks, GraphData.InstanceWorldBounds, frame.Work.Nodes, frame.Work.Clusters, frame.Work.NodeReady, frame.Work.ClusterReady, frame.Work.Arguments, ];
            buffers.UnionWith(frame.TessellationLevelDispatchArgs);
            buffers.UnionWith([frame.LayerPackedViews, frame.LayerVisibleClusters, frame.LayerTileWindows,
                frame.LayerDrawArgs, frame.LayerSecondDrawArgs, frame.LayerDeferredWork,
                frame.LayerDeferredCount, frame.LayerDeferredArgs]);
            buffers.UnionWith(GraphData.BufferDescriptions.Keys);
            foreach (GraphBufferId buffer in buffers)
                if (buffer != default)
                    edit.Remove(buffer);
            HashSet<GraphTextureId> textures = [frame.NeutralPolicyOutput, frame.VisBuffer, frame.VisibilityWinnerImage, frame.Depth, frame.PolicyVisibilityScratch, frame.PolicyDepthScratch, frame.SceneColor, frame.MotionVectors, ];
            textures.UnionWith(frame.HiZ);
            foreach (GraphTextureId texture in textures)
                if (texture != default)
                    edit.Remove(texture);
        }

        edit.Commit();
        _graphRemoved = true;
        GraphData.Outputs = default;
    }

    private readonly Dictionary<string, GraphResourceBinder> _passResourceBinders = new(StringComparer.Ordinal);
    internal ImmutableDictionary<int, ClusterGeometryPipeline.RasterPhase> InstallGraph(ref RenderGraphEdit graph, in ClusterPipelineOutputDescription output, ClusterMaterialAssignmentSet assignments, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        FrameResources frame = CreateFrameResources(ref graph, in output, assignments, rasterStages, shadeStages);
        ClusterGeometryPipeline.RasterBin[] rasterBins = _geometry.CreateRasterBins(rasterStages, shadeStages);
        GraphData.Resources = frame;
        AuthorHistoryInitialization(ref graph, frame);
        frame.PackedViews = graph.CreateTransientBuffer(new BufferDesc(checked((ulong)output.ViewCount * ClusterViewUniforms.SizeInBytes), BufferUsages.ShaderRead, "Cluster packed views"), MemoryType.Upload);
        GraphData.Resources = frame;
        RecordFrameMetricsSourceReset(ref graph, frame);
        _geometry.RecordWorkStatus(ref graph, frame);
        _geometry.RecordDeformReset(ref graph, frame);
        RecordCullStateReset(ref graph, frame);
        _geometry.InstallSceneBounds(ref graph);
        _geometry.InstallLeafFlagFixups(ref graph);
        GraphData.PrimaryGeometry = graph.AddRegion("Cluster primary geometry work", _geometry._regions.PrimaryGeometry);
        GraphRegionId primaryTraversal = graph.AddRegion("Cluster primary traversal", GraphData.PrimaryGeometry);
        RecordWorkSeed(ref graph, frame, frame.PackedViews, frame.ViewCount, 0u, "primary", primaryTraversal);
        _geometry.RecordPersistentCull(ref graph, frame, frame.PackedViews, 0u, frame.DrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, frame.ReadOffsetZero, false, 0, "primary", primaryTraversal);
        ClusterGeometryPipeline.RasterPhase first = RecordRasterPhase(ref graph, frame, rasterBins, frame.DrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, frame.ReadOffsetZero, 0, true);
        GraphRegionId firstHiZ = graph.AddRegion("Cluster first HiZ", GraphData.PrimaryGeometry);
        RecordHiZ(ref graph, frame, true, firstHiZ);
        RecordPhaseOneTessellationCounters(ref graph, frame, firstHiZ);
        GraphRegionId primaryDeferredTraversal = graph.AddRegion("Cluster primary deferred traversal", GraphData.PrimaryGeometry);
        RecordDeferredResume(ref graph, frame, frame.Phase2DrawArgs, "primary post-HiZ", primaryDeferredTraversal);
        _geometry.RecordPersistentCull(ref graph, frame, frame.PackedViews, 2u, frame.Phase2DrawArgs, frame.DrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, false, 1, "primary post-HiZ", primaryDeferredTraversal);
        ClusterGeometryPipeline.RasterPhase second = RecordRasterPhase(ref graph, frame, rasterBins, frame.Phase2DrawArgs, frame.DrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, 1, false);
        GraphPassId geometryComplete = RecordHiZ(ref graph, frame, false, graph.AddRegion("Cluster second HiZ", GraphData.PrimaryGeometry));
        var phases = ImmutableDictionary<int, ClusterGeometryPipeline.RasterPhase>.Empty.Add(0, first).Add(1, second);
        if (GraphData.LayerTileSize != 0u)
        {
            GraphData.RasterPhases = phases;
            InstallLayers(ref graph, rasterStages, shadeStages);
            frame = GraphData.Resources;
            phases = GraphData.RasterPhases;
        }
        phases = RecordContinuation(ref graph, frame, rasterBins, frame.PackedViews, phases);
        RecordShade(ref graph, frame, shadeStages);
        RecordPageFaultReadback(ref graph, frame);
        RecordFrameMetricsReadback(ref graph, frame, firstHiZ);
        GraphData.Outputs = new ClusterPipelineOutputs(frame.VisBuffer, frame.Depth, frame.SceneColor, frame.MotionVectors, new RenderGeometryResources(frame.PackedViews, frame.InstanceData, frame.InstanceProperties, frame.InstancePropertiesRange, frame.VisibleClusters, frame.DrawArgs, frame.Phase2DrawArgs, _geometry._options.MaxCandidates, frame.PageHeap, frame.Bvh, geometryComplete) { DeformCache = frame.DeformCache, DeformRegions = frame.DeformRegions, InstanceWorldBounds = GraphData.InstanceWorldBounds, WorkStatus = frame.WorkStatus, BoundsReady = GraphData.SceneBounds.Pass }, frame.Width, frame.Height, frame.ViewCount)
        {
            CurrentHiZ = frame.HiZ[0]
        };
        return phases;
    }

    internal ImmutableDictionary<int, ClusterGeometryPipeline.RasterPhase> RecordContinuation(ref RenderGraphEdit graph, in FrameResources frame, ClusterGeometryPipeline.RasterBin[] rasterBins, GraphBufferId packedViews, ImmutableDictionary<int, ClusterGeometryPipeline.RasterPhase> phases)
    {
        if (!_geometry._renderContext.IsValid)
            return phases;
        ClusterGeometryPipeline.RasterInputs? previousInputs = _geometry._rasterInputs;
        _geometry._rasterInputs = GraphData.TargetInputs;
        try
        {
            FrameResources targets = GetRasterFrameResources(frame, true);
            GraphData.TargetFirstReference = graph.AddRegion("Cluster target main geometry", _geometry._targetPhaseOneRegion);
            GraphData.TargetSecondReference = graph.AddRegion("Cluster target post-HiZ geometry", _geometry._targetPhaseTwoRegion);
            var reset = new ClusterContinuationResetParameters(targets.CandidateCount, targets.TraversalArguments0, targets.CandidateArgsInitialization, targets.CandidateArgs, targets.ContinuationDrawArgs, targets.Phase2CandidateCount, targets.Phase2CandidateArgs, targets.ContinuationPostDrawArgs);
            graph.AddCopyFramePass("Reset Cluster continuation scratch", PassQueueSelection.AnyOfType(QueueType.Graphics), reset, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), ClusterContinuationResetParameters.Declare, ClusterContinuationResetParameters.Record, region: GraphData.TargetFirstReference);
            GraphRegionId targetTraversal = graph.AddRegion("Cluster target traversal", GraphData.TargetFirstReference);
            RecordWorkSeed(ref graph, targets, packedViews, _geometry._renderContext.MaximumDispatchCount, 1u, "continuation", targetTraversal);
            _geometry.RecordPersistentCull(ref graph, targets, packedViews, 1u, targets.ContinuationDrawArgs, targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ReadOffsetZero, true, 2, "continuation", targetTraversal);
            ClusterGeometryPipeline.RasterPhase first = RecordRasterPhase(ref graph, targets, rasterBins, targets.ContinuationDrawArgs, targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ReadOffsetZero, 2, true);
            _geometry._rasterInputs = GraphData.TargetInputs;
            GraphRegionId targetDeferredTraversal = graph.AddRegion("Cluster target deferred traversal", GraphData.TargetSecondReference);
            RecordDeferredResume(ref graph, targets, targets.ContinuationPostDrawArgs, "continuation post-HiZ", targetDeferredTraversal);
            _geometry.RecordPersistentCull(ref graph, targets, packedViews, 2u, targets.ContinuationPostDrawArgs, targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ContinuationDrawArgs, true, 3, "continuation post-HiZ", targetDeferredTraversal);
            ClusterGeometryPipeline.RasterPhase second = RecordRasterPhase(ref graph, targets, rasterBins, targets.ContinuationPostDrawArgs, targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ContinuationDrawArgs, 3, false);
            return phases.Add(2, first).Add(3, second);
        }
        finally { _geometry._rasterInputs = previousInputs; }
    }

    internal void ReconfigureRenderContext()
    {
        EditGraph((ref RenderGraphEdit edit) =>
        {
            var inputs = new HashSet<ClusterGeometryPipeline.RasterInputs>(ReferenceEqualityComparer.Instance)
            {
                GraphData.TargetInputs,
            };
            var phases = GraphData.RasterPhases;
            foreach ((int index, ClusterGeometryPipeline.RasterPhase phase) in GraphData.RasterPhases)
            {
                if (!phase.PolicyTarget) continue;
                foreach (ClusterGeometryPipeline.RasterBin bin in phase.Bins.Values) inputs.Add(bin.Inputs);
                foreach (ClusterGeometryPipeline.RasterBin bin in phase.Deformation.Values) inputs.Add(bin.Inputs);
                foreach (ClusterGeometryPipeline.RasterInputs patch in phase.Patches.Values) inputs.Add(patch);
                foreach (ClusterGeometryPipeline.TessellationGroup group in phase.TessellationGroups.Values)
                {
                    foreach (ClusterGeometryPipeline.RasterBin seed in group.Seeds.Values) inputs.Add(seed.Inputs);
                    foreach (ClusterGeometryPipeline.TessellationSplitStage split in group.Split.Values) inputs.Add(split.Inputs);
                }
                phases = phases.Remove(index);
            }
            for (int index = 2; index <= 3; index++)
                if (GraphData.RasterBinning[index] is { } targetBinning) RemoveRasterScans(ref edit,targetBinning);
            if (GraphData.TargetFirstReference != default) edit.Remove(GraphData.TargetFirstReference);
            if (GraphData.TargetSecondReference != default) edit.Remove(GraphData.TargetSecondReference);
            foreach (ClusterGeometryPipeline.RasterInputs input in inputs) _geometry.ReleaseRasterInputs(ref edit, input);
            GraphData.TargetInputs = new();
            GraphData.TargetFirstReference = default;
            GraphData.TargetSecondReference = default;

            FrameResources frame = GraphData.Resources;
            if (frame.ContinuationDrawArgs == default)
            {
                ulong candidates = _geometry._options.MaxCandidates;
                frame.ContinuationDrawArgs = frame.AddBuffer(ref edit, 16, BufferUsages.CopySource, "Cluster continuation counts");
                frame.ContinuationPostDrawArgs = frame.AddBuffer(ref edit, 16, BufferUsages.CopySource, "Cluster continuation post counts");
                frame.TargetVisibleClusters = frame.AddBuffer(ref edit, candidates * VisibleClusterStride, BufferUsages.None, "Cluster target visible clusters");
                frame.TargetDeferredWork = frame.AddBuffer(ref edit, candidates * CandidateStride, BufferUsages.None, "Cluster target deferred work");
                frame.TargetDeferredCount = frame.AddBuffer(ref edit, sizeof(uint), BufferUsages.None, "Cluster target deferred count");
                frame.TargetDeferredArgs = frame.AddBuffer(ref edit, 16, BufferUsages.Indirect, "Cluster target deferred dispatch");
            }
            if (_geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly)
            {
                RenderRasterContext context = _geometry._renderContext;
                TextureDesc visibility = new(TextureDimension.Texture2D, context.RasterWidth, context.RasterHeight,
                    1, 1, 1, 1, Format.R32UInt, TextureUsages.ColorAttachment, label: "Cluster policy hardware visibility scratch");
                TextureDesc depth = new(TextureDimension.Texture2D, context.RasterWidth, context.RasterHeight,
                    1, 1, 1, 1, Format.D32Float, TextureUsages.DepthStencilAttachment,
                    label: "Cluster policy hardware depth scratch");
                if (frame.PolicyVisibilityScratch == default)
                {
                    frame.PolicyVisibilityScratch = edit.CreateTransientTexture(visibility);
                    frame.PolicyDepthScratch = edit.CreateTransientTexture(depth);
                    RecordPolicyScratchClear(ref edit, frame);
                }
                else
                {
                    edit.SetTextureDescription(frame.PolicyVisibilityScratch, visibility);
                    edit.SetTextureDescription(frame.PolicyDepthScratch, depth);
                }
            }
            GraphData.Resources = frame;
            GraphData.RasterBinning[2] = null;
            GraphData.RasterBinning[3] = null;
            ClusterGeometryPipeline.RasterBin[] bins = _geometry.CreateRasterBins(_installedRasterStages!, _installedShadeStages!);
            GraphData.RasterPhases = RecordContinuation(ref edit, frame, bins, frame.PackedViews, phases);
            _geometry.UpdateRasterBinning(ref edit, GraphData.RasterPhases);
        });
    }

    internal void RecordFrameMetricsReadback(ref RenderGraphEdit graph, in FrameResources frame, GraphRegionId candidateRegion)
    {
        if (!_geometry._options.EnableFrameMetricsReadback)
            return;
        ClusterFrameMetricsReadbackParameters passData = new()
        {
            CandidateCount = frame.CandidateCount,
            CandidateArgs = frame.CandidateArgs,
            DrawArgs = frame.DrawArgs,
            Phase2CandidateCount = frame.Phase2CandidateCount,
            Phase2CandidateArgs = frame.Phase2CandidateArgs,
            Phase2DrawArgs = frame.Phase2DrawArgs,
            RasterReserve = frame.RasterReserveCounters,
            ShadeCount = frame.ShadeBinCounts,
            ShadeReserve = frame.ShadeReserveCounters,
            SoftwareDebug = frame.SoftwareDebug,
            PhaseOneTessellationCounters = frame.PhaseOneTessellationCounters,
            PhaseTwoTessellationCounters = frame.TessellationCounters,
            WorkStatus = frame.WorkStatus,
            Visibility = frame.VisBuffer,
            VisibilityProbeX = checked((uint)Math.Max(0, frame.Width / 2 - VisibilityProbePixelCount / 2)),
            VisibilityProbeY = checked((uint)(frame.Height / 2)),
            Destination = frame.FrameMetricsBuffer
        };
        GraphPassId candidates = graph.AddCopyFramePass("Copy primary Cluster candidate metrics", PassQueueSelection.AnyCompatible(), passData, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), static (ref PassDefinition access, ref ClusterFrameMetricsReadbackParameters data) =>
        {
            Read(ref access, data.CandidateCount, 0, sizeof(uint));
            Read(ref access, data.CandidateArgs, 0, 12);
            Read(ref access, data.Phase2CandidateCount, 0, sizeof(uint));
            Read(ref access, data.Phase2CandidateArgs, 0, 12);
            access.Write(data.Destination, new BufferRange(CandidateCountReadbackOffset, 16), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            access.Write(data.Destination, new BufferRange(Phase2CandidateCountReadbackOffset, 16), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, ClusterFrameMetricsReadbackParameters.RecordCandidates, region: candidateRegion);
        GraphData.Feedback = GraphData.Feedback.SetItem(FeedbackKind.Candidates, candidates);
        GraphPassId metrics = graph.AddCopyFramePass("Copy Cluster frame metrics", PassQueueSelection.AnyCompatible(), passData, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), static (ref PassDefinition access, ref ClusterFrameMetricsReadbackParameters data) =>
        {
            Read(ref access, data.DrawArgs, 0, 16);
            Read(ref access, data.Phase2DrawArgs, 0, 16);
            Read(ref access, data.RasterReserve, 2 * sizeof(uint), 2 * sizeof(uint));
            Read(ref access, data.ShadeCount, 0, sizeof(uint));
            Read(ref access, data.ShadeReserve, 0, sizeof(uint));
            Read(ref access, data.SoftwareDebug, 0, sizeof(uint));
            Read(ref access, data.PhaseOneTessellationCounters, 0, 8 * sizeof(uint));
            Read(ref access, data.PhaseTwoTessellationCounters, 0, 8 * sizeof(uint));
            Read(ref access, data.WorkStatus, 0, WorkMetricsByteSize);
            access.Write(data.Destination, new BufferRange(WorkMetricsReadbackOffset, WorkMetricsByteSize), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            _ = access.Read(data.Visibility, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
            _ = access.Write(data.Destination, new BufferRange(DrawArgsReadbackOffset, 16), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            access.Write(data.Destination, new BufferRange(Phase2DrawArgsReadbackOffset, 100), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            access.Write(data.Destination, new BufferRange(VisibilityProbeReadbackOffset, VisibilityProbeRowPitch), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, ClusterFrameMetricsReadbackParameters.Record, region: _geometry._regions.Feedback);
        GraphData.Feedback = GraphData.Feedback.SetItem(FeedbackKind.FrameMetrics, metrics);
        GraphData.FrameMetricsReadback = graph.AddReadback(frame.FrameMetricsBuffer, BufferRange.Whole, new ClusterGeometryPipeline.FrameMetricsReceiver(_geometry), "Read back Cluster frame metrics", _geometry._regions.Feedback);
    }

    internal void RecordFrameMetricsSourceReset(ref RenderGraphEdit graph, in FrameResources frame)
    {
        if (!_geometry._options.EnableFrameMetricsReadback)
            return;
        var passData = new ClusterFrameMetricsSourceResetParameters(frame.RasterReserveCounters, frame.ShadeBinCounts, frame.ShadeReserveCounters, frame.SoftwareDebug, frame.PhaseOneTessellationCounters, frame.TessellationCounters);
        _ = graph.AddCopyFramePass("Reset Cluster frame metric sources", PassQueueSelection.AnyCompatible(), passData, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), ClusterFrameMetricsSourceResetParameters.Declare, ClusterFrameMetricsSourceResetParameters.Record, region: _geometry._regions.PrimaryGeometry);
    }

    internal void RecordPageFaultReadback(ref RenderGraphEdit graph, in FrameResources frame)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, "Views", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.PackedViews, ClusterViewUniforms.SizeInBytes), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "PageFaultBuffer", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.PageFaults), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        GraphPassId requests = AddComputePass(ref graph, "Complete Cluster page request priorities", _geometry._pipelines!.PageRequests, new DispatchArguments((uint)(frame.PageFaultCapacity + 63) / 64u, 1, 1), bindingCount, static (ref PassDefinition _, ref DispatchArguments _) =>
        {
        }, static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch), region: _geometry._regions.Feedback);
        ulong workOffset = checked(sizeof(uint) + (ulong)frame.PageFaultCapacity * 16u);
        var workCopy = new ClusterBufferCopyParameters(frame.WorkStatus, 0, frame.PageFaults, workOffset, sizeof(uint));
        GraphPassId work = graph.AddCopyFramePass("Copy Cluster work result", PassQueueSelection.AnyCompatible(), workCopy, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
        {
            access.Read(data.Source, new BufferRange(data.SourceOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopySource);
            access.Write(data.Destination, new BufferRange(data.DestinationOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, ClusterBufferCopyParameters.Record, region: _geometry._regions.Feedback);
        graph.OrderAfter(work, requests);
        GraphData.PageFaultReadback = graph.AddReadback(frame.PageFaults, new BufferRange(0, checked(3u * sizeof(uint) + (ulong)frame.PageFaultCapacity * 24u)), new ClusterGeometryPipeline.PageFaultReceiver(_geometry, frame.PageFaultCapacity, _geometry._resources.EpochId), "Read back Cluster page requests", _geometry._regions.Feedback);
        graph.OrderAfter(GraphData.PageFaultReadback, work);
        GraphData.RasterBatchReadback = graph.AddReadback(frame.RasterBatchDemand,new(0,sizeof(uint)),
            _rasterBatchFeedback,"Cluster raster batch demand feedback",_geometry._regions.Feedback);
        graph.OrderAfter(GraphData.RasterBatchReadback,work);
    }

    internal void RecordWorkSeed(ref RenderGraphEdit graph, in FrameResources frame, GraphBufferId packedViews, uint viewCount, uint viewIndexMode, string epochLabel, GraphRegionId region, bool layerTiles = false)
    {
        bool target = viewIndexMode != 0u;
        ClusterComputePipeline seedPipeline = target ? _geometry._pipelines!.TargetTraversalInitialize : _geometry._pipelines!.TraversalInitialize;
        FrameResources rasterFrame = GetRasterFrameResources(frame, target);
        GraphBufferSlice seedUniforms = UploadUniform(ref graph, new ClusterTraversalSeedUniforms { ViewCount = viewCount, ViewIndexMode = viewIndexMode, MaxNodes = _geometry._options.MaxCandidates, ArgsOffset = 0u, UseInstanceGroups = _geometry._options.UseInstanceGroups ? 1u : 0u, DispatchCountIndex = target ? _geometry._renderContext.DispatchCountIndex : 2u }, $"Cluster {epochLabel} traversal seed uniforms");
        if (layerTiles)
            GraphData.LayerSeedUniforms = seedUniforms;
        else if (target)
            GraphData.TargetSeedUniforms = seedUniforms;
        else
            GraphData.PrimarySeedUniforms = seedUniforms;
        GraphPassId seedPass;
        if (target)
        {
            var indirect = new ClusterIndirectDispatchParameters(_geometry.RequireDispatchIndirectLayout(), _geometry._renderContext.DispatchArguments, 0u);
            seedPass = AddComputePass(ref graph, $"Seed Cluster {epochLabel} hierarchy roots", seedPipeline, indirect, 0, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
        }
        else
        {
            seedPass = AddComputePass(ref graph, $"Seed Cluster {epochLabel} hierarchy roots", seedPipeline, new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)), 0, ClusterDispatchParameters.Record, region: region);
            if (layerTiles) GraphData.LayerSeed = new ClusterLayerDispatch(seedPass, 1, 1);
            else GraphData.PrimarySeed = new ClusterLayerDispatch(seedPass, 1, 1);
        }

        var resources = new Dictionary<string, GraphParameterResourceBinding>(StringComparer.Ordinal)
        {
            ["Views"] = GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, packedViews, ClusterViewUniforms.SizeInBytes), PipelineSync.ComputeShading),
            ["GlobalBVH"] = GraphParameterResourceBinding.ReadOnlyBufferDescriptor(CreateReadOnlyBufferView(graph, frame.Bvh)),
            ["InstanceData"] = GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading),
            ["InstanceProperties"] = GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading),
            ["TraversalNodes"] = GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TraversalNodes0, TraversalNodeStride), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix),
            ["NodeIndirectArgs"] = GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TraversalArguments0), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete),
            ["TraversalSeed"] = GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, seedUniforms), PipelineSync.ComputeShading),
            ["HierarchyHiZ"] = GraphParameterResourceBinding.SampledTexture(graph.CreateTextureSrv(rasterFrame.PreviousHiZ, format: Format.R32Float, dimension: TextureViewDimension.Texture2DArray, label: "Cluster hierarchy history HiZ"), PipelineSync.ComputeShading),
            ["DeferredWork"] = GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, rasterFrame.Phase2CandidateClusters, CandidateStride), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix),
            ["DeferredWorkCount"] = GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, rasterFrame.Phase2CandidateCount, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete),
            ["DeferredWorkDispatchArgs"] = GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, rasterFrame.Phase2CandidateArgs), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete),
            ["SceneInstances"] = GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(GraphData.SceneInstances, structureStride: sizeof(uint)), PipelineSync.ComputeShading),
            ["SceneChunks"] = GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, GraphData.SceneChunks, 16), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix),
            ["InstanceWorldBounds"] = GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, GraphData.InstanceWorldBounds, 16), PipelineSync.ComputeShading),
            ["MaterialBounds"] = GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.MaterialBounds, sizeof(uint)), PipelineSync.ComputeShading),
            ["TraversalReady"] = GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.Work.NodeReady, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial),
            ["ClusterBatchReady"] = GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.Work.ClusterReady, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial),
        };
        PassDefinition seed = graph.GetPass(seedPass);
        foreach (ref readonly ShaderParameterResource input in seedPipeline.ParameterResources.Resources)
        {
            if (ClusterShadingPipeline.BindInactiveStageResource(ref seed, input)) continue;
            if (resources.TryGetValue(input.Name, out GraphParameterResourceBinding binding))
            {
                seed.Bind(binding);
                continue;
            }
            if (input.Name.StartsWith("RasterTarget", StringComparison.Ordinal))
            {
                _geometry.BindRasterTargetResource(ref graph, ref seed, input.Name, input.Type, target);
                continue;
            }
            switch (input.Name)
            {
                case "PolicyActiveViewCount":
                    if (target) _geometry._renderContext.DispatchCountBinder(ref graph, ref seed, PipelineSync.ComputeShading);
                    else _geometry.BindNeutralDispatchCount(ref graph, ref seed);
                    break;
                case "PolicyClusterViews":
                    _geometry.BindPolicyClusterViews(ref graph, ref seed, PipelineSync.ComputeShading, target);
                    break;
                case "WorkStatus":
                    _geometry.BindWorkStatus(ref graph, ref seed);
                    break;
                case "ClusterCurrentBounds":
                case "ClusterPreviousBounds":
                    _geometry.BindDeformBounds(ref graph, ref seed, input.Name == "ClusterPreviousBounds");
                    break;
                default:
                    throw new InvalidDataException($"Cluster seed input '{input.Name}' has no resource.");
            }
        }
    }

    internal void RecordDeferredResume(ref RenderGraphEdit graph, in FrameResources frame, GraphBufferId drawArgs, string epochLabel, GraphRegionId region)
    {
        var reset = new ClusterDeferredResumeResetParameters(frame.CandidateCount, frame.CandidateArgsInitialization, frame.CandidateArgs, frame.TraversalArguments0, drawArgs);
        graph.AddCopyFramePass($"Reset Cluster {epochLabel} deferred-resume outputs", PassQueueSelection.AnyOfType(QueueType.Graphics), reset, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), ClusterDeferredResumeResetParameters.Declare, ClusterDeferredResumeResetParameters.Record, region: region == default ? _geometry._regions.PrimaryGeometry : region);
        GraphBufferSlice uniforms = UploadUniform(ref graph, new ClusterDeferredResumeUniforms { MaxWorkItems = _geometry._options.MaxCandidates }, $"Cluster {epochLabel} deferred-resume uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, "ResumeUniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "DeferredWork", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.Phase2CandidateClusters, CandidateStride), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, "DeferredWorkCount", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.Phase2CandidateCount, sizeof(uint)), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "TraversalNodes", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TraversalNodes0, TraversalNodeStride), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, "TraversalArguments", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TraversalArguments0), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "CandidateClusters", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.CandidateClusters, CandidateStride), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, "CandidateCount", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.CandidateCount, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "CandidateArgs", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.CandidateArgs), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "WorkStatus", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.WorkStatus), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "TraversalReady", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.Work.NodeReady, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial));
        AddBinding(ref bindingCount, "ClusterBatchReady", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.Work.ClusterReady, sizeof(uint)), PipelineSync.ComputeShading));
        var indirect = new ClusterIndirectDispatchParameters(_geometry.RequireDispatchIndirectLayout(), frame.Phase2CandidateArgs, 0u);
        _ = AddComputePass(ref graph, $"Resume Cluster {epochLabel} deferred hierarchy work", _geometry._pipelines!.DeferredResume, indirect, bindingCount, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
    }

    internal void RecordCullStateReset(ref RenderGraphEdit graph, in FrameResources frame)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, "DrawArgs", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.DrawArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "Phase2DrawArgs", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.Phase2DrawArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        ClusterDispatchParameters clear = new(new DispatchArguments(1, 1, 1));
        _ = AddComputePass(ref graph, "Reset Cluster primary cull", _geometry._pipelines!.CullReset, clear, bindingCount, ClusterDispatchParameters.Record, region: _geometry._regions.PrimaryGeometry);
    }

    internal ClusterGeometryPipeline.RasterPhase RecordRasterPhase(ref RenderGraphEdit graph, in FrameResources frame, ClusterGeometryPipeline.RasterBin[] rasterBins, GraphBufferId drawArgs, GraphBufferId readOffsetArgs0, GraphBufferId readOffsetArgs1, GraphBufferId readOffsetArgs2, int epoch, bool phaseOne)
    {
        FrameResources raster = GetRasterFrameResources(frame, epoch is 2 or 3, epoch == 4);
        GraphRegionId parent = epoch switch
        {
            2 => GraphData.TargetFirstReference,
            3 => GraphData.TargetSecondReference,
            4 => _geometry._regions.LayerVisibility,
            _ => GraphData.PrimaryGeometry,
        };
        GraphRegionId phase = graph.AddRegion($"Cluster {(epoch == 4 ? "layer" : epoch < 2 ? "primary" : "target")} {(phaseOne ? "first" : "second")} raster", parent);
        // Shared scratch follows the region tree through ordinary resource accesses.
        GraphRegionId work = graph.AddRegion("Work binning", phase);
        GraphRegionId deformation = graph.AddRegion("User deformation", phase);
        GraphRegionId groups = graph.AddRegion("Tessellation groups", phase);
        GraphRegionId patches = graph.AddRegion("Final patch binning", phase);
        GraphRegionId bins = graph.AddRegion("Raster bins", phase);
        RecordRasterWorkGeneration(ref graph, raster, drawArgs, readOffsetArgs0, readOffsetArgs1, readOffsetArgs2, phaseOne, epoch, work);
        RecordTessellationReset(ref graph, raster, phaseOne, work);
        ClusterGeometryPipeline.RasterPhase data = new ClusterGeometryPipeline.RasterPhase(phase, new GraphRegionMap<uint, ClusterGeometryPipeline.RasterBin>(bins, ClusterGeometryPipeline.RasterBinComparer.Instance), new GraphRegionMap<ClusterGeometryPipeline.TessellationSplitKey, bool>(groups), ImmutableDictionary<ClusterGeometryPipeline.TessellationSplitKey, ClusterGeometryPipeline.TessellationGroup>.Empty, new GraphRegionMap<int, ClusterGeometryPipeline.RasterInputs>(patches), phaseOne, epoch is 2 or 3)
        {
            LayerTarget = epoch == 4,
            Deformation = new GraphRegionMap<uint, ClusterGeometryPipeline.RasterBin>(deformation, ClusterGeometryPipeline.RasterBinComparer.Instance),
            DrawData = new(drawArgs, readOffsetArgs0, readOffsetArgs1, readOffsetArgs2)
        };
        data = _geometry.ReconcileRasterPhase(ref graph, data, rasterBins);
        if (epoch < 2 && _geometry._visibilityMode != ClusterVisibilityMode.HardwareOnly)
            RecordWinnerResolve(ref graph, raster, phaseOne, graph.AddRegion("Visibility resolve", phase));
        return data;
    }

    internal void RecordRasterWorkGeneration(ref RenderGraphEdit graph, in FrameResources frame, GraphBufferId drawArgs, GraphBufferId readOffsetArgs0, GraphBufferId readOffsetArgs1, GraphBufferId readOffsetArgs2, bool phaseOne, int epoch, GraphRegionId region)
    {
        bool shadowPages = epoch is 2 or 3;
        GraphRegionId resetRegion = graph.AddRegion("Reset bins", region);
        GraphRegionId countRegion = graph.AddRegion("Count work", region);
        GraphRegionId reserveRegion = graph.AddRegion("Reserve bins", region);
        GraphRegionId scatterRegion = graph.AddRegion("Scatter work", region);
        GraphBufferSlice binningUniforms = UploadUniform(ref graph, new ClusterRasterBinningUniforms { RasterMaxBins = frame.RasterBinCount, MaterialAddressCapacity = frame.MaterialAddressCapacity, MaxVisibleClusters = _geometry._options.MaxCandidates, CollectMetrics = _geometry._options.EnableFrameMetricsReadback ? 1u : 0u, PolicyTarget = epoch == 4 ? 2u : shadowPages ? 1u : 0u, ScreenWidth = checked((uint)frame.Width), ScreenHeight = checked((uint)frame.Height), ViewCount = frame.ViewCount, BatchCapacity = frame.RasterBatchCapacity, DepthBucketsEnabled = frame.DepthBucketsEnabled ? 1u : 0u }, phaseOne ? "Cluster phase one raster binning uniforms" : "Cluster phase two raster binning uniforms");
        uint binClearGroups = Math.Max(1u, (frame.RasterBinCount + 63u) / 64u);
        int bindingCount = 0;
        AddBinding(ref bindingCount, "RasterBinMeta", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "DrawArgs", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterWorkArguments", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterWorkArguments), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "ReserveCounters", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterReserveCounters, sizeof(uint)), PipelineSync.ComputeShading, _geometry._options.EnableFrameMetricsReadback ? GraphAccessMode.ReadWrite : GraphAccessMode.Write, _geometry._options.EnableFrameMetricsReadback ? WriteCoverage.Partial : WriteCoverage.Complete));
        AddBinding(ref bindingCount, "DepthBuckets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        ClusterDispatchParameters reset = new(new DispatchArguments(binClearGroups, 1, 1));
        GraphPassId resetPass = AddComputePass(ref graph, phaseOne ? "Cluster phase one raster bins reset" : "Cluster phase two raster bins reset", _geometry._pipelines!.RasterBinReset, reset, bindingCount, ClusterDispatchParameters.Record, region: resetRegion);
        bindingCount = 0;
        AddBinding(ref bindingCount, "InstanceProperties", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "InstanceData", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterBinMeta", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "VisibleClusters", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, "DrawArgs", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ClusterReadOffsetArgs", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs0), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ClusterReadOffsetArgs1", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs1), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ClusterReadOffsetArgs2", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs2), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterSlots", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.RasterBinIndices, sizeof(uint)), PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        GraphBufferId policyRasterWorkCounter = _geometry._renderContext.IsValid && shadowPages ? _geometry._renderContext.PolicyRasterWorkCounter : frame.NeutralPolicyCounter;
        AddBinding(ref bindingCount, "PolicyRasterWorkCounter", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, policyRasterWorkCounter, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial, unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
        AddBinding(ref bindingCount, "WorkStatus", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.WorkStatus), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "DepthBuckets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        if (_geometry._renderContext.IsValid && shadowPages && _geometry._renderContext.BindWorkTargetViews is { } bindWorkViews)
            AddBinding(ref bindingCount, "WorkTargetViews", bindWorkViews);
        else
            AddBinding(ref bindingCount, "WorkTargetViews", GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(frame.NeutralPolicyView, structureStride: RenderRasterContext.TargetViewSizeInBytes), PipelineSync.ComputeShading));
        if (_geometry._renderContext.IsValid && shadowPages && _geometry._renderContext.BindDepthCacheClasses is { } bindDepthClasses)
            AddBinding(ref bindingCount, "RasterTargetDepthCacheClasses", bindDepthClasses);
        else
            AddBinding(ref bindingCount, "RasterTargetDepthCacheClasses", GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(frame.NeutralPolicyView, structureStride: sizeof(uint)), PipelineSync.ComputeShading));
        GraphBufferId depthWorkCounter = _geometry._renderContext.IsValid && shadowPages && _geometry._renderContext.DepthWorkCounter != default ? _geometry._renderContext.DepthWorkCounter : frame.NeutralPolicyCounter;
        AddBinding(ref bindingCount, "DepthWorkCounter", GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(depthWorkCounter, structureStride: sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial, unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
        ClusterIndirectDispatchParameters count = new(_geometry.RequireDispatchIndirectLayout(), frame.RasterWorkArguments, 0);
        GraphPassId countPass = AddComputePass(ref graph, epoch == 4 ? "Layer raster bins count" : phaseOne ? "Cluster phase one raster bins count" : "Cluster phase two raster bins count", _geometry._pipelines.RasterBinCount, count, bindingCount, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: countRegion);
        bindingCount = 0;
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterBinMeta", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride), PipelineSync.ComputeShading, GraphAccessMode.Read));
        AddBinding(ref bindingCount, "RasterBatchOffsets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBatchOffsets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "RasterSoftwareOffsets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterSoftwareOffsets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        var prepareDispatch = new ClusterDispatchParameters(new DispatchArguments(Math.Max(1u,(frame.RasterBinCount + 256u)/256u),1u,1u));
        GraphPassId prepare = AddComputePass(ref graph, "Prepare Cluster raster bin offsets", _geometry._pipelines.RasterBinPrepare,
            prepareDispatch, bindingCount, ClusterDispatchParameters.Record, region: reserveRegion);
        graph.OrderAfter(prepare,countPass);
        ScreenBinningScanResources batchScan = ScreenBinningScanPasses.Add(ref graph, _shading._renderDevice.Pipelines,
            _geometry._pipelines.RasterBinScanShader, frame.RasterBatchOffsets, frame.RasterBinCount + 1u, prepare, reserveRegion);
        ScreenBinningScanResources softwareScan = ScreenBinningScanPasses.Add(ref graph, _shading._renderDevice.Pipelines,
            _geometry._pipelines.RasterBinScanShader, frame.RasterSoftwareOffsets, frame.RasterBinCount + 1u, batchScan.CompletePass, reserveRegion);
        bindingCount = 0;
        AddBinding(ref bindingCount, "RasterBinMeta", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "BinnedHWDrawArgs", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.HardwareIndirectArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "BinnedSWDispatchArgs", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.SoftwareDispatchArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "TessellationSeedDispatchArgs", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationSeedDispatchArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "ReserveCounters", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterReserveCounters, sizeof(uint)), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "WorkStatus", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.WorkStatus), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "DepthBuckets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        AddBinding(ref bindingCount, "RasterBatchOffsets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBatchOffsets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Read));
        AddBinding(ref bindingCount, "RasterSoftwareOffsets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterSoftwareOffsets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Read));
        AddBinding(ref bindingCount, "RasterBatchDemand", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBatchDemand, sizeof(uint)), PipelineSync.ComputeShading));
        ClusterDispatchParameters reserve = new(new DispatchArguments(Math.Max(1u, (frame.RasterBinCount + 3u) / 4u), 1, 1));
        GraphPassId reservePass = AddComputePass(ref graph, phaseOne ? "Cluster phase one raster bins reserve" : "Cluster phase two raster bins reserve", _geometry._pipelines.RasterBinReserve, reserve, bindingCount, ClusterDispatchParameters.Record, region: reserveRegion);
        graph.OrderAfter(reservePass,softwareScan.CompletePass);
        bindingCount = 0;
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterBatchOffsets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBatchOffsets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Read));
        AddBinding(ref bindingCount, "DrawArgs", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph,drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterSpillArguments", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph,frame.RasterSpillArguments), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        GraphPassId spillArguments = AddComputePass(ref graph,"Prepare Cluster spilled raster arguments",_geometry._pipelines.RasterSpillPrepare,
            new DispatchArguments(1,1,1),bindingCount,static(ref PassDefinition _,ref DispatchArguments _)=>{},
            static(ref ComputePassCommandScope commands,in DispatchArguments args)=>commands.Dispatch(args),region:reserveRegion);
        graph.OrderAfter(spillArguments,reservePass);
        bindingCount = 0;
        AddBinding(ref bindingCount, "InstanceProperties", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "InstanceData", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterBinMeta", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "VisibleClusters", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, "BinnedClusterIndexBuffer", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.BinnedClusters, BinnedClusterStride), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedRanges));
        AddBinding(ref bindingCount,"RasterSpillClusters",GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph,frame.RasterSpillClusters,sizeof(uint)),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Partial,ResourceContentState.Defined,BufferContentScope.CountBoundedRanges));
        AddBinding(ref bindingCount, "DrawArgs", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ClusterReadOffsetArgs", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs0), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ClusterReadOffsetArgs1", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs1), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ClusterReadOffsetArgs2", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs2), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "RasterSlots", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.RasterBinIndices, sizeof(uint)), PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, "WorkStatus", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.WorkStatus), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "DepthBuckets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        ClusterIndirectDispatchParameters scatter = new(_geometry.RequireDispatchIndirectLayout(), frame.RasterWorkArguments, ClusterRasterArguments.RasterScatterOffset);
        GraphPassId scatterPass = AddComputePass(ref graph, phaseOne ? "Cluster phase one raster bins scatter" : "Cluster phase two raster bins scatter", _geometry._pipelines.RasterBinScatter, scatter, bindingCount, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: scatterRegion);
        GraphData.RasterBinning[epoch] = new ClusterGeometryPipeline.RasterBinning(binningUniforms, resetPass, reservePass, shadowPages)
        { LayerTarget = epoch == 4, Prepare = prepare, BatchScan = batchScan, SoftwareScan = softwareScan, ScanRegion = reserveRegion };
    }

    internal GraphPassId RecordTessellationSeed(ref RenderGraphEdit graph, in FrameResources frame, ClusterRasterDispatch dispatchData, bool phaseOne, bool policyTarget, GraphRegionId region, ClusterGeometryPipeline.DeformDrawData draw = default, bool spilled = false, bool layerTiles = false)
    {
        ClusterTessellationDispatch tessellation = dispatchData.Tessellation!;
        uint bin = dispatchData.BinIndex;
        GraphBufferSlice uniforms = UploadUniform(ref graph, CreateTessellationUniforms(frame, bin, phaseOne, 0u), spilled ? "Cluster spilled tessellation seed uniforms" : phaseOne ? "Cluster phase one tessellation seed uniforms" : "Cluster phase two tessellation seed uniforms");
        ulong seedOffset = checked((ulong)bin * ClusterIndirectLayout.DispatchBytes);
        ClusterIndirectDispatchParameters seed = new(_geometry.RequireDispatchIndirectLayout(),
            spilled ? frame.RasterSpillArguments : frame.TessellationSeedDispatchArgs, spilled ? 0u : seedOffset);
        GraphPassId seedPass = AddComputePass(ref graph, phaseOne ? $"Cluster phase one tessellation seed bin {bin}" : $"Cluster phase two tessellation seed bin {bin}", (layerTiles ? spilled ? tessellation.LayerSpillSeed : tessellation.LayerSeed : spilled ? tessellation.SpillSeed : tessellation.Seed), seed, 0, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
        PassDefinition seedDefinition = graph.GetPass(seedPass);
        _shading.BindTessellationResources(ref graph, ref seedDefinition, dispatchData, (layerTiles ? spilled ? tessellation.LayerSpillSeedResources : tessellation.LayerSeedResources : spilled ? tessellation.SpillSeedResources : tessellation.SeedResources), uniforms, phaseOne, 0u, PipelineSync.ComputeShading, policyTarget, draw, layerTiles);
        return seedPass;
    }

    internal ClusterTessellationUniforms CreateTessellationUniforms(in FrameResources frame, uint bin, bool phaseOne, uint splitLevel) => new()
    {
        ScreenWidth = checked((uint)frame.Width),
        ScreenHeight = checked((uint)frame.Height),
        CurrentBin = bin,
        MaxPatchItems = _geometry._options.MaxCandidates,
        MaxFactor = _geometry._options.MaxTessellationFactor,
        TargetEdgePixels = _geometry._options.TessellationTargetEdgePixels,
        UsePreviousHiZ = phaseOne ? 1u : 0u,
        SplitLevel = splitLevel
    };
    internal void RecordTessellationReset(ref RenderGraphEdit graph, in FrameResources frame, bool phaseOne, GraphRegionId region)
    {
        ClusterTessellationResetParameters clear = new(frame.TessellationCounters);
        _ = graph.AddCopyFramePass(phaseOne ? "Reset Cluster phase one tessellation counters" : "Reset Cluster phase two tessellation counters", PassQueueSelection.AnyOfType(QueueType.Graphics), clear, default, static (ref PassDefinition access, ref ClusterTessellationResetParameters data) =>
        {
            _ = access.Write(data.Counters, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, ClusterTessellationResetParameters.Record, region: region);
    }

    internal GraphPassId RecordTessellationLevelReset(ref RenderGraphEdit graph, in FrameResources frame, uint bin, bool phaseOne, GraphRegionId region)
    {
        ClusterTessellationLevelResetParameters clear = new(frame.TessellationLevelCounts, frame.TessellationLevelDispatchArgs);
        return graph.AddCopyFramePass(phaseOne ? $"Reset Cluster phase one tessellation level scratch bin {bin}" : $"Reset Cluster phase two tessellation level scratch bin {bin}", PassQueueSelection.AnyOfType(QueueType.Graphics), clear, default, static (ref PassDefinition access, ref ClusterTessellationLevelResetParameters data) =>
        {
            _ = access.Write(data.Counts, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            foreach (GraphBufferId arguments in data.DispatchArguments)
            {
                _ = access.Write(arguments, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            }
        }, ClusterTessellationLevelResetParameters.Record, region: region);
    }

    internal GraphPassId RecordPhaseOneTessellationCounters(ref RenderGraphEdit graph, in FrameResources frame, GraphRegionId region)
    {
        ClusterBufferCopyParameters copy = new(frame.TessellationCounters, 0, frame.PhaseOneTessellationCounters, 0, 8 * sizeof(uint));
        return graph.AddCopyFramePass("Copy Cluster phase one tessellation counters", PassQueueSelection.AnyOfType(QueueType.Graphics), copy, default, static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
        {
            _ = access.Read(data.Source, new BufferRange(data.SourceOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = access.Write(data.Destination, new BufferRange(data.DestinationOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, ClusterBufferCopyParameters.Record, region: region);
    }

    internal void RecordTessellationBinning(ref RenderGraphEdit graph, in FrameResources frame, GraphBufferId sourcePatches, GraphBufferId destinationPatches, GraphBufferId sourceArguments, uint sourceCounterOffset, string label, GraphRegionId region)
    {
        uint binCount = checked(frame.RasterBinCount * 8u);
        GraphBufferSlice uniforms = UploadUniform(ref graph, new ClusterTessellationBinUniforms { BinCount = binCount, MaxPatchItems = _geometry._options.MaxCandidates, SourceCounterOffset = sourceCounterOffset }, $"Cluster tessellation bin uniforms {label}");
        int resetBindingCount = BindTessellationBinResources(ref graph, frame, uniforms, sourcePatches, destinationPatches, sourceArguments, TessellationBinningStage.Reset);
        ClusterDispatchParameters reset = new(new DispatchArguments(Math.Max(1u, (binCount + 63u) / 64u), 1u, 1u));
        GraphPassId resetPass = AddComputePass(ref graph, $"Reset Cluster tessellation bins {label}", _geometry._pipelines!.TessellationBinReset, reset, resetBindingCount, ClusterDispatchParameters.Record, region: region);
        int countBindingCount = BindTessellationBinResources(ref graph, frame, uniforms, sourcePatches, destinationPatches, sourceArguments, TessellationBinningStage.Count);
        ClusterIndirectDispatchParameters count = new(_geometry.RequireDispatchIndirectLayout(), sourceArguments, 0u);
        GraphPassId countPass = AddComputePass(ref graph, $"Count Cluster tessellation bins {label}", _geometry._pipelines.TessellationBinCount, count, countBindingCount, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
        int reserveBindingCount = BindTessellationBinResources(ref graph, frame, uniforms, sourcePatches, destinationPatches, sourceArguments, TessellationBinningStage.Reserve);
        ClusterDispatchParameters reserve = new(new DispatchArguments(Math.Max(1u, (binCount + 127u) / 128u), 1u, 1u));
        GraphPassId reservePass = AddComputePass(ref graph, $"Reserve Cluster tessellation bins {label}", _geometry._pipelines.TessellationBinReserve, reserve, reserveBindingCount, ClusterDispatchParameters.Record, region: region);
        int scatterBindingCount = BindTessellationBinResources(ref graph, frame, uniforms, sourcePatches, destinationPatches, sourceArguments, TessellationBinningStage.Scatter);
        ClusterIndirectDispatchParameters scatter = new(_geometry.RequireDispatchIndirectLayout(), sourceArguments, 0u);
        GraphPassId scatterPass = AddComputePass(ref graph, $"Scatter Cluster tessellation bins {label}", _geometry._pipelines.TessellationBinScatter, scatter, scatterBindingCount, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
        if (_geometry._rasterInputs is { } raster)
            raster.PatchBinning = new ClusterGeometryPipeline.RasterBinning(uniforms, resetPass, reservePass, false);
    }

    internal int BindTessellationBinResources(ref RenderGraphEdit graph, in FrameResources frame, GraphBufferSlice uniforms, GraphBufferId sourcePatches, GraphBufferId destinationPatches, GraphBufferId sourceArguments, TessellationBinningStage stage)
    {
        int bindingCount = 0;
        bool reset = stage == TessellationBinningStage.Reset;
        bool count = stage == TessellationBinningStage.Count;
        bool reserve = stage == TessellationBinningStage.Reserve;
        bool scatter = stage == TessellationBinningStage.Scatter;
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "TessellationCountersRead", reserve ? GraphParameterResourceBinding.Null(ResourceBindingType.BufferSrv) : GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.TessellationCounters), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "SourcePatches", count || scatter ? GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, sourcePatches, TessellationFinalPatchStride), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix) : GraphParameterResourceBinding.Null(ResourceBindingType.BufferSrv));
        AddBinding(ref bindingCount, "BinCounts", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationBinCounts, sizeof(uint)), PipelineSync.ComputeShading, reset ? GraphAccessMode.Write : count ? GraphAccessMode.ReadWrite : GraphAccessMode.Read, reset ? WriteCoverage.Complete : WriteCoverage.Partial));
        AddBinding(ref bindingCount, "BinOffsets", count ? GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav) : GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationBinOffsets, sizeof(uint)), PipelineSync.ComputeShading, scatter ? GraphAccessMode.Read : GraphAccessMode.Write, scatter ? WriteCoverage.Partial : WriteCoverage.Complete));
        AddBinding(ref bindingCount, "ScatterCounts", count ? GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav) : GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationBinScatterCounts, sizeof(uint)), PipelineSync.ComputeShading, scatter ? GraphAccessMode.ReadWrite : GraphAccessMode.Write, scatter ? WriteCoverage.Partial : WriteCoverage.Complete));
        AddBinding(ref bindingCount, "ReserveCounter", reset || reserve ? GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationBinReserveCounter, sizeof(uint)), PipelineSync.ComputeShading, reset ? GraphAccessMode.Write : GraphAccessMode.ReadWrite, reset ? WriteCoverage.Complete : WriteCoverage.Partial) : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, "BinDispatchArgs", reset || reserve ? GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationBinDispatchArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete) : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, "HardwareDrawArgs", reset || reserve ? GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationHardwareDrawArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete) : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, "SourceDispatchArgs", reset ? GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, sourceArguments), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete) : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, "GroupedPatchIndices", scatter ? GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, destinationPatches, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix) : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, "TessellationCountersWrite", count || reserve ? GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.TessellationCounters), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial) : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        return bindingCount;
    }

    internal enum TessellationBinningStage : byte
    {
        Reset,
        Count,
        Reserve,
        Scatter,
    }

    internal void RecordTessellationSplit(ref RenderGraphEdit graph, in FrameResources frame, ClusterRasterDispatch dispatchData, bool phaseOne, bool policyTarget, bool sharedBins, GraphRegionId region, bool layerTiles = false)
    {
        ClusterTessellationDispatch tessellation = dispatchData.Tessellation ?? throw new InvalidOperationException("A tessellation split invocation has no tessellation stages.");
        uint bin = dispatchData.BinIndex;
        for (uint level = 0u; level < (uint)TessellationSplitLevelCount; level++)
        {
            GraphBufferSlice uniforms = UploadUniform(ref graph, CreateTessellationUniforms(frame, sharedBins ? uint.MaxValue : bin, phaseOne, level), phaseOne ? $"Cluster phase one tessellation split level {level} uniforms" : $"Cluster phase two tessellation split level {level} uniforms");
            ClusterIndirectDispatchParameters split = new(_geometry.RequireDispatchIndirectLayout(), frame.TessellationLevelDispatchArgs[level], 0u);
            GraphPassId pass = AddComputePass(ref graph, phaseOne ? $"Cluster phase one tessellation split bin {bin} level {level}" : $"Cluster phase two tessellation split bin {bin} level {level}", (layerTiles ? tessellation.LayerSplit : policyTarget ? tessellation.ShadowSplit : tessellation.Split), split, 0, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
            PassDefinition splitDefinition = graph.GetPass(pass);
            _shading.BindTessellationResources(ref graph, ref splitDefinition, dispatchData, layerTiles ? tessellation.LayerSplitResources : policyTarget ? tessellation.ShadowSplitResources : tessellation.SplitResources, uniforms, phaseOne, level + 1u, PipelineSync.ComputeShading, policyTarget, layerTiles: layerTiles);
        }
    }

    internal void RecordTessellationDice(ref RenderGraphEdit graph, in FrameResources frame, IReadOnlyList<ClusterRasterDispatch> stages, bool phaseOne, bool policyTarget, GraphRegionId region)
    {
        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterDispatch dispatchData = stages[invocationIndex];
            if (dispatchData.Tessellation is not { } tessellation)
                continue;
            uint bin = dispatchData.BinIndex;
            GraphBufferSlice uniforms = UploadUniform(ref graph, CreateTessellationUniforms(frame, bin, phaseOne, 0u), phaseOne ? "Cluster phase one dicing uniforms" : "Cluster phase two dicing uniforms");
            ulong offset = checked((ulong)bin * ClusterIndirectLayout.DispatchBytes);
            ClusterIndirectDispatchParameters dice = new(_geometry.RequireDispatchIndirectLayout(), frame.TessellationBinDispatchArgs, offset);
            ClusterComputePipeline dicePipeline = (policyTarget ? tessellation.ShadowDice : tessellation.Dice) ?? throw new InvalidOperationException("A recorded compute dicing pass has no compute pipeline.");
            GraphPassId dicePass = AddComputePass(ref graph, phaseOne ? $"Cluster phase one bounded dicing bin {bin}" : $"Cluster phase two bounded dicing bin {bin}", dicePipeline, dice, 0, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, asyncCompute: true, region: region);
            graph.SetPassTimeGroup(dicePass, ClusterGeometryPipeline.RasterTimeGroup);
            PassDefinition diceDefinition = graph.GetPass(dicePass);
            _shading.BindTessellationResources(ref graph, ref diceDefinition, dispatchData, (policyTarget ? tessellation.ShadowDiceResources : tessellation.DiceResources)!, uniforms, phaseOne, 0u, PipelineSync.ComputeShading, policyTarget);
        }
    }

    internal void RecordTessellationHardwareRaster(ref RenderGraphEdit graph, in FrameResources frame, IReadOnlyList<ClusterRasterDispatch> stages, bool phaseOne, bool policyTarget, GraphRegionId region)
    {
        if (_geometry._visibilityMode != ClusterVisibilityMode.HardwareOnly)
            return;
        GraphTextureId visibilityTarget = policyTarget ? frame.PolicyVisibilityScratch : frame.VisBuffer;
        GraphTextureId depthTarget = policyTarget ? frame.PolicyDepthScratch : frame.Depth;
        uint targetLayers = policyTarget ? 1u : frame.ViewCount;
        if (!policyTarget && (visibilityTarget == default || depthTarget == default))
        {
            throw new InvalidOperationException("Hardware-only tessellation requires a primary or policy scratch target.");
        }

        GraphColorAttachmentViewId visibilityView = policyTarget ? default : graph.CreateColorAttachmentView(visibilityTarget, new TextureSubresourceRange(0, 1, 0, targetLayers, TextureAspects.Color), format: Format.R32UInt, dimension: TextureViewDimension.Texture2DArray, label: phaseOne ? "Cluster phase one tessellation hardware visibility view" : "Cluster phase two tessellation hardware visibility view");
        GraphDepthStencilViewId depthView = policyTarget ? default : graph.CreateDepthStencilView(depthTarget, new TextureSubresourceRange(0, 1, 0, targetLayers, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2DArray, label: phaseOne ? "Cluster phase one tessellation hardware depth view" : "Cluster phase two tessellation hardware depth view");
        int rasterWidth = policyTarget ? checked((int)_geometry._renderContext.RasterWidth) : frame.Width;
        int rasterHeight = policyTarget ? checked((int)_geometry._renderContext.RasterHeight) : frame.Height;
        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterDispatch dispatchData = stages[invocationIndex];
            if (dispatchData.Tessellation is not { } tessellation)
                continue;
            ClusterRasterPipeline pipeline = (policyTarget ? tessellation.ShadowHardware : tessellation.Hardware) ?? throw new InvalidOperationException("Hardware-only tessellation has no fixed-function raster pipeline.");
            uint bin = dispatchData.BinIndex;
            GraphBufferSlice uniforms = UploadUniform(ref graph, CreateTessellationUniforms(frame, bin, phaseOne, 0u), phaseOne ? "Cluster phase one tessellation hardware uniforms" : "Cluster phase two tessellation hardware uniforms");
            ulong offset = pipeline.UsesMesh ? checked((ulong)frame.RasterBinCount * 8u * ClusterIndirectLayout.DrawIndexedBytes + (ulong)bin * ClusterIndirectLayout.DispatchBytes) : checked((ulong)bin * 8u * ClusterIndirectLayout.DrawIndexedBytes);
            ClusterTessellationRasterPassData passData = new(_geometry._drawIndexedIndirectLayout ?? throw new InvalidOperationException("Tessellation indexed drawing is unavailable."), frame.TessellationHardwareDrawArgs, offset, frame.TessellationPatternIndices, frame.TessellationWorkPatches, rasterWidth, rasterHeight, pipeline.UsesMesh);
            GraphBufferAccessId indirectAccess = default;
            GraphPassId hardwarePass = AddRasterPass(ref graph, phaseOne ? $"Cluster phase one tessellation hardware bin {bin}" : $"Cluster phase two tessellation hardware bin {bin}", pipeline, passData, 0, (ref PassDefinition access, ref ClusterTessellationRasterPassData data) =>
            {
                if (!policyTarget)
                {
                    access.ColorAttachment(0, visibilityView, LoadType.Load, StoreType.Store, WriteCoverage.Partial, default);
                    access.DepthStencilAttachment(depthView, LoadType.Load, StoreType.Store, WriteCoverage.Partial, 1f, LoadType.Discard, StoreType.Discard, WriteCoverage.Complete, 0);
                }
                indirectAccess = ClusterTessellationRasterPassData.Declare(ref access, ref data);
            }, ClusterTessellationRasterPassData.Record,
                policyTarget ? new PassOptions(RasterWidth: checked((uint)rasterWidth), RasterHeight: checked((uint)rasterHeight)) : default,
                region: region);
            graph.SetPassTimeGroup(hardwarePass, ClusterGeometryPipeline.RasterTimeGroup);
            if (pipeline.UsesMesh)
                _geometry._rasterInputs!.MeshDraw = new ClusterGeometryPipeline.MeshRaster(hardwarePass, bin, indirectAccess, passData);
            PassDefinition hardwareDefinition = graph.GetPass(hardwarePass);
            _shading.BindTessellationResources(ref graph, ref hardwareDefinition, dispatchData, (policyTarget ? tessellation.ShadowHardwareResources : tessellation.HardwareResources)!, uniforms, phaseOne, 0u, PipelineSync.AllShading, policyTarget);
        }
    }

    internal void RecordSoftwareRaster(ref RenderGraphEdit graph, in FrameResources frame, IReadOnlyList<ClusterRasterDispatch> stages, GraphBufferId packedViews, bool phaseOne, bool policyTarget, GraphRegionId region)
    {
        if (_geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly)
            return;
        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterDispatch dispatchData = stages[invocationIndex];
            uint bin = dispatchData.BinIndex;
            GraphBufferSlice pushConstants = UploadUniform(ref graph, new ClusterSoftwareRasterUniforms { ScreenWidth = checked((uint)frame.Width), ScreenHeight = checked((uint)frame.Height), MaxBins = frame.RasterBinCount, DebugDump = _geometry._options.EnableFrameMetricsReadback ? 1u : 0u, CurrentBin = bin, EarlyDepthEnabled = dispatchData.UsesEarlyDepth ? 1u : 0u, MaxVisibleClusters = _geometry._options.MaxCandidates }, phaseOne ? "Cluster phase one software raster bin uniforms" : "Cluster phase two software raster bin uniforms");
            if (_geometry._rasterInputs is { } raster)
                raster.SoftwareUniforms = pushConstants;
            ulong indirectOffset = checked((ulong)bin * ClusterIndirectLayout.DispatchBytes);
            int bindingCount = 0;
            ClusterSoftwareRasterPassData passData = new(_geometry.RequireDispatchIndirectLayout(), frame.SoftwareDispatchArgs, indirectOffset);
            ClusterComputePipeline softwarePipeline = (policyTarget ? dispatchData.TargetStages?.Software : null) ?? dispatchData.Software ?? throw new InvalidOperationException("A recorded software-raster pass has no software pipeline.");
            // Both raster paths write the same exclusive visibility image.
            GraphPassId softwarePass = AddComputePass(ref graph, phaseOne ? "Cluster phase one software raster bin" : "Cluster phase two software raster bin", softwarePipeline, passData, bindingCount, ClusterSoftwareRasterPassData.Declare, ClusterSoftwareRasterPassData.Record, region: region);
            graph.SetPassTimeGroup(softwarePass, ClusterGeometryPipeline.RasterTimeGroup);
            PassDefinition softwareDefinition = graph.GetPass(softwarePass);
            _shading.BindSoftwareRasterResources(ref graph, ref softwareDefinition, dispatchData, pushConstants, policyTarget);
        }
    }

    internal GraphPassId RecordWinnerResolve(ref RenderGraphEdit graph, in FrameResources frame, bool phaseOne, GraphRegionId region)
    {
        if (_geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly)
            throw new InvalidOperationException("Hardware-only visibility does not use a winner resolve pass.");
        GraphColorAttachmentViewId visibilityView = graph.CreateColorAttachmentView(frame.VisBuffer, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Color), format: Format.R32UInt, dimension: TextureViewDimension.Texture2DArray, label: phaseOne ? "Cluster phase one visibility resolve view" : "Cluster phase two visibility resolve view");
        GraphDepthStencilViewId depthView = graph.CreateDepthStencilView(frame.Depth, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2DArray, label: phaseOne ? "Cluster phase one winner depth view" : "Cluster phase two winner depth view");
        ClusterRasterPipeline pipeline = _geometry._pipelines!.WinnerResolve ?? throw new InvalidOperationException("The selected visibility mode has no winner resolve pipeline.");
        GraphBufferSlice uniforms = UploadUniform(ref graph, new ClusterVisibilityWinnerUniforms { Width = checked((uint)frame.Width), Height = checked((uint)frame.Height), ViewCount = frame.ViewCount }, phaseOne ? "Cluster phase one visibility winner resolve uniforms" : "Cluster phase two visibility winner resolve uniforms");
        int bindingCount = 0;
        if (_geometry._visibilityMode == ClusterVisibilityMode.ImageAtomic64)
        {
            AddBinding(ref bindingCount, "Winner", GraphParameterResourceBinding.SampledTexture(CreateSampledTextureView(graph, frame.VisibilityWinnerImage, Format.R64UInt, null, TextureViewDimension.Texture2DArray), PipelineSync.PixelShading));
        }
        else
        {
            AddBinding(ref bindingCount, "Winner", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.VisibilityWinnerBuffer), PipelineSync.PixelShading));
        }

        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, uniforms), PipelineSync.PixelShading));
        if (!phaseOne)
        {
            ClusterHardwareRasterPassData indirect = new(_geometry.RequireDrawIndirectLayout(), frame.RasterWorkArguments, ClusterRasterArguments.VisibilityResolveOffset, frame.Width, frame.Height, UsesMesh: false);
            return AddRasterPass(ref graph, "Cluster phase two visibility winner resolve", pipeline, indirect, bindingCount, (ref PassDefinition access, ref ClusterHardwareRasterPassData data) =>
            {
                DeclareTargets(ref access);
                ClusterHardwareRasterPassData.Declare(ref access, ref data);
            }, ClusterHardwareRasterPassData.Record, region: region);
        }

        ClusterFullscreenParameters passData = new(frame.Width, frame.Height, 1);
        GraphPassId pass = AddRasterPass(ref graph, "Cluster phase one visibility winner resolve", pipeline, passData, bindingCount, (ref PassDefinition access, ref ClusterFullscreenParameters _) => DeclareTargets(ref access), ClusterFullscreenParameters.Record, region: region);
        GraphData.WinnerDispatches.Add(new ClusterLayerDispatch(pass, checked((uint)frame.Width), checked((uint)frame.Height)));
        return pass;
        void DeclareTargets(ref PassDefinition access)
        {
            LoadType load = phaseOne ? LoadType.Discard : LoadType.Load;
            WriteCoverage coverage = phaseOne ? WriteCoverage.Complete : WriteCoverage.Partial;
            access.ColorAttachment(0, visibilityView, load, StoreType.Store, coverage, default);
            access.DepthStencilAttachment(depthView, load, StoreType.Store, coverage, 1f, LoadType.Discard, StoreType.Discard, WriteCoverage.Complete, 0);
        }
    }

    internal void RecordHardwareRaster(ref RenderGraphEdit graph, in FrameResources frame, IReadOnlyList<ClusterRasterDispatch> stages, GraphBufferId packedViews, bool phaseOne, bool policyTarget, GraphRegionId region)
    {
        bool fixedFunctionVisibility = _geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly;
        GraphTextureId visibilityTarget = policyTarget ? frame.PolicyVisibilityScratch : frame.VisBuffer;
        GraphTextureId depthTarget = policyTarget ? frame.PolicyDepthScratch : frame.Depth;
        uint targetLayers = policyTarget ? 1u : frame.ViewCount;
        if (fixedFunctionVisibility && (visibilityTarget == default || depthTarget == default))
        {
            throw new InvalidOperationException("Hardware-only rasterization requires a primary or policy scratch target.");
        }

        GraphColorAttachmentViewId visView = fixedFunctionVisibility ? graph.CreateColorAttachmentView(visibilityTarget, new TextureSubresourceRange(0, 1, 0, targetLayers, TextureAspects.Color), format: Format.R32UInt, dimension: TextureViewDimension.Texture2DArray, label: phaseOne ? "Cluster phase one hardware visibility view" : "Cluster phase two hardware visibility view") : default;
        GraphDepthStencilViewId depthView = fixedFunctionVisibility ? graph.CreateDepthStencilView(depthTarget, new TextureSubresourceRange(0, 1, 0, targetLayers, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2DArray, label: phaseOne ? "Cluster phase one hardware depth view" : "Cluster phase two hardware depth view") : default;
        int rasterWidth = policyTarget ? checked((int)_geometry._renderContext.RasterWidth) : frame.Width;
        int rasterHeight = policyTarget ? checked((int)_geometry._renderContext.RasterHeight) : frame.Height;
        GraphBufferSlice drawUniforms = UploadUniform(ref graph, new ClusterDrawUniforms { ScreenWidth = checked((uint)frame.Width), ScreenHeight = checked((uint)frame.Height), RasterWidth = checked((uint)rasterWidth), RasterHeight = checked((uint)rasterHeight) }, phaseOne ? "Cluster phase one hardware draw uniforms" : "Cluster phase two hardware draw uniforms");
        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterDispatch dispatchData = stages[invocationIndex];
            uint bin = dispatchData.BinIndex;
            GraphBufferSlice dispatchUniforms = UploadUniform(ref graph, new ClusterDrawDispatchUniforms { DrawArgsByteOffset = checked(bin * ClusterIndirectLayout.HardwareRasterStride), EarlyDepthEnabled = dispatchData.UsesEarlyDepth ? 1u : 0u, CurrentBin = bin, MaxVisibleClusters = _geometry._options.MaxCandidates }, phaseOne ? "Cluster phase one hardware bin dispatch uniforms" : "Cluster phase two hardware bin dispatch uniforms");
            ulong offset = checked((ulong)bin * ClusterIndirectLayout.HardwareRasterStride);
            ClusterRasterPipeline? targetPipeline = policyTarget ? dispatchData.TargetStages?.Hardware : null;
            ClusterRasterPipeline pipeline = targetPipeline ?? dispatchData.Hardware;
            bool attachVisibility = fixedFunctionVisibility && targetPipeline is null;
            int bindingCount = 0;
            ClusterHardwareRasterPassData passData = new(_geometry.RequireDrawIndirectLayout(), frame.HardwareIndirectArgs, offset, rasterWidth, rasterHeight, pipeline.UsesMesh);
            GraphPassId hardwarePass = AddRasterPass(ref graph, phaseOne ? "Cluster phase one hardware visibility bin" : "Cluster phase two hardware visibility bin", pipeline, passData, bindingCount, (ref PassDefinition access, ref ClusterHardwareRasterPassData data) =>
            {
                if (attachVisibility)
                {
                    access.ColorAttachment(0, visView, LoadType.Load, StoreType.Store, WriteCoverage.Partial, default);
                    access.DepthStencilAttachment(depthView, LoadType.Load, StoreType.Store, WriteCoverage.Partial, 1f, LoadType.Discard, StoreType.Discard, WriteCoverage.Complete, 0);
                }

                ClusterHardwareRasterPassData.Declare(ref access, ref data);
            }, ClusterHardwareRasterPassData.Record, attachVisibility ? default : new PassOptions(RasterWidth: checked((uint)rasterWidth), RasterHeight: checked((uint)rasterHeight)), region: region);
            graph.SetPassTimeGroup(hardwarePass, ClusterGeometryPipeline.RasterTimeGroup);
            PassDefinition hardwareDefinition = graph.GetPass(hardwarePass);
            _shading.BindHardwareRasterResources(ref graph, ref hardwareDefinition, dispatchData, drawUniforms, dispatchUniforms, policyTarget);
        }
    }

    internal GraphPassId RecordHiZ(ref RenderGraphEdit graph, in FrameResources frame, bool phaseOne, GraphRegionId region)
    {
        int mipCount = ClusterCullingHistory.CalculateMipCount(frame.Width, frame.Height);
        GraphAccessMode outputAccess = phaseOne ? GraphAccessMode.Write : GraphAccessMode.ReadWrite;
        WriteCoverage outputCoverage = phaseOne ? WriteCoverage.Complete : WriteCoverage.Partial;
        int bindingCount = 0;
        AddBinding(ref bindingCount, "DepthTexture", GraphParameterResourceBinding.SampledTexture(CreateSampledTextureView(graph, frame.Depth, Format.D32Float, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth), TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading));
        for (int mip = 0; mip < 5; mip++)
            AddBinding(ref bindingCount, "Mip" + mip, GraphParameterResourceBinding.StorageTexture(CreateStorageTextureView(graph, frame.CurrentHiZ, Format.R32Float, Mip(Math.Min(mip, mipCount - 1), frame.ViewCount), TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, outputAccess, outputCoverage));
        uint firstWidth = checked((uint)MipExtent(frame.Width, 4));
        uint firstHeight = checked((uint)MipExtent(frame.Height, 4));
        GraphPassId firstPass = RecordHiZDispatch(ref graph, frame, phaseOne ? "Build Cluster phase one HiZ mips 0-4" : "Build Cluster final HiZ mips 0-4", _geometry._pipelines!.HiZFirst, bindingCount, firstWidth, firstHeight, phaseOne, ClusterRasterArguments.HiZFirstOffset, region);
        GraphPassId previousPass = firstPass;
        int sourceMip = Math.Min(4, mipCount - 1);
        while (sourceMip + 1 < mipCount)
        {
            // The tail shader keeps at most 64 by 64 first-level values in
            // group memory. Larger inputs retain parallel two-level reduction.
            bool tail = MipExtent(frame.Width, sourceMip + 1) <= 64 && MipExtent(frame.Height, sourceMip + 1) <= 64;
            int outputCount = tail ? 7 : 2;
            bindingCount = 0;
            AddBinding(ref bindingCount, "Source", GraphParameterResourceBinding.StorageTexture(CreateStorageTextureView(graph, frame.CurrentHiZ, Format.R32Float, Mip(sourceMip, frame.ViewCount), TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, GraphAccessMode.Read));
            for (int output = 0; output < outputCount; output++)
                AddBinding(ref bindingCount, tail ? "Mip" + output : output == 0 ? "Middle" : "Destination", GraphParameterResourceBinding.StorageTexture(CreateStorageTextureView(graph, frame.CurrentHiZ, Format.R32Float, Mip(Math.Min(sourceMip + 1 + output, mipCount - 1), frame.ViewCount), TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, outputAccess, outputCoverage));
            int destinationMip = Math.Min(sourceMip + outputCount, mipCount - 1);
            uint dispatchWidth = tail ? 1u : Groups(MipExtent(frame.Width, destinationMip), 8);
            uint dispatchHeight = tail ? 1u : Groups(MipExtent(frame.Height, destinationMip), 8);
            GraphPassId pass = RecordHiZDispatch(ref graph, frame, $"Build Cluster {(phaseOne ? "phase one" : "final")} HiZ {(tail ? "tail" : "mip pair")}", tail ? _geometry._pipelines.HiZDownsample : _geometry._pipelines.HiZDownsampleTwo, bindingCount, dispatchWidth, dispatchHeight, phaseOne, tail ? ClusterRasterArguments.HiZTailOffset : ClusterRasterArguments.HiZPairOffset(destinationMip), region);
            previousPass = pass;
            sourceMip = destinationMip;
        }

        return previousPass;
    }

    internal GraphPassId RecordHiZDispatch(ref RenderGraphEdit graph, in FrameResources frame, string label, ClusterComputePipeline pipeline, int bindingCount, uint width, uint height, bool phaseOne, ulong argumentOffset, GraphRegionId region)
    {
        if (phaseOne)
        {
            GraphPassId pass = AddComputePass(ref graph, label, pipeline, new ClusterDispatchParameters(new DispatchArguments(width, height, 1)), bindingCount, ClusterDispatchParameters.Record, region: region);
            GraphData.LayerDispatches.Add(new ClusterLayerDispatch(pass, width, height));
            return pass;
        }

        ClusterIndirectDispatchParameters indirect = new(_geometry.RequireDispatchIndirectLayout(), frame.RasterWorkArguments, argumentOffset);
        return AddComputePass(ref graph, label, pipeline, indirect, bindingCount, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
    }

    internal GraphPassId RecordShadeCount(ref RenderGraphEdit graph, in FrameResources frame, GraphBufferSlice binUniforms)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, "InstanceProperties", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "InstanceData", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "BinCounts", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "VisBuffer", GraphParameterResourceBinding.SampledTexture(CreateSampledTextureView(graph, frame.VisBuffer, Format.R32UInt, null, TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "VisibleClusters", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, "ShadeSlots", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.ShadeBinIndices, sizeof(uint)), PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, "PixelCoordinates", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.PixelCoordinates, 2 * sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedRanges));
        ClusterDispatchParameters count = new(new DispatchArguments(Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphPassId countPass = AddComputePass(ref graph, "Count Cluster shade bins", _geometry._pipelines!.ShadeBinCount, count, bindingCount, ClusterDispatchParameters.Record, asyncCompute: true, region: GraphData.ShadeCountRegion);
        GraphData.LayerDispatches.Add(new ClusterLayerDispatch(countPass, Groups(frame.Width, 8), Groups(frame.Height, 8)));
        return countPass;
    }

    internal void RecordShade(ref RenderGraphEdit graph, in FrameResources frame, ClusterShadeStageSet shadeStages)
    {
        GraphData.ShadeBinning = graph.AddRegion("Cluster shade binning", _geometry._regions.PrimaryGeometry);
        GraphData.ShadeGroups = new GraphRegionMap<uint, ClusterShadingPipeline.ShadeGroup>(graph.AddRegion("Cluster shade materials", _geometry._regions.Shading), ClusterShadingPipeline.ShadeGroupComparer.Instance);
        GraphRegionId resolveRegion = graph.AddRegion("Cluster background resolve", _geometry._regions.Shading);
        GraphBufferSlice binUniforms = UploadUniform(ref graph, new ClusterShadeBinUniforms { ScreenWidth = checked((uint)frame.Width), ScreenHeight = checked((uint)frame.Height), ShadeBinCount = frame.ShadeBinCount, MaterialAddressCapacity = frame.MaterialAddressCapacity, BinItemCapacity = ClusterShadingPipeline.CalculateShadeBinItemCapacity(frame), ViewCount = frame.ViewCount }, "Cluster shade bin uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, "BinCounts", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ScatterCounts", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "ReserveCounters", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeReserveCounters, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        ClusterDispatchParameters clear = new(new DispatchArguments(Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        GraphPassId clearPass = AddComputePass(ref graph, "Clear Cluster shade bins", _geometry._pipelines!.ShadeBinClearPrepare, clear, bindingCount, ClusterDispatchParameters.Record, asyncCompute: true, region: GraphData.ShadeBinning);
        GraphData.ShadeCountRegion = graph.AddRegion("Cluster shade count", GraphData.ShadeBinning);
        GraphData.ShadeCountPass = RecordShadeCount(ref graph, frame, binUniforms);
        bindingCount = 0;
        AddBinding(ref bindingCount, "BinCounts", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Read, WriteCoverage.Partial));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "BinOffsets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeBinOffsets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "IndirectArgs", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeIndirectArgs), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "ScatterCounts", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, "ReserveCounters", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeReserveCounters, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        ClusterDispatchParameters reserve = new(new DispatchArguments(Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        GraphPassId reservePass = AddComputePass(ref graph, "Reserve Cluster shade bins", _geometry._pipelines.ShadeBinReserve, reserve, bindingCount, ClusterDispatchParameters.Record, asyncCompute: false, region: GraphData.ShadeBinning);
        GraphData.ShadeBinUniforms = binUniforms;
        GraphData.ShadeBinDispatches = [new(clearPass, 1, 1), new(reservePass, 1, 1)];
        bindingCount = 0;
        AddBinding(ref bindingCount, "InstanceProperties", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "InstanceData", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "VisBuffer", GraphParameterResourceBinding.SampledTexture(CreateSampledTextureView(graph, frame.VisBuffer, Format.R32UInt, null, TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "BinOffsets", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeBinOffsets, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Read, WriteCoverage.Partial));
        AddBinding(ref bindingCount, "VisibleClusters", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, "ShadeSlots", GraphParameterResourceBinding.ReadOnlyBuffer(CreateReadOnlyBufferView(graph, frame.ShadeBinIndices, sizeof(uint)), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, "ScatterCounts", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, "PixelCoordinates", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.PixelCoordinates, 2 * sizeof(uint)), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedRanges));
        ClusterDispatchParameters scatter = new(new DispatchArguments(Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphData.ShadeScatter = graph.AddRegion("Cluster shade scatter", GraphData.ShadeBinning);
        GraphPassId scatterPass = AddComputePass(ref graph, "Scatter Cluster shade bins", _geometry._pipelines.ShadeBinScatter, scatter, bindingCount, ClusterDispatchParameters.Record, asyncCompute: true, region: GraphData.ShadeScatter);
        GraphData.LayerDispatches.Add(new ClusterLayerDispatch(scatterPass, Groups(frame.Width, 8), Groups(frame.Height, 8)));
        {
            // Material bins define geometry pixels; this final write fills every background pixel.
            // Together they define the complete color and motion outputs while retaining the bin writes.
            GraphBufferSlice resolveUniforms = UploadUniform(ref graph, new ClusterResolveUniforms { ScreenWidth = checked((uint)frame.Width), ScreenHeight = checked((uint)frame.Height) }, "Cluster resolve uniforms");
            bindingCount = 0;
            AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, resolveUniforms), PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, "VisBuffer", GraphParameterResourceBinding.SampledTexture(CreateSampledTextureView(graph, frame.VisBuffer, Format.R32UInt, null, TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, "OutputColor", GraphParameterResourceBinding.StorageTexture(CreateStorageTextureView(graph, frame.SceneColor, Format.R16G16B16A16Float, null, TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined));
            AddBinding(ref bindingCount, "OutputMotionVectors", GraphParameterResourceBinding.StorageTexture(CreateStorageTextureView(graph, frame.MotionVectors, Format.R16G16Float, null, TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined));
            ClusterDispatchParameters resolve = new(new DispatchArguments(Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
            GraphPassId resolvePass = AddComputePass(ref graph, "Resolve Cluster visibility background", _geometry._pipelines.Resolve, resolve, bindingCount, ClusterDispatchParameters.Record, region: resolveRegion);
            GraphData.LayerDispatches.Add(new ClusterLayerDispatch(resolvePass, Groups(frame.Width, 8), Groups(frame.Height, 8)));
        }

        GraphData.ShadeGroups = _shading.UpdateShadeGroups(ref graph, shadeStages);
    }

    internal GraphPassId AddRasterPass<TState>(ref RenderGraphEdit graph, string name, ClusterRasterPipeline pipeline, in TState state, int bindingCount, PassDeclaration<TState> declaration, RasterFrameCallback<TState> callback, PassOptions options = default, GraphRegionId region = default)
    {
        try
        {
            GraphPassId pass = graph.AddRasterFramePass(name, PassQueueSelection.AnyOfType(QueueType.Graphics), state, options with { RepeatCommands = true }, pipeline.Pipeline, pipeline.ParameterLayout, default, declaration, callback, region: region == default ? _geometry._regions.PrimaryGeometry : region);
            BindPassResources(ref graph, pass, pipeline, bindingCount, PipelineSync.AllShading);
            return pass;
        }
        finally
        {
            _passResourceBinders.Clear();
        }
    }

    internal GraphPassId AddRasterPass<TState>(ref RenderGraphEdit graph, string name, ClusterRasterPipeline pipeline, in TState state, int bindingCount, RasterFrameCallback<TState> callback, PassOptions options = default, GraphRegionId region = default) => AddRasterPass(ref graph, name, pipeline, state, bindingCount, DeclareNoAdditionalAccess<TState>, callback, options, region);
    internal GraphPassId AddComputePass<TState>(ref RenderGraphEdit graph, string name, ClusterComputePipeline pipeline, in TState state, int bindingCount, PassDeclaration<TState> declaration, ComputeFrameCallback<TState> callback, bool asyncCompute = false, GraphRegionId region = default)
    {
        try
        {
            GraphPassId pass = graph.AddComputeFramePass(name, // compute queue here adds transfers even when the dependency chain is serial.
 _geometry._options.EnableAsyncCompute && asyncCompute ? PassQueueSelection.AnyCompatible() : PassQueueSelection.AnyOfType(QueueType.Graphics), state, new PassOptions(RepeatCommands: true), pipeline.Pipeline, pipeline.ParameterLayout, default, declaration, callback, region: region == default ? _geometry._regions.PrimaryGeometry : region);
            BindPassResources(ref graph, pass, pipeline, bindingCount, PipelineSync.ComputeShading);
            return pass;
        }
        finally
        {
            _passResourceBinders.Clear();
        }
    }

    internal GraphPassId AddComputePass<TState>(ref RenderGraphEdit graph, string name, ClusterComputePipeline pipeline, in TState state, int bindingCount, ComputeFrameCallback<TState> callback, bool asyncCompute = false, GraphRegionId region = default) => AddComputePass(ref graph, name, pipeline, state, bindingCount, DeclareNoAdditionalAccess<TState>, callback, asyncCompute, region);
    private void BindPassResources(ref RenderGraphEdit graph, GraphPassId id,
        ClusterPipeline pipeline, int count, PipelineSync sync)
    {
        if (count == 0) return;
        PassDefinition pass = graph.GetPass(id);
        foreach (ref readonly ShaderParameterResource resource in pipeline.ParameterResources.Resources)
            if (!ClusterShadingPipeline.BindInactiveStageResource(ref pass, resource))
                _passResourceBinders[resource.Name](ref graph, ref pass, sync);
    }

    internal void AddBinding(ref int count, string name, in GraphParameterResourceBinding binding)
    {
        GraphParameterResourceBinding value = binding;
        AddBinding(ref count, name, (ref RenderGraphEdit _, ref PassDefinition pass, PipelineSync _) => pass.Bind(value));
    }

    internal void AddBinding(ref int count, string name, GraphResourceBinder binder)
    {
        _passResourceBinders.Add(name, binder);
        count++;
    }

    internal void AddPageHeapBinding(ref int count, GraphBufferSrvId view)
    {
        AddBinding(ref count, "PageHeap", GraphParameterResourceBinding.ReadOnlyBufferDescriptor(view));
    }

    internal static void DeclareNoAdditionalAccess<TState>(ref PassDefinition access, ref TState state)
    {
    }

    internal static GraphTextureSrvId CreateSampledTextureView(RenderGraphEdit graph, GraphTextureId texture, Format? format, TextureSubresourceRange? range, TextureViewDimension? dimension = null) => graph.CreateTextureSrv(texture, range, format, dimension);
    internal static GraphTextureUavId CreateStorageTextureView(RenderGraphEdit graph, GraphTextureId texture, Format format, TextureSubresourceRange? range, TextureViewDimension? dimension = null) => graph.CreateTextureUav(texture, range, format, dimension);
    internal static GraphTextureSrvId CreateSampledTextureView(RenderGraphEdit graph, GraphTextureInput texture, Format? format, TextureSubresourceRange? range, TextureViewDimension? dimension = null) => range is { } value ? graph.CreateTextureSrv(texture, value, format!.Value, dimension!.Value) : graph.CreateTextureSrv(texture, format!.Value, dimension!.Value);
    internal static GraphTextureUavId CreateStorageTextureView(RenderGraphEdit graph, GraphTextureInput texture, Format format, TextureSubresourceRange? range, TextureViewDimension? dimension = null) => graph.CreateTextureUav(texture, range!.Value, format, dimension!.Value);
    internal static GraphBufferCbvId CreateConstantBufferView(RenderGraphEdit graph, GraphBufferSlice input) => graph.CreateBufferCbv(input.Buffer, input.Range);
    internal static GraphBufferCbvId CreateConstantBufferView(RenderGraphEdit graph, GraphBufferInput buffer, BufferRange? range = null) => graph.CreateBufferCbv(buffer, range!.Value);
    internal static GraphBufferCbvId CreateConstantBufferView(RenderGraphEdit graph, GraphBufferId buffer, BufferRange? range = null) => graph.CreateBufferCbv(buffer, range);
    internal static GraphBufferSrvId CreateReadOnlyBufferView(RenderGraphEdit graph, GraphBufferId buffer, uint stride = 0, BufferRange? range = null) => graph.CreateBufferSrv(buffer, range, structureStride: stride);
    internal static GraphBufferUavId CreateStorageBufferView(RenderGraphEdit graph, GraphBufferId buffer, uint stride = 0, BufferRange? range = null) => graph.CreateBufferUav(buffer, range, structureStride: stride);
    internal static TextureSubresourceRange Mip(int mip, uint viewCount) => new(checked((uint)mip), 1, 0, viewCount, TextureAspects.Color);
    internal static int MipExtent(int extent, int mip) => Math.Max(1, extent >> mip);
    internal static uint Groups(int extent, int groupSize) => checked((uint)((extent + groupSize - 1) / groupSize));
    internal static void Read(ref PassDefinition access, GraphBufferId buffer, ulong offset, ulong size) => _ = access.Read(buffer, new BufferRange(offset, size), PipelineSync.Copy, ResourceAccess.CopySource);
    internal const int CandidateCountReadbackOffset = 0;
    internal const int CandidateArgsReadbackOffset = 4;
    internal const int DrawArgsReadbackOffset = 16;
    internal const int Phase2CandidateCountReadbackOffset = 32;
    internal const int Phase2CandidateArgsReadbackOffset = 36;
    internal const int Phase2DrawArgsReadbackOffset = 48;
    internal const int RasterReserveReadbackOffset = 64;
    internal const int ShadeReserveReadbackOffset = 72;
    internal const int SoftwareDebugReadbackOffset = 76;
    internal const int ShadeBinCountReadbackOffset = 80;
    internal const int PhaseOneTessellationCountersReadbackOffset = 84;
    internal const int PhaseTwoTessellationCountersReadbackOffset = 116;
    internal const int WorkMetricsReadbackOffset = 160;
    internal const int WorkMetricsByteSize = 96;
    internal const int MetricsFrameIndexOffset = 152;
    internal const int VisibilityProbeReadbackOffset = 512;
    internal const int VisibilityProbePixelCount = 64;
    internal const int VisibilityProbeRowPitch = VisibilityProbePixelCount * sizeof(uint);
    internal const int FrameMetricsReadbackByteSize = VisibilityProbeReadbackOffset + VisibilityProbeRowPitch;
    internal const uint DefaultRasterBatchesPerCluster = 9;
    internal const uint ClusterVertexCapacity = 64;
    internal const int RasterBinStride = 36;
    internal const int VisibleClusterStride = 16;
    internal const int CandidateStride = 12;
    internal const int BinnedClusterStride = 4;
    internal const int TraversalNodeStride = 16;
    internal const int TraversalArgumentStride = 32;
    internal const int TessellationWorkPatchStride = 16;
    internal const int TessellationFinalPatchStride = 20;
    internal const int TessellationSplitLevelCount = 6;
    internal FrameResources CreateFrameResources(ref RenderGraphEdit graph, in ClusterPipelineOutputDescription output, ClusterMaterialAssignmentSet assignments, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        uint rasterBinCount = rasterStages.BinCount;
        uint tessellationBinCount = checked(rasterBinCount * 8u);
        uint shadeBinCount = shadeStages.BinCount;
        ulong candidates = _geometry._options.MaxCandidates;
        uint batchCapacity = _geometry._options.RasterBatchCapacity ?? checked((uint)candidates * Math.Max(DefaultRasterBatchesPerCluster, _geometry._resources.MaximumRasterBatchesPerCluster));
        ulong binnedCapacity = batchCapacity;
        int width = output.Width;
        int height = output.Height;
        uint viewCount = output.ViewCount;
        int pageFaultCapacity = _geometry._resources.PageFaultCapacity;
        ClusterResidencyBinding residency = _geometry._resources.Residency.GetBinding();
        var geometryBuffers = _geometry._resources.Residency.GeometryBuffers;
        RenderInstanceData instanceResources = _geometry._resources.InstanceResources;
        ulong instanceMetadataStride = checked((ulong)instanceResources.BatchMetadataStrideBytes);
        int mipCount = ClusterCullingHistory.CalculateMipCount(width, height);
        var historyDescription = new TextureDesc(TextureDimension.Texture2D, checked((uint)width), checked((uint)height), 1, checked((uint)mipCount), viewCount, 1, Format.R32Float, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource, label: "Cluster HiZ history");
        var frame = new FrameResources()
        {
            RasterBatchCapacity = batchCapacity,
            Width = width,
            Height = height,
            ViewCount = viewCount,
            RasterBinCount = rasterBinCount,
            ShadeBinCount = shadeBinCount,
            MaterialAddressCapacity = assignments.MaterialAddressCapacity,
            PageFaultCapacity = pageFaultCapacity,
            PagePriorityCapacity = _geometry._resources.GeometryPageCount,
            PageHeap = geometryBuffers.PageHeap,
            Bvh = geometryBuffers.Bvh,
            InstanceData = instanceResources.PropertyData,
            InstanceProperties = graph.CreateBufferInput(BufferUsages.Constant | BufferUsages.CopySource),
            InstancePropertiesRange = new BufferRange(0, instanceMetadataStride),
            HiZ = [graph.CreatePersistentTexture(historyDescription), graph.CreatePersistentTexture(historyDescription)],
            PreviousHiZ = graph.CreateTextureInput(),
            CurrentHiZ = graph.CreateTextureInput(),
            RasterBinIndices = UploadBytes(ref graph, rasterStages.BinIndices, BufferUsages.ShaderRead, "Cluster raster stage slots"),
            MaterialBounds = UploadBytes(ref graph, rasterStages.MaterialBounds, BufferUsages.ShaderRead, "Cluster material sequence bounds"),
            ReadOffsetZero = UploadBytes(ref graph, stackalloc byte[16], BufferUsages.ShaderRead, "Cluster zero read offset")
        };
        frame.ShadeBinIndices = UploadBytes(ref graph, shadeStages.BinIndices, BufferUsages.ShaderRead, "Cluster shade stage slots");
        frame.DeformCache = frame.AddBuffer(ref graph, _geometry._options.MaxDeformCacheBytes, BufferUsages.None, "Cluster DeformCache");
        frame.DeformRegions = frame.AddBuffer(ref graph, candidates * 8, BufferUsages.None, "Cluster DeformRegions");
        frame.DeformRequests = frame.AddBuffer(ref graph, candidates * 8, BufferUsages.None, "Cluster DeformRequests");
        frame.DeformPrefixOffsets = frame.AddBuffer(ref graph, candidates * 4, BufferUsages.None, "Cluster DeformPrefixOffsets");
        frame.DeformPrefixGroups = frame.AddBuffer(ref graph, ((candidates + 255) / 256) * 8, BufferUsages.None, "Cluster DeformPrefixGroups");
        frame.DeformAllocation = frame.AddBuffer(ref graph, 4, BufferUsages.None, "Cluster DeformAllocation");
        frame.DeformArguments = frame.AddBuffer(ref graph, 24, BufferUsages.Indirect, "Cluster DeformArguments");
        if (_geometry._options.EnableFrameMetricsReadback)
        {
            frame.FrameMetricsBuffer = CreateBufferData(ref graph, ClusterBytes.Zero(FrameMetricsReadbackByteSize), BufferUsages.CopySource, "Cluster frame metric values");
        }

        GraphData.PageUpload = geometryBuffers.PageUpload;
        GraphData.BvhUpload = geometryBuffers.BvhUpload;
        frame.WorkStatus = frame.AddBuffer(ref graph, checked((ulong)(_geometry._options.EnableFrameMetricsReadback ? WorkMetricsByteSize : sizeof(uint))), BufferUsages.CopySource, "Cluster work overflow status");
        frame.CandidateArgs = frame.AddBuffer(ref graph, 16, BufferUsages.Indirect | BufferUsages.CopySource, "Cluster candidate dispatch");
        frame.NeutralPolicyValue = UploadWords(ref graph, [0xFFFFFFFFu, 0xFFFFFFFFu, 0u, 0u], BufferUsages.ShaderRead, "Cluster neutral policy value");
        frame.NeutralPolicyCounter = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.None, "Cluster neutral policy counter");
        frame.NeutralPolicyView = UploadBytes(ref graph, // receiver-mask (8 B) and uint views. Keep the byte size aligned to
 // every structured interpretation.
        stackalloc byte[checked((int)RenderRasterContext.TargetViewSizeInBytes)], BufferUsages.ShaderRead, "Cluster neutral target view");
        frame.Work = _geometry.CreateCullWork(ref graph, candidates);
        frame.TraversalNodes0 = frame.Work.Nodes;
        frame.TraversalArguments0 = frame.Work.Arguments;
        frame.CandidateArgsInitialization = UploadWords(ref graph, [0u, 1u, 1u, 0u], BufferUsages.CopySource, "Cluster candidate dispatch initialization");
        frame.CandidateClusters = frame.Work.Clusters;
        frame.CandidateCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster candidate count");
        frame.DrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster phase-one counts");
        frame.PageFaults = frame.AddBuffer(ref graph, checked(3u * sizeof(uint) + (ulong)pageFaultCapacity * 24u + (ulong)_geometry._resources.GeometryPageCount * sizeof(uint)), BufferUsages.CopySource, "Cluster page faults");
        frame.Phase2CandidateArgs = frame.AddBuffer(ref graph, 16, BufferUsages.Indirect | BufferUsages.CopySource, "Cluster phase-two dispatch");
        frame.Phase2CandidateClusters = frame.AddBuffer(ref graph, checked(candidates * CandidateStride), BufferUsages.None, "Cluster phase-two candidates");
        frame.Phase2CandidateCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster phase-two count");
        frame.Phase2DrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster phase-two counts");
        if (_geometry._renderContext.IsValid)
        {
            frame.ContinuationDrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster continuation counts");
            frame.ContinuationPostDrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster continuation post counts");
        }

        frame.VisibleClusters = frame.AddBuffer(ref graph, checked(candidates * VisibleClusterStride), BufferUsages.None, "Cluster visible clusters");
        if (_geometry._renderContext.IsValid)
        {
            frame.TargetVisibleClusters = frame.AddBuffer(ref graph, checked(candidates * VisibleClusterStride), BufferUsages.None, "Cluster target visible clusters");
            // The target main pass appends once. Neither primary nor target scratch resets
            // may overwrite this list before the target post-HiZ pass consumes it.
            frame.TargetDeferredWork = frame.AddBuffer(ref graph, checked(candidates * CandidateStride), BufferUsages.None, "Cluster target deferred work");
            frame.TargetDeferredCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.None, "Cluster target deferred count");
            frame.TargetDeferredArgs = frame.AddBuffer(ref graph, 16, BufferUsages.Indirect, "Cluster target deferred dispatch");
        }

        frame.RasterBinMeta = frame.AddBuffer(ref graph, checked((ulong)rasterBinCount * RasterBinStride), BufferUsages.None, "Cluster raster bins");
        frame.BinnedClusters = frame.AddBuffer(ref graph, checked(binnedCapacity * BinnedClusterStride), BufferUsages.None, "Cluster raster bin indices");
        frame.RasterBatchOffsets = frame.AddBuffer(ref graph, ((ulong)rasterBinCount + 1u) * sizeof(uint), BufferUsages.None, "Cluster raster batch offsets");
        frame.RasterSoftwareOffsets = frame.AddBuffer(ref graph, ((ulong)rasterBinCount + 1u) * sizeof(uint), BufferUsages.None, "Cluster raster software offsets");
        frame.RasterSpillClusters = frame.AddBuffer(ref graph, candidates * sizeof(uint), BufferUsages.None, "Cluster spilled visible clusters");
        frame.RasterSpillArguments = frame.AddBuffer(ref graph, 32u, BufferUsages.Indirect, "Cluster spilled raster arguments");
        frame.RasterBatchDemand = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster raster batch demand");
        frame.DepthBucketsEnabled = _geometry._options.EnableDepthBuckets && rasterStages.Dispatches.Any(static stage => stage.PixelVisibility);
        frame.DepthBuckets = frame.AddBuffer(ref graph, frame.DepthBucketsEnabled ? checked((ulong)rasterBinCount * 512u * sizeof(uint)) : sizeof(uint), BufferUsages.None, "Cluster depth buckets");
        frame.HardwareIndirectArgs = frame.AddBuffer(ref graph, checked((ulong)rasterBinCount * ClusterIndirectLayout.HardwareRasterStride), BufferUsages.Indirect, "Cluster hardware indirect arguments");
        frame.RasterWorkArguments = frame.AddBuffer(ref graph, ClusterRasterArguments.HiZPairOffset(mipCount), BufferUsages.Indirect, "Cluster raster and screen arguments");
        frame.SoftwareDispatchArgs = frame.AddBuffer(ref graph, checked((ulong)rasterBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster software raster dispatches");
        frame.RasterReserveCounters = frame.AddBuffer(ref graph, 4 * sizeof(uint), BufferUsages.CopySource, "Cluster raster reserve counters");
        frame.TessellationCounters = frame.AddBuffer(ref graph, 8 * sizeof(uint), BufferUsages.CopySource, "Cluster tessellation counters");
        frame.PhaseOneTessellationCounters = frame.AddBuffer(ref graph, 8 * sizeof(uint), BufferUsages.CopySource, "Cluster phase one tessellation counters");
        frame.TessellationWorkPatches = frame.AddBuffer(ref graph, checked((ulong)_geometry._options.MaxCandidates * TessellationWorkPatchStride), BufferUsages.Vertex, "Cluster tessellation work patches");
        frame.TessellationWorkPatchesAlternate = frame.AddBuffer(ref graph, checked((ulong)_geometry._options.MaxCandidates * TessellationWorkPatchStride), BufferUsages.None, "Cluster tessellation alternate work patches");
        frame.TessellationFinalPatches = frame.AddBuffer(ref graph, checked((ulong)_geometry._options.MaxCandidates * TessellationFinalPatchStride), BufferUsages.None, "Cluster tessellation final patches");
        frame.TessellationLevelCounts = frame.AddBuffer(ref graph, TessellationSplitLevelCount * sizeof(uint), BufferUsages.None, "Cluster tessellation level counts");
        frame.TessellationLevelDispatchArgs = new GraphBufferId[TessellationSplitLevelCount + 1];
        for (int level = 0; level <= TessellationSplitLevelCount; level++)
        {
            frame.TessellationLevelDispatchArgs[level] = frame.AddBuffer(ref graph, ClusterIndirectLayout.DispatchBytes, BufferUsages.Indirect, $"Cluster tessellation level {level} dispatch");
        }

        frame.TessellationSourceDispatchArgs = frame.AddBuffer(ref graph, ClusterIndirectLayout.DispatchBytes, BufferUsages.Indirect, "Cluster tessellation source dispatch");
        frame.TessellationSeedDispatchArgs = frame.AddBuffer(ref graph, checked((ulong)rasterBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster tessellation seed dispatches");
        frame.TessellationBinCounts = frame.AddBuffer(ref graph, checked((ulong)tessellationBinCount * sizeof(uint)), BufferUsages.None, "Cluster tessellation bin counts");
        frame.TessellationBinOffsets = frame.AddBuffer(ref graph, checked((ulong)tessellationBinCount * sizeof(uint)), BufferUsages.None, "Cluster tessellation bin offsets");
        frame.TessellationBinScatterCounts = frame.AddBuffer(ref graph, checked((ulong)tessellationBinCount * sizeof(uint)), BufferUsages.None, "Cluster tessellation bin scatter counts");
        frame.TessellationBinReserveCounter = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.None, "Cluster tessellation bin reserve counter");
        frame.TessellationBinDispatchArgs = frame.AddBuffer(ref graph, checked((ulong)tessellationBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster tessellation split or dice dispatches");
        frame.TessellationHardwareDrawArgs = frame.AddBuffer(ref graph, checked((ulong)tessellationBinCount * ClusterIndirectLayout.DrawIndexedBytes + (ulong)rasterBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster tessellation hardware draw arguments");
        if (_geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly && !ClusterRasterPipeline.SupportsMesh(_geometry._backend, _geometry._device))
            frame.TessellationPatternIndices = UploadStructs<ushort>(ref graph, CreateTessellationPatternIndices(), BufferUsages.Index | BufferUsages.ShaderRead, "Cluster tessellation pattern indices");
        ulong softwareDebugBytes = _geometry._visibilityMode != ClusterVisibilityMode.HardwareOnly && _geometry._options.EnableFrameMetricsReadback ? 4ul + 8192ul * 24ul : (ulong)sizeof(uint);
        frame.SoftwareDebug = frame.AddBuffer(ref graph, softwareDebugBytes, BufferUsages.CopySource, "Cluster software raster debug output");
        frame.ShadeBinCounts = frame.AddBuffer(ref graph, checked((ulong)shadeBinCount * 2ul * sizeof(uint)), _geometry._options.EnableFrameMetricsReadback ? BufferUsages.CopySource : BufferUsages.None, "Cluster shade bin counts");
        frame.ShadeBinOffsets = frame.AddBuffer(ref graph, checked((ulong)shadeBinCount * 2ul * sizeof(uint)), BufferUsages.None, "Cluster shade bin offsets");
        frame.ShadeIndirectArgs = frame.AddBuffer(ref graph, checked((ulong)shadeBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster shade dispatches");
        frame.ShadeScatterCounts = frame.AddBuffer(ref graph, checked((ulong)shadeBinCount * 2ul * sizeof(uint)), BufferUsages.None, "Cluster shade scatter counts");
        frame.ShadeReserveCounters = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster shade reserve counter");
        frame.PixelCoordinates = frame.AddBuffer(ref graph, checked((ulong)width * (ulong)height * viewCount * 2ul * sizeof(uint)), BufferUsages.None, "Cluster shade tiles and quads");
        frame.VisBuffer = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, checked((uint)width), checked((uint)height), 1, 1, viewCount, 1, Format.R32UInt, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource, label: "Cluster visibility buffer"));
        if (_geometry._visibilityMode == ClusterVisibilityMode.ImageAtomic64)
        {
            frame.VisibilityWinnerImage = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, checked((uint)width), checked((uint)height), 1, 1, viewCount, 1, Format.R64UInt, TextureUsages.Sampled | TextureUsages.Storage, label: "Cluster visibility winner image"));
        }
        else if (_geometry._visibilityMode is ClusterVisibilityMode.BufferAtomic64 or ClusterVisibilityMode.NonAtomic)
        {
            frame.VisibilityWinnerBuffer = frame.AddBuffer(ref graph, checked((ulong)width * (ulong)height * viewCount * 8u), BufferUsages.None, "Cluster visibility winner buffer");
        }

        frame.Depth = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, checked((uint)width), checked((uint)height), 1, 1, viewCount, 1, Format.D32Float, TextureUsages.Sampled | TextureUsages.DepthStencilAttachment | TextureUsages.CopySource, label: "Cluster scene depth"));
        {
            frame.NeutralPolicyOutput = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R32UInt, TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.Sampled, label: "Cluster neutral target output"));
            if (_geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly && _geometry._renderContext.IsValid)
            {
                frame.PolicyVisibilityScratch = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, _geometry._renderContext.RasterWidth, _geometry._renderContext.RasterHeight, 1, 1, 1, 1, Format.R32UInt, TextureUsages.ColorAttachment, label: "Cluster policy hardware visibility scratch"));
                frame.PolicyDepthScratch = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, _geometry._renderContext.RasterWidth, _geometry._renderContext.RasterHeight, 1, 1, 1, 1, Format.D32Float, TextureUsages.DepthStencilAttachment, label: "Cluster policy hardware depth scratch"));
            }
        }

        frame.SceneColor = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, checked((uint)width), checked((uint)height), 1, 1, viewCount, 1, Format.R16G16B16A16Float, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource, label: "Cluster scene color"));
        frame.MotionVectors = graph.CreateTexture(new TextureDesc(TextureDimension.Texture2D, checked((uint)width), checked((uint)height), 1, 1, viewCount, 1, Format.R16G16Float, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource, label: "Cluster motion vectors"));
        RecordBufferInitialization(ref graph, in frame);
        RecordTargetInitialization(ref graph, in frame);
        RecordVisibilityWinnerClear(ref graph, in frame);
        return frame;
    }

    internal void RecordVisibilityWinnerClear(ref RenderGraphEdit graph, in FrameResources frame)
    {
        if (_geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly)
        {
            return;
        }

        ClusterComputePipeline pipeline = _geometry._pipelines?.WinnerClear ?? throw new InvalidOperationException("The selected visibility mode has no clear pipeline.");
        GraphBufferSlice uniforms = UploadUniform(ref graph, new ClusterVisibilityWinnerUniforms { Width = checked((uint)frame.Width), Height = checked((uint)frame.Height), ViewCount = frame.ViewCount }, "Cluster visibility winner clear uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, "Uniforms", GraphParameterResourceBinding.ConstantBuffer(CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        if (_geometry._visibilityMode == ClusterVisibilityMode.ImageAtomic64)
        {
            AddBinding(ref bindingCount, "Winner", GraphParameterResourceBinding.StorageTexture(CreateStorageTextureView(graph, frame.VisibilityWinnerImage, Format.R64UInt, null, TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
            _ = AddComputePass(ref graph, "Clear Cluster visibility winner image", pipeline, new ClusterDispatchParameters(new DispatchArguments(Groups(frame.Width, 8), Groups(frame.Height, 8), frame.ViewCount)), bindingCount, ClusterDispatchParameters.Record);
            return;
        }

        AddBinding(ref bindingCount, "Winner", GraphParameterResourceBinding.WritableBuffer(CreateStorageBufferView(graph, frame.VisibilityWinnerBuffer), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        ulong pixels = checked((ulong)frame.Width * (ulong)frame.Height * frame.ViewCount);
        ulong groups = (pixels + 63u) / 64u;
        uint groupsX = checked((uint)Math.Min(groups, 65_535u));
        uint groupsY = checked((uint)((groups + 65_534u) / 65_535u));
        _ = AddComputePass(ref graph, "Clear Cluster visibility winner buffer", pipeline, new ClusterDispatchParameters(new DispatchArguments(groupsX, groupsY, 1)), bindingCount, ClusterDispatchParameters.Record);
    }

    internal void RecordSpilledRaster(ref RenderGraphEdit graph, in FrameResources frame, ClusterRasterDispatch stage,
        ClusterGeometryPipeline.DeformDrawData draw, bool phaseOne, bool policyTarget, GraphRegionId region)
    {
        if (stage.Tessellation is not null) return;
        if (_geometry._visibilityMode != ClusterVisibilityMode.HardwareOnly)
        {
            ClusterComputePipeline software = (policyTarget ? stage.SpillTargetStages?.Software : null) ?? stage.SpillStages.Software!;
            GraphBufferSlice uniforms = UploadUniform(ref graph, new ClusterSoftwareRasterUniforms
            {
                ScreenWidth = (uint)frame.Width, ScreenHeight = (uint)frame.Height, MaxBins = frame.RasterBinCount,
                CurrentBin = stage.BinIndex, EarlyDepthEnabled = stage.UsesEarlyDepth ? 1u : 0u,
                MaxVisibleClusters = _geometry._options.MaxCandidates
            }, "Cluster spilled software raster uniforms");
            var dispatch = new ClusterSoftwareRasterPassData(_geometry.RequireDispatchIndirectLayout(), frame.RasterSpillArguments, 0u);
            GraphPassId id = AddComputePass(ref graph, "Cluster spilled software raster", software, dispatch, 0,
                ClusterSoftwareRasterPassData.Declare, ClusterSoftwareRasterPassData.Record, region: region);
            graph.SetPassTimeGroup(id, ClusterGeometryPipeline.RasterTimeGroup);
            PassDefinition pass = graph.GetPass(id);
            _shading.BindSoftwareRasterResources(ref graph, ref pass, stage, uniforms, policyTarget, draw, true);
        }
        ClusterRasterPipeline? target = policyTarget ? stage.SpillTargetStages?.Hardware : null;
        ClusterRasterPipeline hardware = target ?? stage.SpillStages.Hardware!;
        bool attachments = _geometry._visibilityMode == ClusterVisibilityMode.HardwareOnly && target is null;
        uint layers = policyTarget ? 1u : frame.ViewCount;
        GraphTextureId color = policyTarget ? frame.PolicyVisibilityScratch : frame.VisBuffer;
        GraphTextureId depth = policyTarget ? frame.PolicyDepthScratch : frame.Depth;
        GraphColorAttachmentViewId colorView = attachments ? graph.CreateColorAttachmentView(color,
            new(0,1,0,layers,TextureAspects.Color),format:Format.R32UInt,dimension:TextureViewDimension.Texture2DArray) : default;
        GraphDepthStencilViewId depthView = attachments ? graph.CreateDepthStencilView(depth,
            new(0,1,0,layers,TextureAspects.Depth),Format.D32Float,TextureViewDimension.Texture2DArray) : default;
        int width = policyTarget ? (int)_geometry._renderContext.RasterWidth : frame.Width;
        int height = policyTarget ? (int)_geometry._renderContext.RasterHeight : frame.Height;
        GraphBufferSlice views = UploadUniform(ref graph, new ClusterDrawUniforms
        {
            ScreenWidth = (uint)frame.Width, ScreenHeight = (uint)frame.Height,
            RasterWidth = (uint)width, RasterHeight = (uint)height
        }, "Cluster spilled hardware viewport");
        GraphBufferSlice parameters = UploadUniform(ref graph, new ClusterDrawDispatchUniforms
        {
            DrawArgsByteOffset = 16u, EarlyDepthEnabled = stage.UsesEarlyDepth ? 1u : 0u,
            CurrentBin = stage.BinIndex, MaxVisibleClusters = _geometry._options.MaxCandidates
        }, "Cluster spilled hardware raster uniforms");
        var drawData = new ClusterHardwareRasterPassData(_geometry.RequireDrawIndirectLayout(),frame.RasterSpillArguments,
            16u,width,height,false);
        GraphPassId hardwarePass = AddRasterPass(ref graph,"Cluster spilled hardware raster",hardware,drawData,0,
            (ref PassDefinition pass,ref ClusterHardwareRasterPassData data) =>
            {
                if (attachments)
                {
                    pass.ColorAttachment(0,colorView,LoadType.Load,StoreType.Store,WriteCoverage.Partial,default);
                    pass.DepthStencilAttachment(depthView,LoadType.Load,StoreType.Store,WriteCoverage.Partial,1f,
                        LoadType.Discard,StoreType.Discard,WriteCoverage.Complete,0);
                }
                ClusterHardwareRasterPassData.Declare(ref pass,ref data);
            },ClusterHardwareRasterPassData.Record,
            attachments ? default : new PassOptions(RasterWidth:(uint)width,RasterHeight:(uint)height),region:region);
        graph.SetPassTimeGroup(hardwarePass,ClusterGeometryPipeline.RasterTimeGroup);
        PassDefinition hardwareDefinition = graph.GetPass(hardwarePass);
        _shading.BindHardwareRasterResources(ref graph,ref hardwareDefinition,stage,views,parameters,policyTarget,draw,true);
    }

    internal void GrowPageRequestPriorities(ref RenderGraphEdit graph)
    {
        FrameResources frame = GraphData.Resources;
        frame.PagePriorityCapacity = _geometry._resources.GeometryPageCount;
        SetStageBuffer(ref graph, frame.PageFaults, checked(3u * sizeof(uint) + (ulong)frame.PageFaultCapacity * 24u + (ulong)frame.PagePriorityCapacity * sizeof(uint)), BufferUsages.CopySource, "Cluster page faults");
        GraphData.Resources = frame;
    }

    internal void GrowRasterWork()
    {
        if (_rasterBatchFeedback.Required <= GraphData.Resources.RasterBatchCapacity) return;
        uint capacity = checked(_rasterBatchFeedback.Required * 2u);
        EditGraph((ref RenderGraphEdit edit) =>
        {
            FrameResources frame = GraphData.Resources;
            SetStageBuffer(ref edit,frame.BinnedClusters,(ulong)capacity * BinnedClusterStride,
                BufferUsages.None,"Cluster raster bin indices");
            frame.RasterBatchCapacity = capacity;
            GraphData.Resources = frame;
            _geometry.UpdateRasterBinning(ref edit,GraphData.RasterPhases);
        });
    }

    private static void RemoveRasterScans(ref RenderGraphEdit graph, ClusterGeometryPipeline.RasterBinning binning)
    {
        ScreenBinningScanPasses.Remove(ref graph,binning.BatchScan);
        ScreenBinningScanPasses.Remove(ref graph,binning.SoftwareScan);
    }

    internal void ResizeRasterScans(ref RenderGraphEdit graph)
    {
        FrameResources frame = GraphData.Resources;
        for (int index = 0; index < GraphData.RasterBinning.Length; index++)
        {
            if (GraphData.RasterBinning[index] is not { } binning) continue;
            RemoveRasterScans(ref graph,binning);
            var batch = ScreenBinningScanPasses.Add(ref graph,_shading._renderDevice.Pipelines,
                _geometry._pipelines!.RasterBinScanShader,frame.RasterBatchOffsets,frame.RasterBinCount + 1u,
                binning.Prepare,binning.ScanRegion);
            var software = ScreenBinningScanPasses.Add(ref graph,_shading._renderDevice.Pipelines,
                _geometry._pipelines.RasterBinScanShader,frame.RasterSoftwareOffsets,frame.RasterBinCount + 1u,
                batch.CompletePass,binning.ScanRegion);
            graph.OrderAfter(binning.Reserve,software.CompletePass);
            GraphData.RasterBinning[index] = binning with { BatchScan = batch,SoftwareScan = software };
        }
    }

    internal void ReconcileStageResources(ref RenderGraphEdit graph, ClusterMaterialAssignmentSet assignments, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        FrameResources frame = GraphData.Resources;
        uint rasterBinCount = rasterStages.BinCount;
        bool depthBucketsEnabled = _geometry._options.EnableDepthBuckets && rasterStages.Dispatches.Any(static stage => stage.PixelVisibility);
        if (depthBucketsEnabled != frame.DepthBucketsEnabled || rasterBinCount != frame.RasterBinCount)
        {
            frame.DepthBucketsEnabled = depthBucketsEnabled;
            SetStageBuffer(ref graph, frame.DepthBuckets, depthBucketsEnabled ? checked((ulong)rasterBinCount * 512u * sizeof(uint)) : sizeof(uint), BufferUsages.None, "Cluster depth buckets");
        }

        uint tessellationBinCount = checked(rasterBinCount * 8u);
        uint shadeBinCount = shadeStages.BinCount;
        bool rasterCapacityChanged = frame.RasterBinCount != rasterBinCount;
        bool shadeCapacityChanged = frame.ShadeBinCount != shadeBinCount;
        frame.RasterBinCount = rasterBinCount;
        frame.ShadeBinCount = shadeBinCount;
        frame.MaterialAddressCapacity = assignments.MaterialAddressCapacity;
        _shading.UpdateMaterialIndexBuffers(ref graph, rasterStages, shadeStages);
        if (rasterCapacityChanged)
        {
            SetStageBuffer(ref graph, frame.RasterBinMeta, checked((ulong)rasterBinCount * RasterBinStride), BufferUsages.None, "Cluster raster bins");
            SetStageBuffer(ref graph, frame.RasterBatchOffsets, ((ulong)rasterBinCount + 1u) * sizeof(uint), BufferUsages.None, "Cluster raster batch offsets");
            SetStageBuffer(ref graph, frame.RasterSoftwareOffsets, ((ulong)rasterBinCount + 1u) * sizeof(uint), BufferUsages.None, "Cluster raster software offsets");
            SetStageBuffer(ref graph, frame.HardwareIndirectArgs, checked((ulong)rasterBinCount * ClusterIndirectLayout.HardwareRasterStride), BufferUsages.Indirect, "Cluster hardware indirect arguments");
            SetStageBuffer(ref graph, frame.SoftwareDispatchArgs, checked((ulong)rasterBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster software raster dispatches");
            SetStageBuffer(ref graph, frame.TessellationSeedDispatchArgs, checked((ulong)rasterBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster tessellation seed dispatches");
            SetStageBuffer(ref graph, frame.TessellationBinCounts, checked((ulong)tessellationBinCount * sizeof(uint)), BufferUsages.None, "Cluster tessellation bin counts");
            SetStageBuffer(ref graph, frame.TessellationBinOffsets, checked((ulong)tessellationBinCount * sizeof(uint)), BufferUsages.None, "Cluster tessellation bin offsets");
            SetStageBuffer(ref graph, frame.TessellationBinScatterCounts, checked((ulong)tessellationBinCount * sizeof(uint)), BufferUsages.None, "Cluster tessellation bin scatter counts");
            SetStageBuffer(ref graph, frame.TessellationBinDispatchArgs, checked((ulong)tessellationBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster tessellation split or dice dispatches");
            SetStageBuffer(ref graph, frame.TessellationHardwareDrawArgs, checked((ulong)tessellationBinCount * ClusterIndirectLayout.DrawIndexedBytes + (ulong)rasterBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster tessellation hardware draw arguments");
        }

        if (shadeCapacityChanged)
        {
            SetStageBuffer(ref graph, frame.ShadeBinCounts, checked((ulong)shadeBinCount * 2ul * sizeof(uint)), _geometry._options.EnableFrameMetricsReadback ? BufferUsages.CopySource : BufferUsages.None, "Cluster shade bin counts");
            SetStageBuffer(ref graph, frame.ShadeBinOffsets, checked((ulong)shadeBinCount * 2ul * sizeof(uint)), BufferUsages.None, "Cluster shade bin offsets");
            SetStageBuffer(ref graph, frame.ShadeIndirectArgs, checked((ulong)shadeBinCount * ClusterIndirectLayout.DispatchBytes), BufferUsages.Indirect, "Cluster shade dispatches");
            SetStageBuffer(ref graph, frame.ShadeScatterCounts, checked((ulong)shadeBinCount * 2ul * sizeof(uint)), BufferUsages.None, "Cluster shade scatter counts");
        }

        GraphData.Resources = frame;
    }

    internal static void SetStageBuffer(ref RenderGraphEdit graph, GraphBufferId buffer, ulong size, BufferUsages additional, string label)
    {
        graph.SetBufferDescription(buffer, new BufferDesc(Math.Max(size, 4), BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | additional, label));
    }

    internal void BindFrameResources(ref RenderGraphFrame graph, in ClusterRenderBinding binding, ClusterCullingHistory history)
    {
        FrameResources frame = GraphData.Resources;
        graph.SetBufferInput(frame.InstanceProperties, new(binding.InstancePropertyMetadata, binding.InstancePropertyMetadataRange));
        ClusterGeometryPipeline.FrameData frameData = _geometry.GetFrameData(ref graph);
        foreach ((GraphBufferId buffer, var writes)in frameData.Source.BufferWrites)
            foreach (BufferWrite write in writes)
                write.Bytes.CopyTo(graph.GetWriteSpan(buffer, write.Range.Offset, checked((int)write.Range.Size)), checked((int)write.Range.Offset));
        int current = history.WriteIndex;
        graph.SetTextureInput(frame.PreviousHiZ, frame.HiZ[1 - current]);
        graph.SetTextureInput(frame.CurrentHiZ, frame.HiZ[current]);
        foreach (var write in frameData.Writes)
            write.Write.Bytes.CopyTo(graph.GetWriteSpan(write.Buffer, write.Write.Range.Offset, checked((int)write.Write.Range.Size)),
                checked((int)write.Write.Range.Offset));
        frameData.CurrentHiZ = frame.HiZ[current];
    }

    internal void RecordResidencyUploads(ref RenderGraphFrame graph, in ClusterRenderBinding binding, bool writeGeometry)
    {
        _geometry.BindLeafFlagFixups(ref graph, writeGeometry ? binding.Changes : null);
        if (!writeGeometry || binding.Changes is not { } publication)
            return;
        foreach (ClusterStagedUpload upload in publication.Meshes.PageUploads)
            graph.WriteBuffer(GraphData.Resources.PageHeap, upload.Offset, upload.Bytes.Span);
        foreach (ClusterStagedUpload upload in publication.Meshes.Bvh.Uploads)
            graph.WriteBuffer(GraphData.Resources.Bvh, upload.Offset, upload.Bytes.Span);
    }

    internal void DeclareResidencyReads(ref RenderGraphFrame graph, in ClusterRenderBinding binding)
    {
        RenderGeometryFrameRanges ranges = GraphData.Outputs.Geometry.GetFrameRanges(ref graph);
        graph.SetBufferRanges(GraphData.Resources.PageHeap, ranges.ResidentPageReads);
        graph.SetBufferRanges(GraphData.Resources.Bvh, ranges.BvhReadRanges);
        foreach (GraphPassId build in _geometry._resources.GeometryBuilds(ref graph))
            foreach (GraphPassId pass in GraphData.Passes)
                graph.OrderAfter(pass, build);
    }

    internal void WriteFrameUploads(ref RenderGraphFrame graph, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages, scoped ReadOnlySpan<ClusterViewUniforms> packedViews, int primaryViewCount)
    {
        graph.SetBufferData(GraphData.Resources.PackedViews, MemoryMarshal.AsBytes(packedViews));
        Span<byte> parametersBytes = graph.GetBufferDataWriteSpan(GraphData.ShaderParameterBytes);
        GraphData.ParameterData.CopyTo(parametersBytes);
        if (_geometry._options.EnableFrameMetricsReadback)
        {
            ulong frameIndex = _geometry.GetFrameData(ref graph).MetricFrameIndex;
            graph.WriteBuffer(GraphData.Resources.FrameMetricsBuffer, MetricsFrameIndexOffset, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref frameIndex, 1)));
        }

        WriteUniform(parametersBytes, _geometry.GetFrameData(ref graph).SceneUniforms, GraphData.SceneUniforms);
        WriteUniform(parametersBytes, new ClusterTraversalSeedUniforms { ViewCount = checked((uint)primaryViewCount), ViewIndexMode = 0u, DispatchCountIndex = 2u, MaxNodes = _geometry._options.MaxCandidates, UseInstanceGroups = _geometry._options.UseInstanceGroups ? 1u : 0u }, GraphData.PrimarySeedUniforms);
        if (_geometry._renderContext.IsValid)
            WriteUniform(parametersBytes, new ClusterTraversalSeedUniforms { ViewCount = _geometry._renderContext.MaximumDispatchCount, ViewIndexMode = 1u, DispatchCountIndex = _geometry._renderContext.DispatchCountIndex, MaxNodes = _geometry._options.MaxCandidates, UseInstanceGroups = _geometry._options.UseInstanceGroups ? 1u : 0u }, GraphData.TargetSeedUniforms);
    }

    internal void WriteUniform<T>(Span<byte> bytes, in T value, GraphBufferSlice input)
        where T : unmanaged => MemoryMarshal.Write(bytes.Slice(checked((int)input.Range.Offset)), in value);
    internal void RecordBufferInitialization(ref RenderGraphEdit graph, in FrameResources frame)
    {
        AddSmallClear(ref graph, frame.CandidateCount, new BufferRange(0, sizeof(uint)), "Initialize Cluster candidate count");
        AddSmallClear(ref graph, frame.Phase2CandidateCount, new BufferRange(0, sizeof(uint)), "Initialize Cluster deferred work count");
        AddSmallClear(ref graph, frame.TraversalArguments0, BufferRange.Whole, "Initialize Cluster hierarchy arguments 0");
        AddSmallClear(ref graph, frame.PageFaults, BufferRange.Whole, "Initialize Cluster page-fault count");
        AddSmallClear(ref graph, frame.SoftwareDebug, new BufferRange(0, sizeof(uint)), "Initialize Cluster software debug count");
        AddSmallClear(ref graph, frame.NeutralPolicyCounter, new BufferRange(0, sizeof(uint)), "Initialize Cluster neutral policy counter");
        ClusterBufferCopyParameters copy = new(frame.CandidateArgsInitialization, 0, frame.CandidateArgs, 0, 16);
        _ = graph.AddCopyFramePass("Initialize Cluster candidate dispatch arguments", PassQueueSelection.AnyOfType(QueueType.Graphics), copy, new PassOptions(RepeatCommands: true), static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
        {
            _ = access.Read(data.Source, new BufferRange(data.SourceOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = access.Write(data.Destination, new BufferRange(data.DestinationOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, ClusterBufferCopyParameters.Record, region: _geometry._regions.PrimaryGeometry);
        ClusterBufferCopyParameters phaseTwoCopy = new(frame.CandidateArgsInitialization, 0, frame.Phase2CandidateArgs, 0, 16);
        _ = graph.AddCopyFramePass("Initialize Cluster deferred dispatch arguments", PassQueueSelection.AnyOfType(QueueType.Graphics), phaseTwoCopy, new PassOptions(RepeatCommands: true), static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
        {
            _ = access.Read(data.Source, new BufferRange(data.SourceOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopySource);
            _ = access.Write(data.Destination, new BufferRange(data.DestinationOffset, data.ByteCount), PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }, ClusterBufferCopyParameters.Record, region: _geometry._regions.PrimaryGeometry);
        void AddSmallClear(ref RenderGraphEdit activeGraph, GraphBufferId buffer, in BufferRange range, string label)
        {
            var clear = new ClusterBufferRangeClearParameters(buffer, range);
            _ = activeGraph.AddCopyFramePass(label, PassQueueSelection.AnyOfType(QueueType.Graphics), clear, new PassOptions(RepeatCommands: true), static (ref PassDefinition access, ref ClusterBufferRangeClearParameters data) => _ = access.Write(data.Buffer, data.Range, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete), ClusterBufferRangeClearParameters.Record, region: _geometry._regions.PrimaryGeometry);
        }
    }

    internal void RecordTargetInitialization(ref RenderGraphEdit graph, in FrameResources frame)
    {
        TextureSubresourceRange colors = new(0, 1, 0, frame.ViewCount, TextureAspects.Color);
        GraphColorAttachmentViewId vis = graph.CreateColorAttachmentView(frame.VisBuffer, colors, dimension: TextureViewDimension.Texture2DArray, label: "Cluster visibility clear view");
        GraphColorAttachmentViewId scene = graph.CreateColorAttachmentView(frame.SceneColor, colors, dimension: TextureViewDimension.Texture2DArray, label: "Cluster scene clear view");
        GraphDepthStencilViewId depth = graph.CreateDepthStencilView(frame.Depth, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth), dimension: TextureViewDimension.Texture2DArray, label: "Cluster depth clear view");
        ClusterTargetClearParameters data = new(vis, scene, depth);
        _ = graph.AddRasterFramePass("Clear Cluster frame targets", PassQueueSelection.AnyOfType(QueueType.Graphics), data, new PassOptions(RepeatCommands: true), ClusterTargetClearParameters.Declare, ClusterTargetClearParameters.Record, region: _geometry._regions.PrimaryGeometry);
        {
            GraphColorAttachmentViewId neutralPolicyOutput = graph.CreateColorAttachmentView(frame.NeutralPolicyOutput, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), dimension: TextureViewDimension.Texture2D, label: "Cluster neutral target output clear view");
            ClusterColorAttachmentClearParameters neutralData = new(neutralPolicyOutput, Vector4.Zero);
            _ = graph.AddRasterFramePass("Clear Cluster neutral target output", PassQueueSelection.AnyOfType(QueueType.Graphics), neutralData, new PassOptions(RepeatCommands: true), ClusterColorAttachmentClearParameters.Declare, ClusterColorAttachmentClearParameters.Record, region: _geometry._regions.PrimaryGeometry);
        }

        if (frame.PolicyVisibilityScratch != default && frame.PolicyDepthScratch != default)
            RecordPolicyScratchClear(ref graph, frame);
    }

    private void RecordPolicyScratchClear(ref RenderGraphEdit graph, in FrameResources frame)
    {
        GraphColorAttachmentViewId visibility = graph.CreateColorAttachmentView(frame.PolicyVisibilityScratch,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2DArray);
        GraphDepthStencilViewId depth = graph.CreateDepthStencilView(frame.PolicyDepthScratch,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2DArray);
        var data = new ClusterPolicyTargetClearParameters(visibility, depth);
        graph.AddRasterFramePass("Clear Cluster policy hardware scratch", PassQueueSelection.AnyOfType(QueueType.Graphics),
            data, new PassOptions(RepeatCommands: true), ClusterPolicyTargetClearParameters.Declare,
            ClusterPolicyTargetClearParameters.Record, _geometry._regions.PrimaryGeometry);
    }

    internal void AuthorHistoryInitialization(ref RenderGraphEdit graph, in FrameResources frame)
    {
        GraphData.HistoryInitialization = graph.AddRegion("Cluster history initialization", _geometry._regions.PrimaryGeometry);
        foreach (GraphTextureId texture in frame.HiZ)
            for (int mip = 0; mip < ClusterCullingHistory.CalculateMipCount(frame.Width, frame.Height); mip++)
            {
                GraphColorAttachmentViewId view = graph.CreateColorAttachmentView(texture, new TextureSubresourceRange(checked((uint)mip), 1, 0, frame.ViewCount, TextureAspects.Color), dimension: TextureViewDimension.Texture2DArray, label: $"Cluster HiZ initialization mip {mip}");
                var data = new ClusterColorAttachmentClearParameters(view, Vector4.Zero);
                graph.AddRasterFramePass($"Initialize Cluster HiZ mip {mip}", PassQueueSelection.AnyOfType(QueueType.Graphics), data, default, ClusterColorAttachmentClearParameters.Declare, ClusterColorAttachmentClearParameters.Record, region: GraphData.HistoryInitialization);
            }
    }

    internal GraphBufferSlice UploadUniform<T>(ref RenderGraphEdit graph, T value, string name)
        where T : unmanaged
    {
        int valueSize = Unsafe.SizeOf<T>();
        Span<byte> bytes = stackalloc byte[valueSize];
        MemoryMarshal.Write(bytes, in value);
        GraphBufferSlice input = CreateParameters(ref graph, bytes, name);
        _geometry._rasterInputs?.Uniforms.Add(input);
        return input;
    }

    internal static ushort[] CreateTessellationPatternIndices()
    {
        // The eight regular triangular grids share this immutable index buffer.
        var indices = new ushort[3 * 204];
        int output = 0;
        for (int factor = 1; factor <= 8; factor++)
            for (int row = 0; row < factor; row++)
            {
                int top = row * (factor + 1) - row * (row - 1) / 2;
                int bottom = top + factor + 1 - row;
                for (int column = 0; column < factor - row; column++)
                {
                    indices[output++] = checked((ushort)(top + column));
                    indices[output++] = checked((ushort)(top + column + 1));
                    indices[output++] = checked((ushort)(bottom + column));
                    if (column + 1 < factor - row)
                    {
                        indices[output++] = checked((ushort)(top + column + 1));
                        indices[output++] = checked((ushort)(bottom + column + 1));
                        indices[output++] = checked((ushort)(bottom + column));
                    }
                }
            }

        return indices;
    }

    internal GraphBufferId UploadStructs<T>(ref RenderGraphEdit graph, scoped ReadOnlySpan<T> values, BufferUsages usage, string name)
        where T : unmanaged
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one value.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    internal GraphBufferId UploadWords(ref RenderGraphEdit graph, scoped ReadOnlySpan<uint> values, BufferUsages usage, string name)
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one word.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    internal GraphBufferId UploadBytes(ref RenderGraphEdit graph, ClusterBytes bytes, BufferUsages usages, string label)
    {
        GraphBufferId buffer = CreateBufferData(ref graph, bytes, usages, label);
        _geometry._rasterInputs?.Buffers.Add(buffer);
        return buffer;
    }

    internal GraphBufferId UploadBytes(ref RenderGraphEdit graph, scoped ReadOnlySpan<byte> bytes, BufferUsages usage, string name)
    {
        GraphBufferId buffer = CreateBufferData(ref graph, bytes, usage, name);
        _geometry._rasterInputs?.Buffers.Add(buffer);
        return buffer;
    }

    internal static FrameResources GetRasterFrameResources(in FrameResources frame, bool target, bool layerTiles = false)
    {
        FrameResources resources = frame;
        if (layerTiles)
        {
            resources.PackedViews = frame.LayerPackedViews;
            resources.VisibleClusters = frame.LayerVisibleClusters;
            resources.DrawArgs = frame.LayerDrawArgs;
            resources.Phase2DrawArgs = frame.LayerSecondDrawArgs;
            resources.Phase2CandidateClusters = frame.LayerDeferredWork;
            resources.Phase2CandidateCount = frame.LayerDeferredCount;
            resources.Phase2CandidateArgs = frame.LayerDeferredArgs;
        }
        else if (target)
        {
            resources.VisibleClusters = frame.TargetVisibleClusters;
            resources.Phase2CandidateClusters = frame.TargetDeferredWork;
            resources.Phase2CandidateCount = frame.TargetDeferredCount;
            resources.Phase2CandidateArgs = frame.TargetDeferredArgs;
        }

        return resources;
    }

    internal struct FrameResources
    {
        public FrameResources()
        {
        }

        internal int Width;
        internal int Height;
        internal uint ViewCount;
        internal uint RasterBinCount;
        internal uint ShadeBinCount;
        internal uint MaterialAddressCapacity;
        internal uint RasterBatchCapacity;
        internal int PageFaultCapacity;
        internal uint PagePriorityCapacity;
        internal GraphBufferId PageHeap;
        internal GraphBufferId Bvh;
        internal GraphBufferId WorkStatus;
        internal GraphBufferId InstanceData;
        internal GraphBufferInput InstanceProperties = null !;
        internal BufferRange InstancePropertiesRange;
        internal GraphBufferId FrameMetricsBuffer;
        internal GraphTextureId[] HiZ = [];
        internal GraphTextureInput PreviousHiZ = null !;
        internal GraphTextureInput CurrentHiZ = null !;
        internal GraphBufferId PackedViews;
        internal GraphBufferId LayerPackedViews;
        internal GraphBufferId LayerVisibleClusters;
        internal GraphBufferId LayerTileWindows;
        internal GraphBufferId LayerDrawArgs;
        internal GraphBufferId LayerSecondDrawArgs;
        internal GraphBufferId LayerDeferredWork;
        internal GraphBufferId LayerDeferredCount;
        internal GraphBufferId LayerDeferredArgs;
        internal GraphBufferId RasterBinIndices;
        internal GraphBufferId MaterialBounds;
        internal GraphBufferId ShadeBinIndices;
        internal GraphBufferId DeformCache;
        internal GraphBufferId DeformRegions;
        internal GraphBufferId DeformRequests;
        internal GraphBufferId DeformPrefixOffsets;
        internal GraphBufferId DeformPrefixGroups;
        internal GraphBufferId DeformAllocation;
        internal GraphBufferId DeformArguments;
        internal GraphBufferId ReadOffsetZero;
        internal GraphBufferId CandidateArgs;
        internal GraphBufferId NeutralPolicyValue;
        internal GraphBufferId NeutralPolicyCounter;
        internal GraphBufferId NeutralPolicyView;
        internal GraphTextureId NeutralPolicyOutput;
        internal ClusterGeometryPipeline.CullWork Work = null !;
        internal GraphBufferId TraversalNodes0;
        internal GraphBufferId TraversalArguments0;
        internal GraphBufferId CandidateArgsInitialization;
        internal GraphBufferId CandidateClusters;
        internal GraphBufferId CandidateCount;
        internal GraphBufferId DrawArgs;
        internal GraphBufferId PageFaults;
        internal GraphBufferId Phase2CandidateArgs;
        internal GraphBufferId Phase2CandidateClusters;
        internal GraphBufferId Phase2CandidateCount;
        internal GraphBufferId Phase2DrawArgs;
        internal GraphBufferId ContinuationDrawArgs;
        internal GraphBufferId ContinuationPostDrawArgs;
        internal GraphBufferId VisibleClusters;
        internal GraphBufferId TargetDeferredWork;
        internal GraphBufferId TargetDeferredCount;
        internal GraphBufferId TargetDeferredArgs;
        internal GraphBufferId TargetVisibleClusters;
        internal GraphBufferId RasterBinMeta;
        internal GraphBufferId DepthBuckets;
        internal bool DepthBucketsEnabled;
        internal GraphBufferId BinnedClusters;
        internal GraphBufferId RasterBatchOffsets, RasterSoftwareOffsets, RasterSpillClusters, RasterSpillArguments, RasterBatchDemand;
        internal GraphBufferId HardwareIndirectArgs;
        internal GraphBufferId RasterWorkArguments;
        internal GraphBufferId SoftwareDispatchArgs;
        internal GraphBufferId RasterReserveCounters;
        internal GraphBufferId TessellationCounters;
        internal GraphBufferId PhaseOneTessellationCounters;
        internal GraphBufferId TessellationWorkPatches;
        internal GraphBufferId TessellationWorkPatchesAlternate;
        internal GraphBufferId TessellationFinalPatches;
        internal GraphBufferId TessellationLevelCounts;
        internal GraphBufferId[] TessellationLevelDispatchArgs = [];
        internal GraphBufferId TessellationSourceDispatchArgs;
        internal GraphBufferId TessellationSeedDispatchArgs;
        internal GraphBufferId TessellationBinCounts;
        internal GraphBufferId TessellationBinOffsets;
        internal GraphBufferId TessellationBinScatterCounts;
        internal GraphBufferId TessellationBinReserveCounter;
        internal GraphBufferId TessellationBinDispatchArgs;
        internal GraphBufferId TessellationHardwareDrawArgs;
        internal GraphBufferId TessellationPatternIndices;
        internal GraphBufferId SoftwareDebug;
        internal GraphBufferId ShadeBinCounts;
        internal GraphBufferId ShadeBinOffsets;
        internal GraphBufferId ShadeIndirectArgs;
        internal GraphBufferId ShadeScatterCounts;
        internal GraphBufferId ShadeReserveCounters;
        internal GraphBufferId PixelCoordinates;
        internal GraphTextureId VisBuffer;
        internal GraphTextureId VisibilityWinnerImage;
        internal GraphBufferId VisibilityWinnerBuffer;
        internal GraphTextureId Depth;
        internal GraphTextureId PolicyVisibilityScratch;
        internal GraphTextureId PolicyDepthScratch;
        internal GraphTextureId SceneColor;
        internal GraphTextureId MotionVectors;
        internal GraphBufferId AddBuffer(ref RenderGraphEdit graph, ulong size, BufferUsages additional, string name)
        {
            return graph.CreateTransientBuffer(new BufferDesc(Math.Max(size, 4), BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | additional, name));
        }
    }

    public void Apply(in ClusterMaterialChange change)
    {
        ClusterMaterialChange update = change;
        ClusterRasterStageSet raster = update.RasterStages;
        ClusterShadeStageSet shade = update.ShadeStages;
        ClusterMaterialScalars scalars = update.Scalars;
        if (_graphInstalled && ReferenceEquals(raster, _installedRasterStages) && ReferenceEquals(shade, _installedShadeStages) && update.ChangedScalarRanges.IsEmpty && update.ChangedTextures.Count == 0)
            return;
        EditGraph((ref RenderGraphEdit edit) =>
        {
            if (!_graphInstalled)
            {
                GraphData.ScalarBytes = CreateBufferData(ref edit, scalars.Bytes, BufferUsages.ShaderRead, "Cluster material scalars");
                GraphData.ScalarOffsets = CreateBufferData(ref edit, scalars.Offsets, BufferUsages.ShaderRead, "Cluster material scalar offsets");
                GraphData.ShadeScalars = edit.CreateBufferInput(BufferUsages.ShaderRead);
                GraphData.ShadeScalarOffsets = edit.CreateBufferInput(BufferUsages.ShaderRead);
                GraphData.RasterPhases = InstallGraph(ref edit, _geometry._outputDescription, update.Assignments, raster, shade);
            }
            else
            {
                _shading.UpdateScalarBuffers(ref edit, scalars);
                uint previousRasterBins = GraphData.Resources.RasterBinCount;
                uint previousMaterialAddressCapacity = GraphData.Resources.MaterialAddressCapacity;
                ReconcileStageResources(ref edit, update.Assignments, raster, shade);
                _shading.UpdateMaterialIndexBuffers(ref edit, raster, shade);
                if (!ReferenceEquals(shade, _installedShadeStages))
                    _shading.UpdateShadeBinning(ref edit, shade);
                if (!ReferenceEquals(raster, _installedRasterStages) || update.ChangedTextures.Count != 0)
                    GraphData.RasterPhases = _geometry.ReconcileRasterPhases(ref edit, raster, shade);
                if (update.ChangedRasterBins.Count != 0 || previousRasterBins != GraphData.Resources.RasterBinCount ||
                    previousMaterialAddressCapacity != GraphData.Resources.MaterialAddressCapacity)
                {
                    _geometry.UpdateRasterBinning(ref edit, GraphData.RasterPhases);
                    _geometry.UpdateRasterCapacity(ref edit, previousRasterBins, GraphData.RasterPhases);
                }

                if (!ReferenceEquals(shade, _installedShadeStages) || update.ChangedTextures.Count != 0)
                    GraphData.ShadeGroups = _shading.UpdateShadeGroups(ref edit, shade);
            }

            var resources = _shading.UpdateMaterialResources(ref edit, raster, shade);
            GraphData.MaterialBuffers = resources.Buffers;
            GraphData.MaterialTextures = resources.Textures;
        });
        _installedMaterialTopologyVersion = update.Assignments.TopologyVersion;
        _installedRasterStages = raster;
        _installedShadeStages = shade;
        _shading.UpdateMaterialProjection(update.Assignments, shade);
        _geometry._cullingEnabled = raster.HasFiniteBounds;
        _graphInstalled = true;
        if (update.ChangedRasterBins.Count != 0 || _shading._rasterChangedMaterials.Count != 0)
            _geometry._rasterRevision = checked(_geometry._rasterRevision + 1);
    }
}
