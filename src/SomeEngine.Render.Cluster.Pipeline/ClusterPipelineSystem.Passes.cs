using SomeEngine.Render.Lighting;
using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly GraphParameterResourceBinding[] _renderGraphBindings =
        new GraphParameterResourceBinding[96];

    private ImmutableDictionary<int, RasterPhase> InstallGraph(
        ref RenderGraphEdit graph,
        in ClusterPipelineOutputDescription output,
        ClusterMaterialAssignmentSet assignments,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages)
    {
        FrameResources frame = CreateFrameResources(ref graph, in output, assignments, rasterStages, shadeStages);
        RasterBin[] rasterBins = CreateRasterBins(rasterStages, shadeStages);
        GraphData.Resources = frame;
        AuthorHistoryInitialization(ref graph, frame);
        frame.PackedViews = graph.DeclareExternalBuffer(new BufferDesc(
            checked((ulong)_packedViews.Length * ClusterViewUniforms.SizeInBytes),
            BufferUsages.ShaderRead, "Cluster packed views"), MemoryType.Upload);
        GraphData.Resources = frame;
        RecordFrameMetricsSourceReset(ref graph, frame);
        RecordWorkStatus(ref graph, frame);
        RecordCullStateReset(ref graph, frame);
        InstallSceneBounds(ref graph);
        InstallLeafFlagFixups(ref graph);
        GraphData.PrimaryGeometry = graph.AddRegion("Cluster primary geometry work", _regions.PrimaryGeometry);
        GraphRegionId primaryTraversal = graph.AddRegion("Cluster primary traversal", GraphData.PrimaryGeometry);
        RecordWorkSeed(ref graph, frame, frame.PackedViews, frame.ViewCount, 0u, "primary", primaryTraversal);
        RecordPersistentCull(ref graph, frame, frame.PackedViews, 0u, frame.DrawArgs,
            frame.ReadOffsetZero, frame.ReadOffsetZero, frame.ReadOffsetZero, false, 0, "primary", primaryTraversal);
        RasterPhase first = RecordRasterPhase(ref graph, frame, rasterBins, frame.DrawArgs,
            frame.ReadOffsetZero, frame.ReadOffsetZero, frame.ReadOffsetZero, 0, true);
        GraphRegionId firstHiZ = graph.AddRegion("Cluster first HiZ", GraphData.PrimaryGeometry);
        RecordHiZ(ref graph, frame, true, firstHiZ);
        RecordPhaseOneTessellationCounters(ref graph, frame, firstHiZ);
        GraphRegionId primaryDeferredTraversal = graph.AddRegion("Cluster primary deferred traversal", GraphData.PrimaryGeometry);
        RecordDeferredResume(ref graph, frame, frame.Phase2DrawArgs, "primary post-HiZ", primaryDeferredTraversal);
        RecordPersistentCull(ref graph, frame, frame.PackedViews, 2u, frame.Phase2DrawArgs,
            frame.DrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, false, 1, "primary post-HiZ", primaryDeferredTraversal);
        RasterPhase second = RecordRasterPhase(ref graph, frame, rasterBins, frame.Phase2DrawArgs,
            frame.DrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, 1, false);
        GraphPassId geometryComplete = RecordHiZ(ref graph, frame, false,
            graph.AddRegion("Cluster second HiZ", GraphData.PrimaryGeometry));
        var phases = ImmutableDictionary<int, RasterPhase>.Empty.Add(0, first).Add(1, second);
        phases = RecordContinuation(ref graph, frame, rasterBins, frame.PackedViews, phases);
        RecordShade(ref graph, frame, shadeStages);
        GraphRegionId? receiverConsumers = _options.ExportSurfaceReceivers
            ? graph.AddRegion("Cluster surface receiver consumers", _regions.Shading) : null;
        RecordPageFaultReadback(ref graph, frame);
        RecordFrameMetricsReadback(ref graph, frame, firstHiZ);
        GraphData.Outputs = new ClusterPipelineOutputs(frame.VisBuffer, frame.Depth, frame.SceneColor, frame.MotionVectors,
            new ClusterGeometryCapabilities(frame.PackedViews, frame.InstanceData, frame.InstanceProperties,
                frame.InstancePropertiesRange, frame.VisibleClusters, frame.DrawArgs, frame.Phase2DrawArgs,
                _options.MaxCandidates, frame.PageHeap, frame.Bvh, geometryComplete)
            {
                InstanceWorldBounds = GraphData.InstanceWorldBounds,
                WorkStatus = frame.WorkStatus,
                BoundsReady = GraphData.SceneBounds.Pass,
            }, frame.Width, frame.Height, frame.ViewCount)
        {
            SurfaceReceivers = frame.SurfaceReceivers,
            SurfaceReceiverConsumers = receiverConsumers,
            AdditionalSurfaceReceivers = frame.AdditionalSurfaceReceivers,
            AdditionalReceiverLayerCount = frame.AdditionalReceiverLayerCount,
            CurrentHiZ = frame.HiZ[0],
        };
        return phases;
    }

    private ImmutableDictionary<int, RasterPhase> RecordContinuation(
        ref RenderGraphEdit graph, in FrameResources frame, RasterBin[] rasterBins,
        GraphBufferId packedViews, ImmutableDictionary<int, RasterPhase> phases)
    {
        if (!_renderContext.IsValid) return phases;
        FrameResources targets = GetRasterFrameResources(frame, true);
        GraphData.TargetFirstReference = graph.AddRegion("Cluster target main geometry", _targetPhaseOneRegion);
        GraphData.TargetSecondReference = graph.AddRegion("Cluster target post-HiZ geometry", _targetPhaseTwoRegion);
        var reset = new ClusterContinuationResetParameters(targets.CandidateCount, targets.TraversalArguments0,
            targets.CandidateArgsInitialization, targets.CandidateArgs,
            targets.ContinuationDrawArgs, targets.Phase2CandidateCount, targets.Phase2CandidateArgs, targets.ContinuationPostDrawArgs);
        graph.AddCopyFramePass("Reset Cluster continuation scratch", PassQueueSelection.AnyOfType(QueueType.Graphics),
            reset, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            ClusterContinuationResetParameters.Declare, ClusterContinuationResetParameters.Record, region: GraphData.TargetFirstReference);
        GraphRegionId targetTraversal = graph.AddRegion("Cluster target traversal", GraphData.TargetFirstReference);
        RecordWorkSeed(ref graph, targets, packedViews, _renderContext.MaximumInvocationCount, 1u, "continuation", targetTraversal);
        RecordPersistentCull(ref graph, targets, packedViews, 1u, targets.ContinuationDrawArgs,
            targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ReadOffsetZero, true, 2, "continuation", targetTraversal);
        RasterPhase first = RecordRasterPhase(ref graph, targets, rasterBins, targets.ContinuationDrawArgs,
            targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ReadOffsetZero, 2, true);
        GraphRegionId targetDeferredTraversal = graph.AddRegion("Cluster target deferred traversal", GraphData.TargetSecondReference);
        RecordDeferredResume(ref graph, targets, targets.ContinuationPostDrawArgs, "continuation post-HiZ", targetDeferredTraversal);
        RecordPersistentCull(ref graph, targets, packedViews, 2u, targets.ContinuationPostDrawArgs,
            targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ContinuationDrawArgs, true, 3, "continuation post-HiZ", targetDeferredTraversal);
        RasterPhase second = RecordRasterPhase(ref graph, targets, rasterBins, targets.ContinuationPostDrawArgs,
            targets.ReadOffsetZero, targets.ReadOffsetZero, targets.ContinuationDrawArgs, 3, false);
        return phases.Add(2, first).Add(3, second);
    }

    private void RecordFrameMetricsReadback(
        ref RenderGraphEdit graph,
        in FrameResources frame, GraphRegionId candidateRegion)
    {
        if (!_options.EnableFrameMetricsReadback)
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
            VisibilityProbeX = checked((uint)Math.Max(
                0,
                frame.Width / 2 - VisibilityProbePixelCount / 2)),
            VisibilityProbeY = checked((uint)(frame.Height / 2)),
            Destination = frame.FrameMetricsBuffer,
        };
        GraphPassId candidates = graph.AddCopyFramePass(
            "Copy primary Cluster candidate metrics",
            PassQueueSelection.AnyCompatible(), passData,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition access, ref ClusterFrameMetricsReadbackParameters data) =>
            {
                Read(ref access, data.CandidateCount, 0, sizeof(uint));
                Read(ref access, data.CandidateArgs, 0, 12);
                Read(ref access, data.Phase2CandidateCount, 0, sizeof(uint));
                Read(ref access, data.Phase2CandidateArgs, 0, 12);
                access.Write(data.Destination, new BufferRange(CandidateCountReadbackOffset, 16),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                access.Write(data.Destination, new BufferRange(Phase2CandidateCountReadbackOffset, 16),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            }, ClusterFrameMetricsReadbackParameters.RecordCandidates, region: candidateRegion);
        GraphData.Feedback = GraphData.Feedback.SetItem(FeedbackKind.Candidates, candidates);
        GraphPassId metrics = graph.AddCopyFramePass(
            "Copy Cluster frame metrics",
            PassQueueSelection.AnyCompatible(),
            passData,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition access, ref ClusterFrameMetricsReadbackParameters data) =>
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
                access.Write(data.Destination, new BufferRange(WorkMetricsReadbackOffset, WorkMetricsByteSize),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                _ = access.Read(
                    data.Visibility,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource,
                    TextureLayout.CopySource);
                _ = access.Write(data.Destination,
                    new BufferRange(DrawArgsReadbackOffset, 16),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
                access.Write(data.Destination,
                    new BufferRange(Phase2DrawArgsReadbackOffset, 100),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                access.Write(data.Destination,
                    new BufferRange(VisibilityProbeReadbackOffset, VisibilityProbeRowPitch),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            ClusterFrameMetricsReadbackParameters.Record, region: _regions.Feedback);
        GraphData.Feedback = GraphData.Feedback.SetItem(FeedbackKind.FrameMetrics, metrics);
        GraphData.FrameMetricsReadback = graph.AddReadback(frame.FrameMetricsBuffer, BufferRange.Whole,
            new FrameMetricsReceiver(this), "Read back Cluster frame metrics", _regions.Feedback);
    }

    private void RecordFrameMetricsSourceReset(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        if (!_options.EnableFrameMetricsReadback)
            return;

        var passData = new ClusterFrameMetricsSourceResetParameters(
            frame.RasterReserveCounters,
            frame.ShadeBinCounts,
            frame.ShadeReserveCounters,
            frame.SoftwareDebug,
            frame.PhaseOneTessellationCounters,
            frame.TessellationCounters);
        _ = graph.AddCopyFramePass(
            "Reset Cluster frame metric sources",
            PassQueueSelection.AnyCompatible(),
            passData,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            ClusterFrameMetricsSourceResetParameters.Declare,
            ClusterFrameMetricsSourceResetParameters.Record, region: _regions.PrimaryGeometry);
    }

    private void RecordPageFaultReadback(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PackedViews, ClusterViewUniforms.SizeInBytes), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.PageFaults), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        GraphPassId requests = AddComputePass(ref graph, "Complete Cluster page request priorities",
            _pipelines!.PageRequests, new DispatchArguments((uint)(frame.PageFaultCapacity + 63) / 64u, 1, 1), bindingCount,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch),
            region: _regions.Feedback);
        ulong workOffset = checked(sizeof(uint) + (ulong)frame.PageFaultCapacity * 16u);
        var workCopy = new ClusterBufferCopyParameters(frame.WorkStatus, 0, frame.PageFaults, workOffset, sizeof(uint));
        GraphPassId work = graph.AddCopyFramePass("Copy Cluster work result", PassQueueSelection.AnyCompatible(), workCopy,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
            {
                access.Read(data.Source, new BufferRange(data.SourceOffset, data.ByteCount),
                    PipelineSync.Copy, ResourceAccess.CopySource);
                access.Write(data.Destination, new BufferRange(data.DestinationOffset, data.ByteCount),
                    PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            }, ClusterBufferCopyParameters.Record, region: _regions.Feedback);
        graph.OrderAfter(work, requests);
        GraphData.PageFaultReadback = graph.AddReadback(frame.PageFaults,
            new BufferRange(0, checked(3u * sizeof(uint) + (ulong)frame.PageFaultCapacity * 24u)),
            new PageFaultReceiver(this, frame.PageFaultCapacity, _resources.EpochId), "Read back Cluster page requests", _regions.Feedback);
        graph.OrderAfter(GraphData.PageFaultReadback, work);
    }

    private void RecordWorkSeed(ref RenderGraphEdit graph, in FrameResources frame, GraphBufferId packedViews,
        uint viewCount, uint viewIndexMode, string epochLabel, GraphRegionId region)
    {
        int bindingCount = 0;
        GraphPassId seedPass;
        {
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    packedViews,
                    ClusterViewUniforms.SizeInBytes),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
                CreateReadOnlyBufferView(graph, frame.Bvh)));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TraversalNodes0, TraversalNodeStride),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Partial,
                ResourceContentState.Defined,
                BufferContentScope.CountBoundedPrefix));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TraversalArguments0),
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Complete));
            GraphBufferSlice seedUniforms = UploadUniform(
                ref graph,
                new ClusterTraversalSeedUniforms
                {
                    ViewCount = viewCount,
                    ViewIndexMode = viewIndexMode,
                    MaxNodes = _options.MaxCandidates,
                    ArgsOffset = 0u,
                    UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
                    InvocationCountIndex = viewIndexMode == 0u ? 2u : _renderContext.InvocationCountIndex,
                },
                $"Cluster {epochLabel} traversal seed uniforms");
            if (viewIndexMode == 0u)
                GraphData.PrimarySeedUniforms = seedUniforms;
            else
                GraphData.TargetSeedUniforms = seedUniforms;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, seedUniforms),
                PipelineSync.ComputeShading));
            if (viewIndexMode == 0u)
            {
                seedPass = AddComputePass(
                    ref graph,
                    $"Seed Cluster {epochLabel} hierarchy roots",
                    _pipelines!.TraversalInitialize,
                    new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)),
                    bindingCount,
                    ClusterDispatchParameters.Record, region: region);
            }
            else
            {
                var indirect = new ClusterIndirectDispatchParameters(
                    RequireDispatchIndirectLayout(),
                    _renderContext.InvocationDispatchArguments,
                    0u);
                seedPass = AddComputePass(
                    ref graph,
                    $"Seed Cluster {epochLabel} hierarchy roots",
                    _pipelines!.TraversalInitialize,
                    indirect,
                    bindingCount,
                    static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                        _ = access.Read(
                            data.Arguments,
                            new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                            PipelineSync.ExecuteIndirect,
                            ResourceAccess.IndirectArgument),
                    ClusterIndirectDispatchParameters.Record, region: region);
            }
            PassDefinition seed = graph.GetPass(seedPass);
            if (viewIndexMode == 0u)
            {
                GraphData.PrimarySeed = new ClusterLayerDispatch(seedPass, 1, 1);
                BindNeutralInvocationCount(ref graph, ref seed);
            }
            else _renderContext.InvocationCountBinder(ref graph, ref seed, PipelineSync.ComputeShading);
            BindHierarchySeedResources(ref graph, seedPass, viewIndexMode != 0u);
            PassDefinition ready = graph.GetPass(seedPass);
            ready.Bind(graph.CreateBufferUav(frame.Work.NodeReady, structureStride: sizeof(uint)),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial);
            ready.Bind(graph.CreateBufferUav(frame.Work.ClusterReady, structureStride: sizeof(uint)),
                PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        }
    }

    private void RecordDeferredResume(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId drawArgs,
        string epochLabel,
        GraphRegionId region)
    {
        var reset = new ClusterDeferredResumeResetParameters(
            frame.CandidateCount,
            frame.CandidateArgsInitialization,
            frame.CandidateArgs,
            frame.TraversalArguments0,
            drawArgs);
        graph.AddCopyFramePass(
            $"Reset Cluster {epochLabel} deferred-resume outputs",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            reset,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            ClusterDeferredResumeResetParameters.Declare,
            ClusterDeferredResumeResetParameters.Record, region: region == default ? _regions.PrimaryGeometry : region);

        GraphBufferSlice uniforms = UploadUniform(
            ref graph,
            new ClusterDeferredResumeUniforms { MaxWorkItems = _options.MaxCandidates },
            $"Cluster {epochLabel} deferred-resume uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.Phase2CandidateClusters, CandidateStride),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.Phase2CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.TraversalNodes0, TraversalNodeStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.TraversalArguments0),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CandidateClusters, CandidateStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CandidateArgs),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Complete));
        var indirect = new ClusterIndirectDispatchParameters(
            RequireDispatchIndirectLayout(), frame.Phase2CandidateArgs, 0u);
        GraphPassId resume = AddComputePass(
            ref graph,
            $"Resume Cluster {epochLabel} deferred hierarchy work",
            _pipelines!.DeferredResume,
            indirect,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);
        PassDefinition pass = graph.GetPass(resume);
        BindWorkStatus(ref graph, ref pass);
        pass.Bind(graph.CreateBufferUav(frame.Work.NodeReady, structureStride: sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial);
        pass.Bind(graph.CreateBufferUav(frame.Work.ClusterReady, structureStride: sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
    }

    private void RecordCullStateReset(ref RenderGraphEdit graph, in FrameResources frame)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DrawArgs),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2DrawArgs),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        ClusterDispatchParameters clear = new(new DispatchArguments(1, 1, 1));
        _ = AddComputePass(ref graph, "Reset Cluster primary cull",
            _pipelines!.CullReset, clear, bindingCount, ClusterDispatchParameters.Record, region: _regions.PrimaryGeometry);
    }

    private RasterPhase RecordRasterPhase(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        RasterBin[] rasterBins,
        GraphBufferId drawArgs,
        GraphBufferId readOffsetArgs0,
        GraphBufferId readOffsetArgs1,
        GraphBufferId readOffsetArgs2,
        int epoch,
        bool phaseOne)
    {
        FrameResources raster = GetRasterFrameResources(frame, epoch >= 2);
        GraphRegionId parent = epoch switch
        {
            2 => GraphData.TargetFirstReference,
            3 => GraphData.TargetSecondReference,
            _ => GraphData.PrimaryGeometry,
        };
        GraphRegionId phase = graph.AddRegion(
            $"Cluster {(epoch < 2 ? "primary" : "target")} {(phaseOne ? "first" : "second")} raster", parent);
        // Shared scratch follows the region tree through ordinary resource accesses.
        GraphRegionId work = graph.AddRegion("Work binning", phase);
        GraphRegionId groups = graph.AddRegion("Tessellation groups", phase);
        GraphRegionId patches = graph.AddRegion("Final patch binning", phase);
        GraphRegionId bins = graph.AddRegion("Raster bins", phase);
        RecordRasterWorkGeneration(ref graph, raster, drawArgs,
            readOffsetArgs0, readOffsetArgs1, readOffsetArgs2, phaseOne, epoch, work);
        RecordTessellationReset(ref graph, raster, phaseOne, work);
        var data = new RasterPhase(phase,
            new GraphRegionMap<uint, RasterBin>(bins, RasterBinComparer.Instance),
            new GraphRegionMap<TessellationSplitKey, bool>(groups),
            ImmutableDictionary<TessellationSplitKey, TessellationGroup>.Empty,
            new GraphRegionMap<int, RasterInputs>(patches), phaseOne, epoch >= 2);
        data = ReconcileRasterPhase(ref graph, data, rasterBins);
        if (epoch < 2 && _visibilityMode != ClusterVisibilityMode.HardwareOnly)
            RecordWinnerResolve(ref graph, raster, phaseOne, graph.AddRegion("Visibility resolve", phase));
        return data;
    }

    private void RecordRasterWorkGeneration(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId drawArgs,
        GraphBufferId readOffsetArgs0,
        GraphBufferId readOffsetArgs1,
        GraphBufferId readOffsetArgs2,
        bool phaseOne,
        int epoch,
        GraphRegionId region)
    {
        GraphRegionId resetRegion = graph.AddRegion("Reset bins", region);
        GraphRegionId countRegion = graph.AddRegion("Count work", region);
        GraphRegionId reserveRegion = graph.AddRegion("Reserve bins", region);
        GraphRegionId scatterRegion = graph.AddRegion("Scatter work", region);
        GraphBufferSlice binningUniforms = UploadUniform(
            ref graph,
            new ClusterRasterBinningUniforms
            {
                RasterMaxBins = frame.RasterBinCount,
                SlotCapacity = frame.SlotCapacity,
                MaxVisibleClusters = _options.MaxCandidates,
                CollectMetrics = _options.EnableFrameMetricsReadback ? 1u : 0u,
                PolicyTarget = epoch >= 2 ? 1u : 0u,
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                ViewCount = frame.ViewCount,
                BinnedEntriesPerCluster = frame.BinnedEntriesPerCluster,
                DepthBucketsEnabled = frame.DepthBucketsEnabled ? 1u : 0u,
            },
            phaseOne
                ? "Cluster phase one raster binning uniforms"
                : "Cluster phase two raster binning uniforms");

        uint binClearGroups = Math.Max(
            1u,
            (frame.RasterBinCount + 63u) / 64u);

        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterWorkArguments),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading,
            _options.EnableFrameMetricsReadback ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
            _options.EnableFrameMetricsReadback ? WriteCoverage.Partial : WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Complete));
        ClusterDispatchParameters reset = new(
            new DispatchArguments(binClearGroups, 1, 1));
        GraphPassId resetPass = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one raster bins reset"
                : "Cluster phase two raster bins reset",
            _pipelines!.RasterBinReset,
            reset,
            bindingCount,
            ClusterDispatchParameters.Record, region: resetRegion);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(
                graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs0), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs1), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs2), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.RasterSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        GraphBufferId policyRasterWorkCounter = _renderContext.IsValid && epoch >= 2
            ? _renderContext.PolicyRasterWorkCounter : frame.NeutralPolicyCounter;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, policyRasterWorkCounter, sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.WorkStatus), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        ClusterIndirectDispatchParameters count = new(
            RequireDispatchIndirectLayout(), frame.RasterWorkArguments, 0);
        GraphPassId countPass = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one raster bins count"
                : "Cluster phase two raster bins count",
            _pipelines.RasterBinCount,
            count,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: countRegion);
        PassDefinition countAccess = graph.GetPass(countPass);
        if (_renderContext.IsValid && epoch >= 2 && _renderContext.BindWorkTargetViews is { } bindWorkViews)
            bindWorkViews(ref graph, ref countAccess, PipelineSync.ComputeShading);
        else
            _ = countAccess.Bind(graph.CreateBufferSrv(frame.NeutralPolicyView,
                structureStride: ClusterRenderContext.TargetViewSizeInBytes), PipelineSync.ComputeShading);
        if (_renderContext.IsValid && epoch >= 2 && _renderContext.BindDepthCacheClasses is { } bindDepthClasses)
            bindDepthClasses(ref graph, ref countAccess, PipelineSync.ComputeShading);
        else
            _ = countAccess.Bind(graph.CreateBufferSrv(frame.NeutralPolicyView,
                structureStride: sizeof(uint)), PipelineSync.ComputeShading);
        GraphBufferId depthWorkCounter = _renderContext.IsValid && epoch >= 2 && _renderContext.DepthWorkCounter != default
            ? _renderContext.DepthWorkCounter : frame.NeutralPolicyCounter;
        _ = countAccess.Bind(graph.CreateBufferUav(depthWorkCounter, structureStride: sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.HardwareIndirectArgs),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.SoftwareDispatchArgs),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.TessellationSeedDispatchArgs),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.WorkStatus), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        ClusterDispatchParameters reserve = new(new DispatchArguments(
            Math.Max(1u, (frame.RasterBinCount + 3u) / 4u), 1, 1));
        GraphPassId reservePass = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one raster bins reserve"
                : "Cluster phase two raster bins reserve",
            _pipelines.RasterBinReserve,
            reserve,
            bindingCount,
            ClusterDispatchParameters.Record, region: reserveRegion);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(
                graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.BinnedClusters, BinnedClusterStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedRanges));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs0), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs1), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs2), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.RasterSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.WorkStatus), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DepthBuckets, sizeof(uint)), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        ClusterIndirectDispatchParameters scatter = new(
            RequireDispatchIndirectLayout(), frame.RasterWorkArguments, ClusterRasterArguments.RasterScatterOffset);
        GraphPassId scatterPass = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one raster bins scatter"
                : "Cluster phase two raster bins scatter",
            _pipelines.RasterBinScatter,
            scatter,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: scatterRegion);
        GraphData.RasterBinning[epoch] = new RasterBinning(
            binningUniforms,
            resetPass,
            reservePass,
            epoch >= 2);

    }

    private GraphPassId RecordTessellationSeed(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterInvocation invocation,
        bool phaseOne,
        bool policyTarget,
        GraphRegionId region)
    {
        ClusterTessellationInvocation tessellation = invocation.Tessellation!;
        uint bin = invocation.BinIndex;
        GraphBufferSlice uniforms = UploadUniform(
            ref graph,
            CreateTessellationUniforms(frame, bin, phaseOne, 0u),
            phaseOne
                ? "Cluster phase one tessellation seed uniforms"
                : "Cluster phase two tessellation seed uniforms");

        ulong seedOffset = checked((ulong)bin * ClusterIndirectAbi.DispatchBytes);
        ClusterIndirectDispatchParameters seed = new(
            RequireDispatchIndirectLayout(),
            frame.TessellationSeedDispatchArgs,
            seedOffset);
        GraphPassId seedPass = AddComputePass(
            ref graph,
            phaseOne
                ? $"Cluster phase one tessellation seed bin {bin}"
                : $"Cluster phase two tessellation seed bin {bin}",
            tessellation.Seed,
            seed,
            0,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);
        PassDefinition seedDefinition = graph.GetPass(seedPass);
        BindTessellationResources(ref graph, ref seedDefinition, invocation,
            tessellation.SeedResources, uniforms, phaseOne, 0u,
            PipelineSync.ComputeShading, policyTarget);
        return seedPass;
    }

    private ClusterTessellationUniforms CreateTessellationUniforms(
        in FrameResources frame,
        uint bin,
        bool phaseOne,
        uint splitLevel) => new()
        {
            ScreenWidth = checked((uint)frame.Width),
            ScreenHeight = checked((uint)frame.Height),
            CurrentBin = bin,
            MaxPatchItems = _options.MaxCandidates,
            MaxFactor = _options.MaxTessellationFactor,
            TargetEdgePixels = _options.TessellationTargetEdgePixels,
            UsePreviousHiZ = phaseOne ? 1u : 0u,
            SplitLevel = splitLevel,
        };

    private void RecordTessellationReset(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne,
        GraphRegionId region)
    {
        ClusterTessellationResetParameters clear = new(frame.TessellationCounters);
        _ = graph.AddCopyFramePass(
            phaseOne
                ? "Reset Cluster phase one tessellation counters"
                : "Reset Cluster phase two tessellation counters",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            clear,
            default,
            static (ref PassDefinition access, ref ClusterTessellationResetParameters data) =>
            {
                _ = access.Write(
                    data.Counters,
                    BufferRange.Whole,
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            ClusterTessellationResetParameters.Record, region: region);
    }

    private GraphPassId RecordTessellationLevelReset(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        uint bin,
        bool phaseOne,
        GraphRegionId region)
    {
        ClusterTessellationLevelResetParameters clear = new(
            frame.TessellationLevelCounts,
            frame.TessellationLevelDispatchArgs);
        return graph.AddCopyFramePass(
            phaseOne
                ? $"Reset Cluster phase one tessellation level scratch bin {bin}"
                : $"Reset Cluster phase two tessellation level scratch bin {bin}",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            clear,
            default,
            static (ref PassDefinition access, ref ClusterTessellationLevelResetParameters data) =>
            {
                _ = access.Write(
                    data.Counts,
                    BufferRange.Whole,
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
                foreach (GraphBufferId arguments in data.DispatchArguments)
                {
                    _ = access.Write(
                        arguments,
                        BufferRange.Whole,
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                }
            },
            ClusterTessellationLevelResetParameters.Record, region: region);
    }

    private GraphPassId RecordPhaseOneTessellationCounters(
        ref RenderGraphEdit graph,
        in FrameResources frame, GraphRegionId region)
    {
        ClusterBufferCopyParameters copy = new(
            frame.TessellationCounters,
            0,
            frame.PhaseOneTessellationCounters,
            0,
            8 * sizeof(uint));
        return graph.AddCopyFramePass(
            "Copy Cluster phase one tessellation counters",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            copy,
            default,
            static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
            {
                _ = access.Read(
                    data.Source,
                    new BufferRange(data.SourceOffset, data.ByteCount),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource);
                _ = access.Write(
                    data.Destination,
                    new BufferRange(data.DestinationOffset, data.ByteCount),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            ClusterBufferCopyParameters.Record, region: region);
    }

    private void RecordTessellationBinning(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId sourcePatches,
        GraphBufferId destinationPatches,
        GraphBufferId sourceArguments,
        uint sourceCounterOffset,
        string label,
        GraphRegionId region)
    {
        uint binCount = checked(frame.RasterBinCount * 8u);
        GraphBufferSlice uniforms = UploadUniform(
            ref graph,
            new ClusterTessellationBinUniforms
            {
                BinCount = binCount,
                MaxPatchItems = _options.MaxCandidates,
                SourceCounterOffset = sourceCounterOffset,
            },
            $"Cluster tessellation bin uniforms {label}");

        int resetBindingCount = BindTessellationBinResources(
            ref graph,
            frame,
            uniforms,
            sourcePatches,
            destinationPatches,
            sourceArguments,
            TessellationBinningStage.Reset);
        ClusterDispatchParameters reset = new(new DispatchArguments(
            Math.Max(1u, (binCount + 63u) / 64u), 1u, 1u));
        GraphPassId resetPass = AddComputePass(
            ref graph,
            $"Reset Cluster tessellation bins {label}",
            _pipelines!.TessellationBinReset,
            reset,
            resetBindingCount,
            ClusterDispatchParameters.Record, region: region);

        int countBindingCount = BindTessellationBinResources(
            ref graph,
            frame,
            uniforms,
            sourcePatches,
            destinationPatches,
            sourceArguments,
            TessellationBinningStage.Count);
        ClusterIndirectDispatchParameters count = new(
            RequireDispatchIndirectLayout(), sourceArguments, 0u);
        GraphPassId countPass = AddComputePass(
            ref graph,
            $"Count Cluster tessellation bins {label}",
            _pipelines.TessellationBinCount,
            count,
            countBindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);

        int reserveBindingCount = BindTessellationBinResources(
            ref graph,
            frame,
            uniforms,
            sourcePatches,
            destinationPatches,
            sourceArguments,
            TessellationBinningStage.Reserve);
        ClusterDispatchParameters reserve = new(new DispatchArguments(
            Math.Max(1u, (binCount + 127u) / 128u), 1u, 1u));
        GraphPassId reservePass = AddComputePass(
            ref graph,
            $"Reserve Cluster tessellation bins {label}",
            _pipelines.TessellationBinReserve,
            reserve,
            reserveBindingCount,
            ClusterDispatchParameters.Record, region: region);

        int scatterBindingCount = BindTessellationBinResources(
            ref graph,
            frame,
            uniforms,
            sourcePatches,
            destinationPatches,
            sourceArguments,
            TessellationBinningStage.Scatter);
        ClusterIndirectDispatchParameters scatter = new(
            RequireDispatchIndirectLayout(), sourceArguments, 0u);
        GraphPassId scatterPass = AddComputePass(
            ref graph,
            $"Scatter Cluster tessellation bins {label}",
            _pipelines.TessellationBinScatter,
            scatter,
            scatterBindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);
        if (_rasterInputs is { } raster)
            raster.PatchBinning = new RasterBinning(
                uniforms,
                resetPass,
                reservePass,
                false);
    }

    private int BindTessellationBinResources(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferSlice uniforms,
        GraphBufferId sourcePatches,
        GraphBufferId destinationPatches,
        GraphBufferId sourceArguments,
        TessellationBinningStage stage)
    {
        int bindingCount = 0;
        bool reset = stage == TessellationBinningStage.Reset;
        bool count = stage == TessellationBinningStage.Count;
        bool reserve = stage == TessellationBinningStage.Reserve;
        bool scatter = stage == TessellationBinningStage.Scatter;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, reserve
            ? GraphParameterResourceBinding.Null(ResourceBindingType.BufferSrv)
            : GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.TessellationCounters), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, count || scatter
            ? GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, sourcePatches, TessellationFinalPatchStride),
                PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix)
            : GraphParameterResourceBinding.Null(ResourceBindingType.BufferSrv));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.TessellationBinCounts, sizeof(uint)),
            PipelineSync.ComputeShading,
            reset ? GraphAccessMode.Write : count ? GraphAccessMode.ReadWrite : GraphAccessMode.Read,
            reset ? WriteCoverage.Complete : WriteCoverage.Partial));
        AddBinding(ref bindingCount, count
            ? GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav)
            : GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TessellationBinOffsets, sizeof(uint)),
                PipelineSync.ComputeShading,
                scatter ? GraphAccessMode.Read : GraphAccessMode.Write,
                scatter ? WriteCoverage.Partial : WriteCoverage.Complete));
        AddBinding(ref bindingCount, count
            ? GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav)
            : GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TessellationBinScatterCounts, sizeof(uint)),
                PipelineSync.ComputeShading,
                scatter ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
                scatter ? WriteCoverage.Partial : WriteCoverage.Complete));
        AddBinding(ref bindingCount, reset || reserve
            ? GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TessellationBinReserveCounter, sizeof(uint)),
                PipelineSync.ComputeShading,
                reset ? GraphAccessMode.Write : GraphAccessMode.ReadWrite,
                reset ? WriteCoverage.Complete : WriteCoverage.Partial)
            : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, reset || reserve
            ? GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TessellationBinDispatchArgs),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete)
            : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, reset || reserve
            ? GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TessellationHardwareDrawArgs),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete)
            : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, reset
            ? GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, sourceArguments),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete)
            : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, scatter
            ? GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, destinationPatches, sizeof(uint)),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial,
                ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix)
            : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        AddBinding(ref bindingCount, count || reserve
            ? GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TessellationCounters),
                PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial)
            : GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        return bindingCount;
    }

    private enum TessellationBinningStage : byte
    {
        Reset,
        Count,
        Reserve,
        Scatter,
    }

    private void RecordTessellationSplit(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterInvocation invocation,
        bool phaseOne,
        bool policyTarget,
        bool sharedBins,
        GraphRegionId region)
    {
        ClusterTessellationInvocation tessellation = invocation.Tessellation ??
            throw new InvalidOperationException("A tessellation split invocation has no tessellation stages.");
        uint bin = invocation.BinIndex;
        for (uint level = 0u; level < (uint)TessellationSplitLevelCount; level++)
        {
            GraphBufferSlice uniforms = UploadUniform(
                ref graph,
                CreateTessellationUniforms(frame, sharedBins ? uint.MaxValue : bin, phaseOne, level),
                phaseOne
                    ? $"Cluster phase one tessellation split level {level} uniforms"
                    : $"Cluster phase two tessellation split level {level} uniforms");
            ClusterIndirectDispatchParameters split = new(
                RequireDispatchIndirectLayout(),
                frame.TessellationLevelDispatchArgs[level],
                0u);
            GraphPassId pass = AddComputePass(
                ref graph,
                phaseOne
                    ? $"Cluster phase one tessellation split bin {bin} level {level}"
                    : $"Cluster phase two tessellation split bin {bin} level {level}",
                tessellation.Split,
                split,
                0,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record, region: region);
            PassDefinition splitDefinition = graph.GetPass(pass);
            BindTessellationResources(ref graph, ref splitDefinition, invocation,
                tessellation.SplitResources, uniforms, phaseOne, level + 1u,
                PipelineSync.ComputeShading, policyTarget);
        }
    }

    private void RecordTessellationDice(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        IReadOnlyList<ClusterRasterInvocation> stages,
        bool phaseOne,
        bool policyTarget,
        GraphRegionId region)
    {
        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = stages[invocationIndex];
            if (invocation.Tessellation is not { } tessellation)
                continue;
            uint bin = invocation.BinIndex;
            GraphBufferSlice uniforms = UploadUniform(
                ref graph,
                CreateTessellationUniforms(frame, bin, phaseOne, 0u),
                phaseOne
                    ? "Cluster phase one dicing uniforms"
                    : "Cluster phase two dicing uniforms");
            ulong offset = checked((ulong)bin * ClusterIndirectAbi.DispatchBytes);
            ClusterIndirectDispatchParameters dice = new(
                RequireDispatchIndirectLayout(),
                frame.TessellationBinDispatchArgs,
                offset);
            ClusterComputePipeline dicePipeline = tessellation.Dice ??
                throw new InvalidOperationException(
                    "A recorded compute dicing pass has no compute pipeline.");
            GraphPassId dicePass = AddComputePass(
                ref graph,
                phaseOne
                    ? $"Cluster phase one bounded dicing bin {bin}"
                    : $"Cluster phase two bounded dicing bin {bin}",
                dicePipeline,
                dice,
                0,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record,
                asyncCompute: true, region: region);
            PassDefinition diceDefinition = graph.GetPass(dicePass);
            BindTessellationResources(ref graph, ref diceDefinition, invocation,
                tessellation.DiceResources!, uniforms, phaseOne, 0u,
                PipelineSync.ComputeShading, policyTarget);
        }
    }

    private void RecordTessellationHardwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        IReadOnlyList<ClusterRasterInvocation> stages,
        bool phaseOne,
        bool policyTarget,
        GraphRegionId region)
    {
        if (_visibilityMode != ClusterVisibilityMode.HardwareOnly)
            return;

        GraphTextureId visibilityTarget = policyTarget
            ? frame.PolicyVisibilityScratch
            : frame.VisBuffer;
        GraphTextureId depthTarget = policyTarget
            ? frame.PolicyDepthScratch
            : frame.Depth;
        uint targetLayers = policyTarget ? 1u : frame.ViewCount;
        if (visibilityTarget == default || depthTarget == default)
        {
            throw new InvalidOperationException(
                "Hardware-only tessellation requires a primary or policy scratch target.");
        }
        GraphColorAttachmentViewId visibilityView = graph.CreateColorAttachmentView(
            visibilityTarget,
            new TextureSubresourceRange(
                0,
                1,
                0,
                targetLayers,
                TextureAspects.Color),
            format: Format.R32UInt,
            dimension: TextureViewDimension.Texture2DArray,
            label: phaseOne
                ? "Cluster phase one tessellation hardware visibility view"
                : "Cluster phase two tessellation hardware visibility view");
        GraphDepthStencilViewId depthView = graph.CreateDepthStencilView(
            depthTarget,
            new TextureSubresourceRange(
                0,
                1,
                0,
                targetLayers,
                TextureAspects.Depth),
            Format.D32Float,
            TextureViewDimension.Texture2DArray,
            label: phaseOne
                ? "Cluster phase one tessellation hardware depth view"
                : "Cluster phase two tessellation hardware depth view");
        int rasterWidth = policyTarget
            ? checked((int)_renderContext.RasterWidth)
            : frame.Width;
        int rasterHeight = policyTarget
            ? checked((int)_renderContext.RasterHeight)
            : frame.Height;

        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = stages[invocationIndex];
            if (invocation.Tessellation is not { } tessellation)
                continue;
            ClusterRasterPipeline pipeline = tessellation.Hardware ??
                throw new InvalidOperationException(
                    "Hardware-only tessellation has no fixed-function raster pipeline.");
            uint bin = invocation.BinIndex;
            GraphBufferSlice uniforms = UploadUniform(
                ref graph,
                CreateTessellationUniforms(frame, bin, phaseOne, 0u),
                phaseOne
                    ? "Cluster phase one tessellation hardware uniforms"
                    : "Cluster phase two tessellation hardware uniforms");
            ulong offset = pipeline.UsesMesh
                ? checked((ulong)frame.RasterBinCount * 8u * ClusterIndirectAbi.DrawIndexedBytes +
                    (ulong)bin * ClusterIndirectAbi.DispatchBytes)
                : checked((ulong)bin * 8u * ClusterIndirectAbi.DrawIndexedBytes);
            ClusterTessellationRasterPassData passData = new(
                _drawIndexedIndirectLayout ?? throw new InvalidOperationException("Tessellation indexed drawing is unavailable."),
                frame.TessellationHardwareDrawArgs,
                offset,
                frame.TessellationPatternIndices,
                frame.TessellationWorkPatches,
                rasterWidth,
                rasterHeight,
                pipeline.UsesMesh);
            GraphBufferAccessId indirectAccess = default;
            GraphPassId hardwarePass = AddRasterPass(
                ref graph,
                phaseOne
                    ? $"Cluster phase one tessellation hardware bin {bin}"
                    : $"Cluster phase two tessellation hardware bin {bin}",
                pipeline,
                passData,
                0,
                (ref PassDefinition access, ref ClusterTessellationRasterPassData data) =>
                {
                    access.ColorAttachment(
                        0,
                        visibilityView,
                        LoadType.Load,
                        StoreType.Store,
                        WriteCoverage.Partial,
                        default);
                    access.DepthStencilAttachment(
                        depthView,
                        LoadType.Load,
                        StoreType.Store,
                        WriteCoverage.Partial,
                        1f,
                        LoadType.Discard,
                        StoreType.Discard,
                        WriteCoverage.Complete,
                        0);
                    indirectAccess = ClusterTessellationRasterPassData.Declare(ref access, ref data);
                },
                ClusterTessellationRasterPassData.Record, region: region);
            if (pipeline.UsesMesh)
                _rasterInputs!.MeshDraw = new MeshRaster(hardwarePass, bin, indirectAccess, passData);
            PassDefinition hardwareDefinition = graph.GetPass(hardwarePass);
            BindTessellationResources(ref graph, ref hardwareDefinition, invocation,
                tessellation.HardwareResources!, uniforms, phaseOne, 0u,
                PipelineSync.AllShading, policyTarget);
        }
    }

    private void RecordSoftwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        IReadOnlyList<ClusterRasterInvocation> stages,
        GraphBufferId packedViews,
        bool phaseOne,
        bool policyTarget,
        GraphRegionId region)
    {
        if (_visibilityMode == ClusterVisibilityMode.HardwareOnly)
            return;

        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = stages[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferSlice pushConstants = UploadUniform(
                ref graph,
                new ClusterSoftwareRasterUniforms
                {
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                    MaxBins = frame.RasterBinCount,
                    DebugDump = _options.EnableFrameMetricsReadback ? 1u : 0u,
                    CurrentBin = bin,
                    EarlyDepthEnabled = invocation.UsesEarlyDepth ? 1u : 0u,
                },
                phaseOne
                    ? "Cluster phase one software raster bin uniforms"
                    : "Cluster phase two software raster bin uniforms");
            if (_rasterInputs is { } raster) raster.SoftwareUniforms = pushConstants;
            ulong indirectOffset = checked((ulong)bin * ClusterIndirectAbi.DispatchBytes);
            int bindingCount = 0;
            ClusterSoftwareRasterPassData passData = new(
                RequireDispatchIndirectLayout(),
                frame.SoftwareDispatchArgs,
                indirectOffset);
            ClusterComputePipeline softwarePipeline = (policyTarget ? invocation.TargetStages?.Software : null) ?? invocation.Software ??
                throw new InvalidOperationException(
                    "A recorded software-raster pass has no software pipeline.");
            // Both raster paths write the same exclusive visibility image.
            GraphPassId softwarePass = AddComputePass(
                ref graph,
                phaseOne
                    ? "Cluster phase one software raster bin"
                    : "Cluster phase two software raster bin",
                softwarePipeline,
                passData,
                bindingCount,
                ClusterSoftwareRasterPassData.Declare,
                ClusterSoftwareRasterPassData.Record, region: region);
            PassDefinition softwareDefinition = graph.GetPass(softwarePass);
            BindSoftwareRasterResources(ref graph, ref softwareDefinition, invocation,
                pushConstants, policyTarget);
        }
    }

    private GraphPassId RecordWinnerResolve(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne,
        GraphRegionId region)
    {
        if (_visibilityMode == ClusterVisibilityMode.HardwareOnly)
            throw new InvalidOperationException("Hardware-only visibility does not use a winner resolve pass.");
        GraphColorAttachmentViewId visibilityView = graph.CreateColorAttachmentView(
            frame.VisBuffer,
            new TextureSubresourceRange(
                0,
                1,
                0,
                frame.ViewCount,
                TextureAspects.Color),
            format: Format.R32UInt,
            dimension: TextureViewDimension.Texture2DArray,
            label: phaseOne
                ? "Cluster phase one visibility resolve view"
                : "Cluster phase two visibility resolve view");
        GraphDepthStencilViewId depthView = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(
                0,
                1,
                0,
                frame.ViewCount,
                TextureAspects.Depth),
            Format.D32Float,
            TextureViewDimension.Texture2DArray,
            label: phaseOne
                ? "Cluster phase one winner depth view"
                : "Cluster phase two winner depth view");
        ClusterRasterPipeline pipeline = _pipelines!.WinnerResolve
            ?? throw new InvalidOperationException("The selected visibility mode has no winner resolve pipeline.");
        GraphBufferSlice uniforms = UploadUniform(
            ref graph,
            new ClusterVisibilityWinnerUniforms
            {
                Width = checked((uint)frame.Width),
                Height = checked((uint)frame.Height),
                ViewCount = frame.ViewCount,
            },
            phaseOne
                ? "Cluster phase one visibility winner resolve uniforms"
                : "Cluster phase two visibility winner resolve uniforms");
        int bindingCount = 0;
        if (_visibilityMode == ClusterVisibilityMode.ImageAtomic64)
        {
            AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
                CreateSampledTextureView(
                    graph,
                    frame.VisibilityWinnerImage,
                    Format.R64UInt,
                    null,
                    TextureViewDimension.Texture2DArray),
                PipelineSync.PixelShading));
        }
        else
        {
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.VisibilityWinnerBuffer),
                PipelineSync.PixelShading));
        }
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.PixelShading));
        if (!phaseOne)
        {
            ClusterHardwareRasterPassData indirect = new(
                RequireDrawIndirectLayout(),
                frame.RasterWorkArguments,
                ClusterRasterArguments.VisibilityResolveOffset,
                frame.Width,
                frame.Height,
                UsesMesh: false);
            return AddRasterPass(
                ref graph,
                "Cluster phase two visibility winner resolve",
                pipeline,
                indirect,
                bindingCount,
                (ref PassDefinition access, ref ClusterHardwareRasterPassData data) =>
                {
                    DeclareTargets(ref access);
                    ClusterHardwareRasterPassData.Declare(ref access, ref data);
                },
                ClusterHardwareRasterPassData.Record, region: region);
        }
        ClusterFullscreenParameters passData = new(frame.Width, frame.Height, 1);
        GraphPassId pass = AddRasterPass(
            ref graph,
            "Cluster phase one visibility winner resolve",
            pipeline,
            passData,
            bindingCount,
            (ref PassDefinition access, ref ClusterFullscreenParameters _) =>
                DeclareTargets(ref access),
            ClusterFullscreenParameters.Record, region: region);
        GraphData.WinnerDispatches.Add(new ClusterLayerDispatch(pass, checked((uint)frame.Width), checked((uint)frame.Height)));
        return pass;

        void DeclareTargets(ref PassDefinition access)
        {
            LoadType load = phaseOne ? LoadType.Discard : LoadType.Load;
            WriteCoverage coverage = phaseOne ? WriteCoverage.Complete : WriteCoverage.Partial;
            access.ColorAttachment(
                0,
                visibilityView,
                load,
                StoreType.Store,
                coverage,
                default);
            access.DepthStencilAttachment(
                depthView,
                load,
                StoreType.Store,
                coverage,
                1f,
                LoadType.Discard,
                StoreType.Discard,
                WriteCoverage.Complete,
                0);
        }
    }

    private void RecordHardwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        IReadOnlyList<ClusterRasterInvocation> stages,
        GraphBufferId packedViews,
        bool phaseOne,
        bool policyTarget,
        GraphRegionId region)
    {
        bool fixedFunctionVisibility = _visibilityMode == ClusterVisibilityMode.HardwareOnly;
        GraphTextureId visibilityTarget = policyTarget
            ? frame.PolicyVisibilityScratch
            : frame.VisBuffer;
        GraphTextureId depthTarget = policyTarget
            ? frame.PolicyDepthScratch
            : frame.Depth;
        uint targetLayers = policyTarget ? 1u : frame.ViewCount;
        if (fixedFunctionVisibility &&
            (visibilityTarget == default || depthTarget == default))
        {
            throw new InvalidOperationException(
                "Hardware-only rasterization requires a primary or policy scratch target.");
        }
        GraphColorAttachmentViewId visView = fixedFunctionVisibility
            ? graph.CreateColorAttachmentView(
                visibilityTarget,
                new TextureSubresourceRange(
                    0,
                    1,
                    0,
                    targetLayers,
                    TextureAspects.Color),
                format: Format.R32UInt,
                dimension: TextureViewDimension.Texture2DArray,
                label: phaseOne
                    ? "Cluster phase one hardware visibility view"
                    : "Cluster phase two hardware visibility view")
            : default;
        GraphDepthStencilViewId depthView = fixedFunctionVisibility
            ? graph.CreateDepthStencilView(
                depthTarget,
                new TextureSubresourceRange(
                    0,
                    1,
                    0,
                    targetLayers,
                    TextureAspects.Depth),
                Format.D32Float,
                TextureViewDimension.Texture2DArray,
                label: phaseOne
                    ? "Cluster phase one hardware depth view"
                    : "Cluster phase two hardware depth view")
            : default;
        int rasterWidth = policyTarget
            ? checked((int)_renderContext.RasterWidth)
            : frame.Width;
        int rasterHeight = policyTarget
            ? checked((int)_renderContext.RasterHeight)
            : frame.Height;
        GraphBufferSlice drawUniforms = UploadUniform(
            ref graph,
            new ClusterDrawUniforms
            {
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                RasterWidth = checked((uint)rasterWidth),
                RasterHeight = checked((uint)rasterHeight),
            },
            phaseOne
                ? "Cluster phase one hardware draw uniforms"
                : "Cluster phase two hardware draw uniforms");

        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = stages[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferSlice dispatchUniforms = UploadUniform(
                ref graph,
                new ClusterDrawDispatchUniforms
                {
                    DrawArgsByteOffset = checked(bin * ClusterIndirectAbi.HardwareRasterStride),
                    EarlyDepthEnabled = invocation.UsesEarlyDepth ? 1u : 0u,
                },
                phaseOne
                    ? "Cluster phase one hardware bin dispatch uniforms"
                    : "Cluster phase two hardware bin dispatch uniforms");
            ulong offset = checked((ulong)bin * ClusterIndirectAbi.HardwareRasterStride);
            ClusterRasterPipeline? targetPipeline = policyTarget ? invocation.TargetStages?.Hardware : null;
            ClusterRasterPipeline pipeline = targetPipeline ?? invocation.Hardware;
            bool attachVisibility = fixedFunctionVisibility && targetPipeline is null;
            int bindingCount = 0;
            ClusterHardwareRasterPassData passData = new(
                RequireDrawIndirectLayout(),
                frame.HardwareIndirectArgs,
                offset,
                rasterWidth,
                rasterHeight,
                pipeline.UsesMesh);
            GraphPassId hardwarePass = AddRasterPass(
                ref graph,
                phaseOne
                    ? "Cluster phase one hardware visibility bin"
                    : "Cluster phase two hardware visibility bin",
                pipeline,
                passData,
                bindingCount,
                (ref PassDefinition access, ref ClusterHardwareRasterPassData data) =>
                {
                    if (attachVisibility)
                    {
                        access.ColorAttachment(
                            0,
                            visView,
                            LoadType.Load,
                            StoreType.Store,
                            WriteCoverage.Partial,
                            default);
                        access.DepthStencilAttachment(
                            depthView,
                            LoadType.Load,
                            StoreType.Store,
                            WriteCoverage.Partial,
                            1f,
                            LoadType.Discard,
                            StoreType.Discard,
                            WriteCoverage.Complete,
                            0);
                    }
                    ClusterHardwareRasterPassData.Declare(ref access, ref data);
                },
                ClusterHardwareRasterPassData.Record,
                attachVisibility
                    ? default
                    : new PassOptions(
                        RasterWidth: checked((uint)rasterWidth),
                        RasterHeight: checked((uint)rasterHeight)), region: region);
            PassDefinition hardwareDefinition = graph.GetPass(hardwarePass);
            BindHardwareRasterResources(ref graph, ref hardwareDefinition, invocation,
                drawUniforms, dispatchUniforms, policyTarget);
        }
    }

    private GraphPassId RecordHiZ(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne, GraphRegionId region)
    {
        int mipCount = ClusterCullingHistory.CalculateMipCount(frame.Width, frame.Height);
        GraphAccessMode outputAccess = phaseOne ? GraphAccessMode.Write : GraphAccessMode.ReadWrite;
        WriteCoverage outputCoverage = phaseOne ? WriteCoverage.Complete : WriteCoverage.Partial;
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(graph, frame.Depth, Format.D32Float,
                new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth),
                TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading));
        for (int mip = 0; mip < 5; mip++)
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(graph, frame.CurrentHiZ, Format.R32Float,
                    Mip(Math.Min(mip, mipCount - 1), frame.ViewCount), TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading, outputAccess, outputCoverage));

        uint firstWidth = checked((uint)MipExtent(frame.Width, 4));
        uint firstHeight = checked((uint)MipExtent(frame.Height, 4));
        GraphPassId firstPass = RecordHiZDispatch(ref graph, frame,
            phaseOne ? "Build Cluster phase one HiZ mips 0-4" : "Build Cluster final HiZ mips 0-4",
            _pipelines!.HiZFirst, bindingCount, firstWidth, firstHeight,
            phaseOne, ClusterRasterArguments.HiZFirstOffset, region);

        GraphPassId previousPass = firstPass;
        int sourceMip = Math.Min(4, mipCount - 1);
        while (sourceMip + 1 < mipCount)
        {
            // The tail shader keeps at most 64 by 64 first-level values in
            // group memory. Larger inputs retain parallel two-level reduction.
            bool tail = MipExtent(frame.Width, sourceMip + 1) <= 64 &&
                MipExtent(frame.Height, sourceMip + 1) <= 64;
            int outputCount = tail ? 7 : 2;
            bindingCount = 0;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(graph, frame.CurrentHiZ, Format.R32Float,
                    Mip(sourceMip, frame.ViewCount), TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading, GraphAccessMode.Read));
            for (int output = 0; output < outputCount; output++)
                AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                    CreateStorageTextureView(graph, frame.CurrentHiZ, Format.R32Float,
                        Mip(Math.Min(sourceMip + 1 + output, mipCount - 1), frame.ViewCount),
                        TextureViewDimension.Texture2DArray),
                    PipelineSync.ComputeShading, outputAccess, outputCoverage));

            int destinationMip = Math.Min(sourceMip + outputCount, mipCount - 1);
            uint dispatchWidth = tail ? 1u : Groups(MipExtent(frame.Width, destinationMip), 8);
            uint dispatchHeight = tail ? 1u : Groups(MipExtent(frame.Height, destinationMip), 8);
            GraphPassId pass = RecordHiZDispatch(ref graph, frame,
                $"Build Cluster {(phaseOne ? "phase one" : "final")} HiZ {(tail ? "tail" : "mip pair")}",
                tail ? _pipelines.HiZDownsample : _pipelines.HiZDownsampleTwo,
                bindingCount, dispatchWidth, dispatchHeight, phaseOne,
                tail ? ClusterRasterArguments.HiZTailOffset : ClusterRasterArguments.HiZPairOffset(destinationMip), region);
            previousPass = pass;
            sourceMip = destinationMip;
        }
        return previousPass;
    }

    private GraphPassId RecordHiZDispatch(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        string label,
        ClusterComputePipeline pipeline,
        int bindingCount,
        uint width,
        uint height,
        bool phaseOne,
        ulong argumentOffset, GraphRegionId region)
    {
        if (phaseOne)
        {
            GraphPassId pass = AddComputePass(ref graph, label, pipeline,
                new ClusterDispatchParameters(new DispatchArguments(width, height, 1)),
                bindingCount, ClusterDispatchParameters.Record, region: region);
            GraphData.LayerDispatches.Add(new ClusterLayerDispatch(pass, width, height));
            return pass;
        }
        ClusterIndirectDispatchParameters indirect = new(
            RequireDispatchIndirectLayout(), frame.RasterWorkArguments, argumentOffset);
        return AddComputePass(ref graph, label, pipeline, indirect, bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);
    }

    private GraphPassId RecordShadeCount(ref RenderGraphEdit graph, in FrameResources frame,
        GraphBufferSlice binUniforms, bool writeReceivers)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(
                graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.VisBuffer,
                Format.R32UInt,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.ShadeSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.PixelCoordinates, 3 * sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedRanges));
        if (writeReceivers)
        {
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.SurfaceReceivers, SurfaceReceiver.SizeInBytes),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.AdditionalSurfaceReceivers, SurfaceReceiver.SizeInBytes),
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        }
        else
        {
            AddBinding(ref bindingCount, GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav));
        }
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.SurfaceCounts, sizeof(uint)),
            PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        ClusterDispatchParameters count = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphPassId countPass = AddComputePass(
            ref graph,
            "Count Cluster shade bins",
            _pipelines!.GetShadeBinCount(writeReceivers), count, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true, region: GraphData.ShadeCountRegion);
        GraphData.LayerDispatches.Add(new ClusterLayerDispatch(
            countPass,
            Groups(frame.Width, 8),
            Groups(frame.Height, 8)));

        return countPass;
    }

    private void RecordShade(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterShadeStageSet shadeStages)
    {
        GraphRegionId receivers = _surfaceReceiverRegion != default ? _surfaceReceiverRegion : _regions.Shading;
        GraphData.ShadeBinning = graph.AddRegion("Cluster shade binning", receivers);
        GraphData.ReceiverGroups = new GraphRegionMap<uint, ShadeGroup>(graph.AddRegion("Cluster surface receiver materials", receivers), ShadeGroupComparer.Instance);
        GraphData.ShadeGroups = new GraphRegionMap<uint, ShadeGroup>(graph.AddRegion("Cluster shade materials", _regions.Shading), ShadeGroupComparer.Instance);
        GraphRegionId resolveRegion = graph.AddRegion("Cluster background resolve", _regions.Shading);
        GraphBufferSlice binUniforms = UploadUniform(
            ref graph,
            new ClusterShadeBinUniforms
            {
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                ShadeBinCount = frame.ShadeBinCount,
                SlotCapacity = frame.SlotCapacity,
                BinItemCapacity = CalculateShadeBinItemCapacity(frame),
                AdditionalReceiverLayerCount = frame.AdditionalReceiverLayerCount,
                ViewCount = frame.ViewCount,
                SurfaceDataStride = frame.SurfaceDataStride,
            },
            "Cluster shade bin uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.SurfaceCounts, sizeof(uint)),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        ClusterDispatchParameters clear = new(new DispatchArguments(
            Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        GraphPassId clearPass = AddComputePass(ref graph, "Clear Cluster shade bins",
            _pipelines!.ShadeBinClearPrepare, clear, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true, region: GraphData.ShadeBinning);

        GraphData.ShadeCountRegion = graph.AddRegion("Cluster shade count", GraphData.ShadeBinning);
        GraphData.ShadeCountPass = RecordShadeCount(ref graph, frame, binUniforms, shadeStages.WritesReceivers);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Read,
            WriteCoverage.Partial));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinOffsets, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeIndirectArgs),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.SurfaceCounts, sizeof(uint)), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            graph.CreateBufferSrv(frame.SurfaceDataSizes, structureStride: sizeof(uint)), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.SurfaceDataOffsets, sizeof(uint)),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.SurfaceScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete));
        ClusterDispatchParameters reserve = new(new DispatchArguments(
            Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        GraphPassId reservePass = AddComputePass(ref graph, "Reserve Cluster shade bins",
            _pipelines.ShadeBinReserve, reserve, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: false, region: GraphData.ShadeBinning);
        GraphData.ShadeBinUniforms = binUniforms;
        GraphData.ShadeBinDispatches = [new(clearPass, 1, 1), new(reservePass, 1, 1)];

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(
                graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.VisBuffer,
                Format.R32UInt,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinOffsets, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Read,
            WriteCoverage.Partial));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.ShadeSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.PixelCoordinates, 3 * sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedRanges));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.SurfaceScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial));
        ClusterDispatchParameters scatter = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphData.ShadeScatter = graph.AddRegion("Cluster shade scatter", GraphData.ShadeBinning);
        GraphPassId scatterPass = AddComputePass(ref graph, "Scatter Cluster shade bins",
            _pipelines.ShadeBinScatter, scatter, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true, region: GraphData.ShadeScatter);
        GraphData.LayerDispatches.Add(new ClusterLayerDispatch(
            scatterPass,
            Groups(frame.Width, 8),
            Groups(frame.Height, 8)));


        {

            // Material bins define geometry pixels; this final write fills every background pixel.
            // Together they define the complete color and motion outputs while retaining the bin writes.
            GraphBufferSlice resolveUniforms = UploadUniform(
                ref graph,
                new ClusterResolveUniforms
                {
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                },
                "Cluster resolve uniforms");
            bindingCount = 0;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, resolveUniforms), PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
                CreateSampledTextureView(
                    graph,
                    frame.VisBuffer,
                    Format.R32UInt,
                    null,
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.SceneColor,
                    Format.R16G16B16A16Float,
                    null,
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Partial,
                ResourceContentState.Defined));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.MotionVectors,
                    Format.R16G16Float,
                    null,
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Partial,
                ResourceContentState.Defined));
            ClusterDispatchParameters resolve = new(new DispatchArguments(
                Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
            GraphPassId resolvePass = AddComputePass(ref graph, "Resolve Cluster visibility background",
                _pipelines.Resolve, resolve, bindingCount,
                ClusterDispatchParameters.Record, region: resolveRegion);
            GraphData.LayerDispatches.Add(new ClusterLayerDispatch(
                resolvePass,
                Groups(frame.Width, 8),
                Groups(frame.Height, 8)));


        }

        GraphData.ShadeGroups = UpdateShadeGroups(ref graph, shadeStages);
    }
    private GraphPassId AddRasterPass<TState>(
        ref RenderGraphEdit graph,
        string name,
        ClusterRasterPipeline pipeline,
        in TState state,
        int bindingCount,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback,
        PassOptions options = default,
        GraphRegionId region = default)
    {
        Span<GraphParameterResourceBinding> bindings =
            _renderGraphBindings.AsSpan(0, bindingCount);
        try
        {
            GraphPassId pass = graph.AddRasterFramePass(
                name,
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                state,
                options with { RepeatCommands = true },
                pipeline.Pipeline,
                pipeline.ParameterLayout,
                bindings,
                declaration,
                callback, region: region == default ? _regions.PrimaryGeometry : region);
            return pass;
        }
        finally
        {
            bindings.Clear();
        }
    }

    private GraphPassId AddRasterPass<TState>(
        ref RenderGraphEdit graph,
        string name,
        ClusterRasterPipeline pipeline,
        in TState state,
        int bindingCount,
        RasterFrameCallback<TState> callback,
        PassOptions options = default,
        GraphRegionId region = default) =>
        AddRasterPass(ref graph, name, pipeline, state, bindingCount,
            DeclareNoAdditionalAccess<TState>, callback, options, region);

    private GraphPassId AddComputePass<TState>(
        ref RenderGraphEdit graph,
        string name,
        ClusterComputePipeline pipeline,
        in TState state,
        int bindingCount,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback,
        bool asyncCompute = false,
        GraphRegionId region = default)
    {
        Span<GraphParameterResourceBinding> bindings =
            _renderGraphBindings.AsSpan(0, bindingCount);
        try
        {
            GraphPassId pass = graph.AddComputeFramePass(
                name,
                // Optional asynchronous work may stay with its producers. Requiring a
                // compute queue here adds transfers even when the dependency chain is serial.
                _options.EnableAsyncCompute && asyncCompute
                    ? PassQueueSelection.AnyCompatible()
                    : PassQueueSelection.AnyOfType(QueueType.Graphics),
                state,
                new PassOptions(RepeatCommands: true),
                pipeline.Pipeline,
                pipeline.ParameterLayout,
                bindings,
                declaration,
                callback, region: region == default ? _regions.PrimaryGeometry : region);
            return pass;
        }
        finally
        {
            bindings.Clear();
        }
    }

    private GraphPassId AddComputePass<TState>(
        ref RenderGraphEdit graph,
        string name,
        ClusterComputePipeline pipeline,
        in TState state,
        int bindingCount,
        ComputeFrameCallback<TState> callback,
        bool asyncCompute = false,
        GraphRegionId region = default) =>
        AddComputePass(ref graph, name, pipeline, state, bindingCount,
            DeclareNoAdditionalAccess<TState>, callback, asyncCompute, region);

    private void AddBinding(ref int count, in GraphParameterResourceBinding binding)
    {
        if ((uint)count >= (uint)_renderGraphBindings.Length)
            throw new InvalidOperationException("A Cluster Pass exceeds the binding scratch capacity.");
        _renderGraphBindings[count++] = binding;
    }

    private void AddPageHeapBinding(ref int count, GraphBufferSrvId view)
    {
        AddBinding(
            ref count,
            GraphParameterResourceBinding.ReadOnlyBufferDescriptor(view));
    }

    private static void DeclareNoAdditionalAccess<TState>(
        ref PassDefinition access,
        ref TState state)
    {
    }

    private static GraphTextureSrvId CreateSampledTextureView(
        RenderGraphEdit graph,
        GraphTextureId texture,
        Format? format,
        TextureSubresourceRange? range,
        TextureViewDimension? dimension = null) =>
        graph.CreateTextureSrv(texture, range, format, dimension);

    private static GraphTextureUavId CreateStorageTextureView(
        RenderGraphEdit graph,
        GraphTextureId texture,
        Format format,
        TextureSubresourceRange? range,
        TextureViewDimension? dimension = null) =>
        graph.CreateTextureUav(texture, range, format, dimension);

    private static GraphTextureSrvId CreateSampledTextureView(RenderGraphEdit graph,
        GraphTextureInput texture, Format? format, TextureSubresourceRange? range,
        TextureViewDimension? dimension = null) => range is { } value
            ? graph.CreateTextureSrv(texture, value, format!.Value, dimension!.Value)
            : graph.CreateTextureSrv(texture, format!.Value, dimension!.Value);

    private static GraphTextureUavId CreateStorageTextureView(RenderGraphEdit graph,
        GraphTextureInput texture, Format format, TextureSubresourceRange? range,
        TextureViewDimension? dimension = null) => graph.CreateTextureUav(texture, range!.Value, format, dimension!.Value);

    private static GraphBufferCbvId CreateConstantBufferView(
        RenderGraphEdit graph,
        GraphBufferSlice input) => graph.CreateBufferCbv(input.Buffer, input.Range);

    private static GraphBufferCbvId CreateConstantBufferView(
        RenderGraphEdit graph,
        GraphBufferId buffer,
        BufferRange? range = null) =>
        graph.CreateBufferCbv(buffer, range);

    private static GraphBufferSrvId CreateReadOnlyBufferView(
        RenderGraphEdit graph,
        GraphBufferId buffer,
        uint stride = 0,
        BufferRange? range = null) =>
        graph.CreateBufferSrv(buffer, range, structureStride: stride);

    private static GraphBufferUavId CreateStorageBufferView(
        RenderGraphEdit graph,
        GraphBufferId buffer,
        uint stride = 0,
        BufferRange? range = null) =>
        graph.CreateBufferUav(buffer, range, structureStride: stride);

    private static TextureSubresourceRange Mip(int mip, uint viewCount)
        => new(checked((uint)mip), 1, 0, viewCount, TextureAspects.Color);

    private static int MipExtent(int extent, int mip)
        => Math.Max(1, extent >> mip);

    private static uint Groups(int extent, int groupSize)
        => checked((uint)((extent + groupSize - 1) / groupSize));

    private static void Read(
        ref PassDefinition access,
        GraphBufferId buffer,
        ulong offset,
        ulong size) =>
        _ = access.Read(buffer, new BufferRange(offset, size),
            PipelineSync.Copy, ResourceAccess.CopySource);

}
