using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly GraphParameterResourceBinding[] _renderGraphBindings =
        new GraphParameterResourceBinding[96];

    private void InstallGraph(
        ref RenderGraphEdit graph,
        in ClusterPipelineOutputDescription output,
        ClusterMaterialAssignmentSet assignments,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages)
    {
        FrameResources frame = CreateFrameResources(
            ref graph,
            in output,
            assignments,
            rasterStages,
            shadeStages);
        _frameResources = frame;
        AuthorHistoryInitialization(ref graph, frame);
        GraphBufferId packedViews = graph.DeclareExternalBuffer(new BufferDesc(
            checked((ulong)_packedViews.Length * ClusterViewUniforms.SizeInBytes),
            BufferUsages.ShaderRead, "Cluster packed views"), MemoryType.Upload);
        frame.PackedViews = packedViews;
        _frameResources = frame;

        RecordFrameMetricsSourceReset(ref graph, frame);
        InstallSceneBounds(ref graph);
        _traversalPass = RecordTraversal(
            ref graph,
            frame,
            packedViews,
            frame.ViewCount,
            viewIndexMode: 0u,
            traversalMode: 0u,
            seedRoots: true,
            startAfter: default,
            _traversalLevelPasses,
            "primary",
            out _traversalSeedPass);
        graph.OrderAfter(_traversalSeedPass, _sceneBoundsPass);
        RecordStageRegion(
            ref graph,
            frame,
            rasterStages,
            packedViews,
            shadeStages,
            installCommonPasses: true);
        RecordContinuation(
            ref graph,
            frame,
            rasterStages,
            packedViews);
        BindTargetPolicyResources(ref graph);
        RecordPageFaultReadback(ref graph, frame);
        RecordFrameMetricsReadback(ref graph, frame);
        _outputs = new ClusterPipelineOutputs(
            frame.VisBuffer,
            frame.Depth,
            frame.SceneColor,
            frame.MotionVectors,
            new ClusterGeometryCapabilities(
                frame.PackedViews,
                frame.InstanceData,
                frame.InstanceProperties,
                frame.InstancePropertiesRange,
                frame.VisibleClusters,
                frame.DrawArgs,
                frame.Phase2DrawArgs,
                _options.MaxCandidates,
                frame.PageHeap,
                frame.Bvh,
                _phaseTwoHiZPass)
            {
                InstanceWorldBounds = _instanceWorldBounds,
                BoundsReady = _sceneBoundsPass,
            },
            frame.Width,
            frame.Height,
            frame.ViewCount);
    }

    private void RecordStageRegion(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        ClusterShadeStageSet shadeStages,
        bool installCommonPasses)
    {

        if (installCommonPasses)
        {
            _phaseOneCullPass = RecordCullPhaseOne(
                ref graph,
                frame,
                packedViews,
                frame.DrawArgs,
                frame.ReadOffsetZero,
                frame.ReadOffsetZero,
                resetPrimaryCullState: true,
                "primary");
            graph.OrderAfter(_phaseOneCullPass, _traversalPass);
        }
        RecordRasterPhase(
            ref graph,
            frame,
            rasterStages,
            packedViews,
            frame.DrawArgs,
            frame.ReadOffsetZero,
            frame.ReadOffsetZero,
            frame.ReadOffsetZero,
            epoch: 0,
            cullBoundary: _phaseOneCullPass,
            phaseOne: true,
            installCommonPasses);
        if (installCommonPasses)
        {
            _phaseOneHiZPass = RecordHiZ(ref graph, frame, phaseOne: true);
            OrderAfterRasterPhase(ref graph, _phaseOneHiZPass, phase: 0);
        }

        GraphPassId phaseOneTessellationSnapshot;

        phaseOneTessellationSnapshot = RecordPhaseOneTessellationCounterSnapshot(ref graph, frame);
        OrderAfterRasterPhase(ref graph, phaseOneTessellationSnapshot, phase: 0);


        if (installCommonPasses)
        {
            _primaryDeferredResumePass = RecordDeferredResume(
                ref graph,
                frame,
                frame.Phase2DrawArgs,
                _phaseOneHiZPass,
                "primary post-HiZ",
                out _primaryDeferredResetPass);
            graph.OrderAfter(_primaryDeferredResetPass, phaseOneTessellationSnapshot);
            _primaryPostTraversalPass = RecordTraversal(
                ref graph,
                frame,
                packedViews,
                frame.ViewCount,
                viewIndexMode: 0u,
                traversalMode: 2u,
                seedRoots: false,
                startAfter: _primaryDeferredResumePass,
                _primaryPostTraversalLevelPasses,
                "primary post-HiZ",
                out _);
            _phaseTwoCullPass = RecordCullPhaseTwo(
                ref graph,
                frame,
                packedViews,
                frame.CandidateClusters,
                frame.CandidateCount,
                frame.CandidateArgs,
                frame.Phase2DrawArgs,
                frame.DrawArgs,
                frame.ReadOffsetZero,
                frame.ReadOffsetZero,
                "primary phase-two");
            graph.OrderAfter(_phaseTwoCullPass, _primaryPostTraversalPass);
        }
        RecordRasterPhase(
            ref graph,
            frame,
            rasterStages,
            packedViews,
            frame.Phase2DrawArgs,
            frame.DrawArgs,
            frame.ReadOffsetZero,
            frame.ReadOffsetZero,
            epoch: 1,
            cullBoundary: _phaseTwoCullPass,
            phaseOne: false,
            installCommonPasses);
        if (installCommonPasses)
        {
            _phaseTwoHiZPass = RecordHiZ(ref graph, frame, phaseOne: false);
            OrderAfterRasterPhase(ref graph, _phaseTwoHiZPass, phase: 1);
        }



        RecordShade(ref graph, frame, shadeStages, installCommonPasses);
        if (installCommonPasses && _shadeResolvePass != default)
            graph.OrderAfter(_shadeResolvePass, _phaseTwoHiZPass);

    }

    private void RecordContinuation(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews)
    {
        if (!_renderContext.IsValid)
            return;
        if (frame.ContinuationDrawArgs == default)
            throw new InvalidOperationException("The Cluster continuation count buffer is missing.");

        var reset = new ClusterContinuationResetParameters(
            frame.CandidateCount,
            frame.TraversalArguments0,
            frame.TraversalArguments1,
            frame.CandidateArgsInitialization,
            frame.CandidateArgs,
            frame.ContinuationDrawArgs,
            frame.Phase2CandidateCount,
            frame.Phase2CandidateArgs,
            frame.ContinuationPostDrawArgs);
        _continuationResetPass = graph.AddCopyFramePass(
            "Reset Cluster continuation scratch",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            reset,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            ClusterContinuationResetParameters.Declare,
            ClusterContinuationResetParameters.Record, region: _targetPhaseOneRegion);
        graph.OrderAfter(_continuationResetPass, _phaseTwoCullPass);

        _continuationTraversalPass = RecordTraversal(
            ref graph,
            frame,
            packedViews,
            _renderContext.MaximumInvocationCount,
            viewIndexMode: 1u,
            traversalMode: 1u,
            seedRoots: true,
            startAfter: default,
            _continuationTraversalLevelPasses,
            "continuation",
            out _continuationTraversalSeedPass, region: _targetPhaseOneRegion);
        graph.OrderAfter(_continuationTraversalSeedPass, _continuationResetPass);

        _continuationCullPass = RecordCullPhaseOne(
            ref graph,
            frame,
            packedViews,
            frame.ContinuationDrawArgs,
            frame.DrawArgs,
            frame.Phase2DrawArgs,
            resetPrimaryCullState: false,
            "continuation", region: _targetPhaseOneRegion);
        graph.OrderAfter(_continuationCullPass, _continuationTraversalPass);


        RecordRasterPhase(
            ref graph,
            frame,
            rasterStages,
            packedViews,
            frame.ContinuationDrawArgs,
            frame.DrawArgs,
            frame.Phase2DrawArgs,
            frame.ReadOffsetZero,
            epoch: 2,
            cullBoundary: _continuationCullPass,
            phaseOne: true,
            installCommonPasses: false,
            orderPrimaryDepth: false);


        _continuationDeferredResumePass = RecordDeferredResume(
            ref graph,
            frame,
            frame.ContinuationPostDrawArgs,
            default,
            "continuation post-HiZ",
            out _continuationDeferredResetPass, region: _targetPhaseTwoRegion);
        _continuationPostTraversalPass = RecordTraversal(
            ref graph,
            frame,
            packedViews,
            _renderContext.MaximumInvocationCount,
            viewIndexMode: 1u,
            traversalMode: 2u,
            seedRoots: false,
            startAfter: _continuationDeferredResumePass,
            _continuationPostTraversalLevelPasses,
            "continuation post-HiZ",
            out _, region: _targetPhaseTwoRegion);
        _continuationPostCullPass = RecordCullPhaseTwo(
            ref graph,
            frame,
            packedViews,
            frame.CandidateClusters,
            frame.CandidateCount,
            frame.CandidateArgs,
            frame.ContinuationPostDrawArgs,
            frame.DrawArgs,
            frame.Phase2DrawArgs,
            frame.ContinuationDrawArgs,
            "continuation post", region: _targetPhaseTwoRegion);
        graph.OrderAfter(
            _continuationPostCullPass,
            _continuationPostTraversalPass);
        RecordRasterPhase(
            ref graph,
            frame,
            rasterStages,
            packedViews,
            frame.ContinuationPostDrawArgs,
            frame.DrawArgs,
            frame.Phase2DrawArgs,
            frame.ContinuationDrawArgs,
            epoch: 3,
            cullBoundary: _continuationPostCullPass,
            phaseOne: false,
            installCommonPasses: false,
            orderPrimaryDepth: false);
    }

    private void OrderAfterRasterPhase(
        ref RenderGraphEdit graph,
        GraphPassId consumer,
        int phase)
    {
        if (phase is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(phase));
        AddRasterConsumer(ref graph, phase, consumer);
        GraphPassId winnerResolve = phase == 0 ? _phaseOneWinnerResolvePass : _phaseTwoWinnerResolvePass;
        if (winnerResolve != default)
            graph.OrderAfter(consumer, winnerResolve);
    }

    private void RecordGeometryStageRegion(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        bool rasterChanged)
    {
        if (!rasterChanged) return;
        for (int epoch = 0; epoch < RasterEpochCount; epoch++)
            if (_rasterEpochs.ContainsKey(epoch))
                UpdateRasterGroups(ref graph, epoch, rasterStages, initial: false);
    }

    private void RecordShadeStageRegion(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterShadeStageSet shadeStages)
    {

        UpdateShadeBinning(ref graph);
        UpdateShadeGroups(ref graph, shadeStages);

    }

    private GraphPassId TrackStagePass(GraphPassId pass)
    {
        if (_rasterGroup is { } raster)
            raster.Passes.Add(pass);
        return pass;
    }

    private void RecordFrameMetricsReadback(
        ref RenderGraphEdit graph,
        in FrameResources frame)
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
            Visibility = frame.VisBuffer,
            VisibilityProbeX = checked((uint)Math.Max(
                0,
                frame.Width / 2 - VisibilityProbePixelCount / 2)),
            VisibilityProbeY = checked((uint)(frame.Height / 2)),
            Destination = frame.FrameMetricsReadback,
        };
        GraphPassId candidates = graph.AddCopyFramePass(
            "Read back primary Cluster candidate metrics",
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
            }, ClusterFrameMetricsReadbackParameters.RecordCandidates, region: _regions.PrimaryGeometry);
        graph.OrderAfter(candidates, _phaseOneCullPass);
        _candidateMetricsReadbackPass = candidates;
        graph.OrderAfter(_primaryDeferredResetPass, candidates);
        _frameMetricsReadbackPass = graph.AddCopyFramePass(
            "Read back Cluster frame metrics",
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
        graph.OrderAfter(_frameMetricsReadbackPass, _shadeResolvePass);
        graph.OrderAfter(_frameMetricsReadbackPass, _shadeScatterPass);
        foreach (int epoch in _rasterEpochs.Keys)
            AddRasterConsumer(ref graph, epoch, _frameMetricsReadbackPass);
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
        ulong byteCount = checked(
            sizeof(uint) + (ulong)frame.PageFaultCapacity * sizeof(uint));
        BufferRange range = new(0, byteCount);
        ClusterBufferCopyParameters passData = new(
            frame.PageFaults,
            0,
            frame.PageFaultReadback,
            0,
            byteCount);
        _pageFaultReadbackPass = graph.AddCopyFramePass(
            "Read back Cluster page faults",
            PassQueueSelection.AnyCompatible(),
            passData,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true) { SubmitAfter = true },
            static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
            {
                _ = access.Read(data.Source, new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy, ResourceAccess.CopySource);
                _ = access.Write(data.Destination, new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy, ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            ClusterBufferCopyParameters.Record, region: _regions.Feedback);
        graph.OrderAfter(_pageFaultReadbackPass, _phaseTwoCullPass);
        if (_continuationPostCullPass != default)
            graph.OrderAfter(_pageFaultReadbackPass, _continuationPostCullPass);
    }

    private GraphPassId RecordTraversal(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId packedViews,
        uint viewCount,
        uint viewIndexMode,
        uint traversalMode,
        bool seedRoots,
        GraphPassId startAfter,
        List<GraphPassId> levelPasses,
        string epochLabel,
        out GraphPassId seedPass,
        GraphRegionId region = default)
    {
        int bindingCount = 0;
        GraphPassId previous = startAfter;
        seedPass = default;
        if (seedRoots)
        {
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    packedViews,
                    ClusterViewUniforms.SizeInBytes),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
                CreateReadOnlyBufferView(graph, frame.Bvh, stride: 64)));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TraversalNodes, TraversalNodeStride),
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
            GraphBufferInput seedUniforms = UploadUniform(
                ref graph,
                new ClusterTraversalSeedUniforms
                {
                    ViewCount = viewCount,
                    ViewIndexMode = viewIndexMode,
                    MaxNodes = _options.MaxCandidates,
                    ArgsOffset = 0u,
                    UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
                },
                $"Cluster {epochLabel} traversal seed uniforms");
            if (viewIndexMode == 0u)
                _primaryTraversalSeedUniforms = seedUniforms;
            else
                _continuationTraversalSeedUniforms = seedUniforms;
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
            previous = seedPass;
        }
        uint hierarchyLevels = checked((uint)_resources.HierarchyLevels);
        for (uint level = checked((uint)levelPasses.Count); level < hierarchyLevels; level++)
        {
            GraphBufferId currentArguments = (level & 1u) == 0u
                ? frame.TraversalArguments0
                : frame.TraversalArguments1;
            GraphBufferId nextArguments = (level & 1u) == 0u
                ? frame.TraversalArguments1
                : frame.TraversalArguments0;
            uint currentOffset = checked(level * (uint)TraversalArgumentStride);
            uint nextOffset = checked((level + 1u) * (uint)TraversalArgumentStride);
            GraphBufferInput levelUniforms = UploadUniform(
                ref graph,
                new ClusterTraversalLevelUniforms
                {
                    NodeLevel = level,
                    MaxNodes = _options.MaxCandidates,
                    CurrentArgsOffset = currentOffset,
                    NextArgsOffset = nextOffset,
                    TraversalMode = traversalMode,
                },
                $"Cluster {epochLabel} traversal level {level} uniforms");

            bindingCount = 0;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, packedViews, ClusterViewUniforms.SizeInBytes),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
                CreateReadOnlyBufferView(graph, frame.Bvh, stride: 64)));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.CandidateArgs),
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
                CreateStorageBufferView(graph, frame.PageFaults),
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Complete));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, levelUniforms),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.TraversalNodes, TraversalNodeStride),
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Partial,
                ResourceContentState.Defined,
                BufferContentScope.CountBoundedPrefix));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, currentArguments),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, nextArguments),
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Complete));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
                graph.CreateTextureSrv(
                    traversalMode == 2u ? frame.CurrentHiZ : frame.PreviousHiZ,
                    format: Format.R32Float,
                    dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.Phase2CandidateClusters, CandidateStride),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Partial,
                ResourceContentState.Defined,
                BufferContentScope.CountBoundedPrefix));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.Phase2CandidateCount, sizeof(uint)),
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Complete));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.Phase2CandidateArgs),
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Complete));

            var indirect = new ClusterIndirectDispatchParameters(
                RequireDispatchIndirectLayout(),
                currentArguments,
                currentOffset);
            GraphPassId levelPass = AddComputePass(
                ref graph,
                $"Traverse Cluster {epochLabel} hierarchy level {level}",
                _pipelines!.Traversal,
                indirect,
                bindingCount,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record, region: region);
            if (previous != default)
                graph.OrderAfter(levelPass, previous);
            levelPasses.Add(levelPass);
            previous = levelPass;
        }
        return previous;
    }

    private GraphPassId RecordDeferredResume(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId drawArgs,
        GraphPassId startAfter,
        string epochLabel,
        out GraphPassId resetPass,
        GraphRegionId region = default)
    {
        var reset = new ClusterDeferredResumeResetParameters(
            frame.CandidateCount,
            frame.CandidateArgsInitialization,
            frame.CandidateArgs,
            frame.TraversalArguments0,
            frame.TraversalArguments1,
            drawArgs);
        resetPass = graph.AddCopyFramePass(
            $"Reset Cluster {epochLabel} deferred-resume outputs",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            reset,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            ClusterDeferredResumeResetParameters.Declare,
            ClusterDeferredResumeResetParameters.Record, region: region == default ? _regions.PrimaryGeometry : region);
        if (startAfter != default)
            graph.OrderAfter(resetPass, startAfter);

        GraphBufferInput uniforms = UploadUniform(
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
            CreateStorageBufferView(graph, frame.TraversalNodes, TraversalNodeStride),
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
        graph.OrderAfter(resume, resetPass);
        return resume;
    }

    private GraphPassId RecordCullPhaseOne(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId packedViews,
        GraphBufferId drawArgs,
        GraphBufferId baseDrawArgs0,
        GraphBufferId baseDrawArgs1,
        bool resetPrimaryCullState,
        string epochLabel,
        GraphRegionId region = default)
    {
        int bindingCount = 0;
        if (resetPrimaryCullState)
        {
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
                _pipelines!.CullReset, clear, bindingCount, ClusterDispatchParameters.Record, region: region);
        }

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(
                graph,
                packedViews,
                ClusterViewUniforms.SizeInBytes),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.CandidateClusters, CandidateStride),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, baseDrawArgs0), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, baseDrawArgs1), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(
                graph,
                frame.VisibleClusterRasterWindows,
                VisibleClusterRasterWindowStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.PreviousHiZ,
                Format.R32Float,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2CandidateClusters, CandidateStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2CandidateArgs), PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters cull = new(
            RequireDispatchIndirectLayout(), frame.CandidateArgs, 0);
        return AddComputePass(ref graph, $"Cluster {epochLabel} cull", _pipelines!.CullPhase1,
            cull, bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);
    }

    private GraphPassId RecordCullPhaseTwo(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId packedViews,
        GraphBufferId candidateClusters,
        GraphBufferId candidateCount,
        GraphBufferId candidateArgs,
        GraphBufferId phaseDrawArgs,
        GraphBufferId baseDrawArgs0,
        GraphBufferId baseDrawArgs1,
        GraphBufferId baseDrawArgs2,
        string epochLabel,
        GraphRegionId region = default)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(
                graph,
                packedViews,
                ClusterViewUniforms.SizeInBytes),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, candidateClusters, CandidateStride),
            PipelineSync.ComputeShading,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, candidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, baseDrawArgs0), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, baseDrawArgs1), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, baseDrawArgs2), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(
                graph,
                frame.VisibleClusterRasterWindows,
                VisibleClusterRasterWindowStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedPrefix));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.CurrentHiZ,
                Format.R32Float,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, phaseDrawArgs), PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters cull = new(
            RequireDispatchIndirectLayout(), candidateArgs, 0);
        return AddComputePass(ref graph, $"Cluster {epochLabel} cull", _pipelines!.CullPhase2,
            cull, bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);
    }

    private void RecordRasterPhase(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        GraphBufferId drawArgs,
        GraphBufferId readOffsetArgs0,
        GraphBufferId readOffsetArgs1,
        GraphBufferId readOffsetArgs2,
        int epoch,
        GraphPassId cullBoundary,
        bool phaseOne,
        bool installCommonPasses,
        bool orderPrimaryDepth = true)
    {
        var work = new RasterPassGroup();
        var patches = new RasterPassGroup();
        _rasterGroup = work;
        try
        {
            RecordRasterWorkGeneration(ref graph, frame, drawArgs,
                readOffsetArgs0, readOffsetArgs1, readOffsetArgs2, phaseOne, epoch);
            RecordTessellationReset(ref graph, frame, phaseOne, GetRasterRegion(epoch));
        }
        finally { _rasterGroup = null; }
        OrderRasterPasses(ref graph, work, cullBoundary);
        _rasterEpochs = _rasterEpochs.SetItem(epoch, new RasterEpoch(work, patches,
            System.Collections.Immutable.ImmutableDictionary<uint, RasterBin>.Empty,
            System.Collections.Immutable.ImmutableDictionary<TessellationSplitKey, TessellationGroup>.Empty,
            null, null, System.Collections.Immutable.ImmutableHashSet<GraphPassId>.Empty,
            phaseOne, epoch >= 2));
        UpdateRasterGroups(ref graph, epoch, rasterStages, initial: true);
        if (orderPrimaryDepth && _visibilityMode != ClusterVisibilityMode.HardwareOnly)
        {
            GraphPassId winnerResolve = RecordWinnerResolve(ref graph, frame, phaseOne);
            if (phaseOne) _phaseOneWinnerResolvePass = winnerResolve;
            else _phaseTwoWinnerResolvePass = winnerResolve;
            AddRasterConsumer(ref graph, epoch, winnerResolve);
        }
    }

    private void RecordRasterWorkGeneration(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId drawArgs,
        GraphBufferId readOffsetArgs0,
        GraphBufferId readOffsetArgs1,
        GraphBufferId readOffsetArgs2,
        bool phaseOne,
        int epoch)
    {
        GraphBufferInput binningUniforms = UploadUniform(
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
            ClusterDispatchParameters.Record, region: GetRasterRegion(epoch));

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
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.BinnedClusters, BinnedClusterStride),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedRanges));
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
            ClusterIndirectDispatchParameters.Record, region: GetRasterRegion(epoch));
        graph.OrderAfter(countPass, resetPass);

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
        GraphBufferId policyRasterWorkCounter = _renderContext.IsValid && epoch >= 2
            ? _renderContext.PolicyRasterWorkCounter
            : frame.NeutralPolicyCounter;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, policyRasterWorkCounter, sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
        ClusterDispatchParameters reserve = new(new DispatchArguments(
            Math.Max(1u, (frame.RasterBinCount + 127u) / 128u), 1, 1));
        GraphPassId reservePass = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one raster bins reserve"
                : "Cluster phase two raster bins reserve",
            _pipelines.RasterBinReserve,
            reserve,
            bindingCount,
            ClusterDispatchParameters.Record, region: GetRasterRegion(epoch));
        graph.OrderAfter(reservePass, countPass);

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
            ClusterIndirectDispatchParameters.Record, region: GetRasterRegion(epoch));
        graph.OrderAfter(scatterPass, reservePass);
        _rasterBinning[epoch] = new RasterBinning(
            binningUniforms,
            resetPass,
            reservePass,
            epoch >= 2,
            countPass,
            scatterPass);

    }

    private GraphPassId RecordTessellationSeed(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterInvocation invocation,
        GraphPassId predecessor,
        bool phaseOne,
        bool policyTarget)
    {
        ClusterTessellationInvocation tessellation = invocation.Tessellation!;
        uint bin = invocation.BinIndex;
        GraphBufferInput uniforms = UploadUniform(
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
            ClusterIndirectDispatchParameters.Record, region: GetRasterRegion(policyTarget, phaseOne));
        _materialInputsAreShade = false;
        _materialInputBin = invocation.BinIndex;
        PassDefinition seedDefinition = graph.GetPass(seedPass);
        BindTessellationResources(ref graph, ref seedDefinition, invocation,
            tessellation.SeedResources, uniforms, phaseOne, 0u,
            PipelineSync.ComputeShading, policyTarget);
        _pageHeapReaderPasses = _pageHeapReaderPasses.Add(seedPass);
        graph.OrderAfter(seedPass, predecessor);
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
        _ = TrackStagePass(graph.AddCopyFramePass(
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
            ClusterTessellationResetParameters.Record, region: region));
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
        return TrackStagePass(graph.AddCopyFramePass(
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
            ClusterTessellationLevelResetParameters.Record, region: region));
    }

    private GraphPassId RecordPhaseOneTessellationCounterSnapshot(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        ClusterBufferCopyParameters copy = new(
            frame.TessellationCounters,
            0,
            frame.PhaseOneTessellationCounters,
            0,
            8 * sizeof(uint));
        return TrackStagePass(graph.AddCopyFramePass(
            "Snapshot Cluster phase one tessellation counters",
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
            ClusterBufferCopyParameters.Record, region: _regions.PrimaryGeometry));
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
        GraphBufferInput uniforms = UploadUniform(
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
        graph.OrderAfter(countPass, resetPass);
        graph.OrderAfter(reservePass, countPass);
        graph.OrderAfter(scatterPass, reservePass);
        if (_rasterGroup is { } raster)
            raster.PatchBinning = new RasterBinning(
                uniforms,
                resetPass,
                reservePass,
                false);
    }

    private int BindTessellationBinResources(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferInput uniforms,
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
        GraphPassId seedPass,
        bool phaseOne,
        bool policyTarget,
        bool sharedBins)
    {
        ClusterTessellationInvocation tessellation = invocation.Tessellation ??
            throw new InvalidOperationException("A tessellation split invocation has no tessellation stages.");
        uint bin = invocation.BinIndex;
        GraphPassId predecessor = seedPass;
        for (uint level = 0u; level < (uint)TessellationSplitLevelCount; level++)
        {
            GraphBufferInput uniforms = UploadUniform(
                ref graph,
                CreateTessellationUniforms(frame, sharedBins ? uint.MaxValue : bin, phaseOne, level),
                phaseOne
                    ? $"Cluster phase one tessellation split level {level} uniforms"
                    : $"Cluster phase two tessellation split level {level} uniforms");
            _rasterGroup?.SplitUniforms.Add(uniforms);
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
                ClusterIndirectDispatchParameters.Record, region: GetRasterRegion(policyTarget, phaseOne));
            _materialInputsAreShade = false;
            _materialInputBin = invocation.BinIndex;
            PassDefinition splitDefinition = graph.GetPass(pass);
            BindTessellationResources(ref graph, ref splitDefinition, invocation,
                tessellation.SplitResources, uniforms, phaseOne, level + 1u,
                PipelineSync.ComputeShading, policyTarget);
            graph.OrderAfter(pass, predecessor);
            predecessor = pass;
            _pageHeapReaderPasses = _pageHeapReaderPasses.Add(pass);
        }
    }

    private void RecordTessellationDice(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        IReadOnlyList<ClusterRasterInvocation> stages,
        bool phaseOne,
        bool policyTarget)
    {
        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = stages[invocationIndex];
            if (invocation.Tessellation is not { } tessellation)
                continue;
            uint bin = invocation.BinIndex;
            GraphBufferInput uniforms = UploadUniform(
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
                asyncCompute: true, region: GetRasterRegion(policyTarget, phaseOne));
            _materialInputsAreShade = false;
            _materialInputBin = invocation.BinIndex;
            PassDefinition diceDefinition = graph.GetPass(dicePass);
            BindTessellationResources(ref graph, ref diceDefinition, invocation,
                tessellation.DiceResources!, uniforms, phaseOne, 0u,
                PipelineSync.ComputeShading, policyTarget);
            _pageHeapReaderPasses = _pageHeapReaderPasses.Add(dicePass);
        }
    }

    private void RecordTessellationHardwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        IReadOnlyList<ClusterRasterInvocation> stages,
        bool phaseOne,
        bool policyTarget)
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
            GraphBufferInput uniforms = UploadUniform(
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
                ClusterTessellationRasterPassData.Record, region: GetRasterRegion(policyTarget, phaseOne));
            if (pipeline.UsesMesh)
                _meshRasterPasses = _meshRasterPasses.Add(hardwarePass, new MeshRaster(bin, indirectAccess, passData));
            _materialInputsAreShade = false;
            _materialInputBin = invocation.BinIndex;
            PassDefinition hardwareDefinition = graph.GetPass(hardwarePass);
            BindTessellationResources(ref graph, ref hardwareDefinition, invocation,
                tessellation.HardwareResources!, uniforms, phaseOne, 0u,
                PipelineSync.AllShading, policyTarget);
            _pageHeapReaderPasses = _pageHeapReaderPasses.Add(hardwarePass);
        }
    }

    private void RecordSoftwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        IReadOnlyList<ClusterRasterInvocation> stages,
        GraphBufferId packedViews,
        bool phaseOne,
        bool policyTarget)
    {
        if (_visibilityMode == ClusterVisibilityMode.HardwareOnly)
            return;

        for (int invocationIndex = 0; invocationIndex < stages.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = stages[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferInput pushConstants = UploadUniform(
                ref graph,
                new ClusterSoftwareRasterUniforms
                {
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                    MaxBins = frame.RasterBinCount,
                    DebugDump = _options.EnableFrameMetricsReadback ? 1u : 0u,
                    CurrentBin = bin,
                },
                phaseOne
                    ? "Cluster phase one software raster bin uniforms"
                    : "Cluster phase two software raster bin uniforms");
            if (_rasterGroup is { } raster) raster.SoftwareUniforms = pushConstants;
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
                ClusterSoftwareRasterPassData.Record, region: GetRasterRegion(policyTarget, phaseOne));
            _materialInputsAreShade = false;
            _materialInputBin = invocation.BinIndex;
            PassDefinition softwareDefinition = graph.GetPass(softwarePass);
            BindSoftwareRasterResources(ref graph, ref softwareDefinition, invocation,
                pushConstants, policyTarget);
            _pageHeapReaderPasses = _pageHeapReaderPasses.Add(softwarePass);
        }
    }

    private GraphPassId RecordWinnerResolve(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
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
        GraphBufferInput uniforms = UploadUniform(
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
                ClusterHardwareRasterPassData.Record);
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
            ClusterFullscreenParameters.Record);
        _winnerResolvePasses.Add(pass);
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
        bool policyTarget)
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
        GraphBufferInput drawUniforms = UploadUniform(
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
            GraphBufferInput dispatchUniforms = UploadUniform(
                ref graph,
                new ClusterDrawDispatchUniforms
                {
                    DrawArgsByteOffset = checked(bin * ClusterIndirectAbi.HardwareRasterStride),
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
                        RasterHeight: checked((uint)rasterHeight)), region: GetRasterRegion(policyTarget, phaseOne));
            _materialInputsAreShade = false;
            _materialInputBin = invocation.BinIndex;
            PassDefinition hardwareDefinition = graph.GetPass(hardwarePass);
            BindHardwareRasterResources(ref graph, ref hardwareDefinition, invocation,
                drawUniforms, dispatchUniforms, policyTarget);
            _pageHeapReaderPasses = _pageHeapReaderPasses.Add(hardwarePass);
        }
    }

    private GraphPassId RecordHiZ(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        int mipCount = _history!.HiZMipCount;
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
            phaseOne, ClusterRasterArguments.HiZFirstOffset);
        OrderAfterRasterPhase(ref graph, firstPass, phaseOne ? 0 : 1);

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
                tail ? ClusterRasterArguments.HiZTailOffset : ClusterRasterArguments.HiZPairOffset(destinationMip));
            graph.OrderAfter(pass, previousPass);
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
        ulong argumentOffset)
    {
        if (phaseOne)
        {
            GraphPassId pass = AddComputePass(ref graph, label, pipeline,
                new ClusterDispatchParameters(new DispatchArguments(width, height, 1)),
                bindingCount, ClusterDispatchParameters.Record);
            _layerDispatches.Add(new ClusterLayerDispatch(pass, width, height));
            return pass;
        }
        ClusterIndirectDispatchParameters indirect = new(
            RequireDispatchIndirectLayout(), frame.RasterWorkArguments, argumentOffset);
        return AddComputePass(ref graph, label, pipeline, indirect, bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);
    }

    private void RecordShade(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterShadeStageSet shadeStages,
        bool installCommonPasses)
    {
        if (!installCommonPasses)
        {
            UpdateShadeBinning(ref graph);
            UpdateShadeGroups(ref graph, shadeStages);
            return;
        }
        GraphBufferInput binUniforms = UploadUniform(
            ref graph,
            new ClusterShadeBinUniforms
            {
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                ShadeBinCount = frame.ShadeBinCount,
                SlotCapacity = frame.SlotCapacity,
                BinItemCapacity = CalculateShadeBinItemCapacity(frame),
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
        ClusterDispatchParameters clear = new(new DispatchArguments(
            Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        GraphPassId clearPass = AddComputePass(ref graph, "Clear Cluster shade bins",
            _pipelines!.ShadeBinClearPrepare, clear, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true, region: _regions.Shading);

        bindingCount = 0;
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
            CreateStorageBufferView(graph, frame.PixelCoordinates, 2 * sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedRanges));
        ClusterDispatchParameters count = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphPassId countPass = AddComputePass(
            ref graph,
            "Count Cluster shade bins",
            _pipelines.ShadeBinCount, count, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true, region: _regions.Shading);
        if (_phaseTwoHiZPass == default)
            throw new InvalidOperationException("Cluster shade preparation requires a geometry completion pass.");
        graph.OrderAfter(countPass, _phaseTwoHiZPass);
        graph.OrderAfter(countPass, clearPass);
        _layerDispatches.Add(new ClusterLayerDispatch(
            countPass,
            Groups(frame.Width, 8),
            Groups(frame.Height, 8)));

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
        ClusterDispatchParameters reserve = new(new DispatchArguments(
            Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        GraphPassId reservePass = AddComputePass(ref graph, "Reserve Cluster shade bins",
            _pipelines.ShadeBinReserve, reserve, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: false, region: _regions.Shading);
        _shadeBinUniforms = binUniforms;
        _shadeCountPass = countPass;
        _shadeClearPass = clearPass;
        _shadeReservePass = reservePass;
        graph.OrderAfter(reservePass, countPass);

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
            CreateStorageBufferView(graph, frame.PixelCoordinates, 2 * sizeof(uint)),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Partial,
            ResourceContentState.Defined,
            BufferContentScope.CountBoundedRanges));
        ClusterDispatchParameters scatter = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphPassId scatterPass = AddComputePass(ref graph, "Scatter Cluster shade bins",
            _pipelines.ShadeBinScatter, scatter, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true, region: _regions.Shading);
        graph.OrderAfter(scatterPass, reservePass);
        _layerDispatches.Add(new ClusterLayerDispatch(
            scatterPass,
            Groups(frame.Width, 8),
            Groups(frame.Height, 8)));

        _shadeScatterPass = scatterPass;
        GraphPassId resolvePass;
        if (installCommonPasses)
        {

            GraphBufferInput resolveUniforms = UploadUniform(
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
                WriteCoverage.Complete));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.MotionVectors,
                    Format.R16G16Float,
                    null,
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            ClusterDispatchParameters resolve = new(new DispatchArguments(
                Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
            resolvePass = AddComputePass(ref graph, "Resolve Cluster visibility background",
                _pipelines.Resolve, resolve, bindingCount,
                ClusterDispatchParameters.Record, region: _regions.Shading);
            _layerDispatches.Add(new ClusterLayerDispatch(
                resolvePass,
                Groups(frame.Width, 8),
                Groups(frame.Height, 8)));
            _shadeResolvePass = resolvePass;

        }
        else
        {
            resolvePass = _shadeResolvePass;
        }
        graph.OrderAfter(resolvePass, scatterPass);
        graph.OrderAfter(resolvePass, reservePass);

        UpdateShadeGroups(ref graph, shadeStages);
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
        bool readsPageHeap = _bindingScratchReadsPageHeap;
        _bindingScratchReadsPageHeap = false;
        Span<GraphParameterResourceBinding> bindings =
            _renderGraphBindings.AsSpan(0, bindingCount);
        try
        {
            GraphPassId pass = TrackStagePass(graph.AddRasterFramePass(
                name,
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                state,
                options with { RepeatCommands = true },
                pipeline.Pipeline,
                pipeline.ParameterLayout,
                bindings,
                declaration,
                callback, region: region == default ? _regions.PrimaryGeometry : region));
            if (readsPageHeap)
                _pageHeapReaderPasses = _pageHeapReaderPasses.Add(pass);
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
        bool readsPageHeap = _bindingScratchReadsPageHeap;
        _bindingScratchReadsPageHeap = false;
        Span<GraphParameterResourceBinding> bindings =
            _renderGraphBindings.AsSpan(0, bindingCount);
        try
        {
            GraphPassId pass = TrackStagePass(graph.AddComputeFramePass(
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
                callback, region: region == default ? _regions.PrimaryGeometry : region));
            if (readsPageHeap)
                _pageHeapReaderPasses = _pageHeapReaderPasses.Add(pass);
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

    private GraphRegionId GetRasterRegion(int epoch) => epoch switch
    {
        2 => _targetPhaseOneRegion,
        3 => _targetPhaseTwoRegion,
        _ => _regions.PrimaryGeometry,
    };

    private GraphRegionId GetRasterRegion(bool policyTarget, bool phaseOne) =>
        policyTarget ? GetRasterRegion(phaseOne ? 2 : 3) : _regions.PrimaryGeometry;

    private void AddBinding(ref int count, in GraphParameterResourceBinding binding)
    {
        if ((uint)count >= (uint)_renderGraphBindings.Length)
            throw new InvalidOperationException("A Cluster Pass exceeds the binding scratch capacity.");
        _renderGraphBindings[count++] = binding;
    }

    private void AddPageHeapBinding(ref int count, GraphBufferSrvId view)
    {
        if (_bindingScratchReadsPageHeap)
            throw new InvalidOperationException("A Cluster Pass cannot bind the page heap twice.");
        _bindingScratchReadsPageHeap = true;
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

    private static GraphBufferCbvId CreateConstantBufferView(
        RenderGraphEdit graph,
        GraphBufferInput input) => graph.CreateBufferCbv(input);

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
