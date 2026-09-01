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
        ClusterDeformStageSet deformStages,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages)
    {
        _frameResourceScratch.Begin();
        try
        {
            FrameResources frame = CreateFrameResources(
                ref graph,
                in output,
                assignments,
                deformStages,
                rasterStages,
                shadeStages);
            _frameResources = frame;
            AuthorHistoryInitialization(ref graph, frame);
            GraphBufferId packedViews = UploadStructs(
                ref graph,
                _packedViews,
                BufferUsages.ShaderRead,
                "Cluster packed views");
            frame.PackedViews = packedViews;
            _frameResources = frame;

            _traversalPass = RecordTraversal(ref graph, frame, packedViews, 1);
            RecordStageRegion(
                ref graph,
                frame,
                deformStages,
                rasterStages,
                packedViews,
                shadeStages,
                installCommonPasses: true);
            RecordPageFaultReadback(ref graph, frame);
            RecordFrameMetricsReadback(ref graph, frame);
            _outputs = new ClusterPipelineOutputs(
                frame.VisBuffer,
                frame.Depth,
                frame.SceneColor,
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
                    frame.DeformCache,
                    frame.CacheOffsets),
                frame.Width,
                frame.Height,
                frame.ViewCapacity);
        }
        finally
        {
            _frameResourceScratch.End();
        }
    }

    private void RecordStageRegion(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterDeformStageSet deformStages,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        ClusterShadeStageSet shadeStages,
        bool installCommonPasses)
    {
        if (_recordStagePasses)
            throw new InvalidOperationException("Cluster stage pass recording is already active.");
        if (_stagePassDestination is not null)
            throw new InvalidOperationException("Cluster stage pass destination is already active.");
        _stagePassDestination = _geometryStagePasses;
        try
        {
        if (installCommonPasses)
            _phaseOneCullPass = RecordCullPhaseOne(ref graph, frame, packedViews);
        RecordRasterPhase(
            ref graph,
            frame,
            deformStages,
            rasterStages,
            packedViews,
            frame.DrawArgs,
            frame.ReadOffsetZero,
            resetCacheAllocation: true,
            installCommonPasses);
        if (installCommonPasses)
            _phaseOneHiZPass = RecordHiZ(ref graph, frame, phaseOne: true);

        _recordStagePasses = true;
        try
        {
            _ = RecordPhaseOneTessellationCounterSnapshot(ref graph, frame);
        }
        finally
        {
            _recordStagePasses = false;
        }

        if (installCommonPasses)
            _phaseTwoCullPass = RecordCullPhaseTwo(ref graph, frame, packedViews);
        RecordRasterPhase(
            ref graph,
            frame,
            deformStages,
            rasterStages,
            packedViews,
            frame.Phase2DrawArgs,
            frame.DrawArgs,
            resetCacheAllocation: false,
            installCommonPasses);
        if (installCommonPasses)
            _phaseTwoHiZPass = RecordHiZ(ref graph, frame, phaseOne: false);
        }
        finally
        {
            _stagePassDestination = null;
        }

        _stagePassDestination = _shadeStagePasses;
        _recordStagePasses = true;
        try
        {
            RecordShade(ref graph, frame, shadeStages, installCommonPasses);
        }
        finally
        {
            _recordStagePasses = false;
            _stagePassDestination = null;
        }
    }

    private void RecordGeometryStageRegion(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterDeformStageSet deformStages,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        bool deformChanged,
        bool rasterChanged)
    {
        if (_recordStagePasses || _stagePassDestination is not null)
            throw new InvalidOperationException("Cluster geometry stage recording is already active.");
        _stagePassDestination = _geometryStagePasses;
        try
        {
            RecordRasterPhase(
                ref graph,
                frame,
                deformStages,
                rasterStages,
                packedViews,
                frame.DrawArgs,
                frame.ReadOffsetZero,
                resetCacheAllocation: true,
                installCommonPasses: false,
                recordBinning: true,
                recordDeform: deformChanged,
                recordRaster: rasterChanged);

            RecordRasterPhase(
                ref graph,
                frame,
                deformStages,
                rasterStages,
                packedViews,
                frame.Phase2DrawArgs,
                frame.DrawArgs,
                resetCacheAllocation: false,
                installCommonPasses: false,
                recordBinning: true,
                recordDeform: deformChanged,
                recordRaster: rasterChanged);
        }
        finally
        {
            _recordStagePasses = false;
            _stagePassDestination = null;
        }
    }

    private void RecordShadeStageRegion(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterShadeStageSet shadeStages)
    {
        if (_recordStagePasses || _stagePassDestination is not null)
            throw new InvalidOperationException("Cluster shade stage recording is already active.");
        _stagePassDestination = _shadeStagePasses;
        _recordStagePasses = true;
        try
        {
            RecordShade(ref graph, frame, shadeStages, installCommonPasses: false);
        }
        finally
        {
            _recordStagePasses = false;
            _stagePassDestination = null;
        }
    }

    private GraphPassId TrackStagePass(GraphPassId pass)
    {
        if (_recordStagePasses)
        {
            _stagePasses.Add(pass);
            _stagePassDestination?.Add(pass);
            _stagePassGroupDestination?.Add(pass);
        }
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
            DeformReserve = frame.DeformReserveCounters,
            CacheAllocation = frame.CacheAllocationCounter,
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
        _ = graph.AddCopyFramePass(
            "Read back Cluster frame metrics",
            PassQueueSelection.AnyOfType(QueueType.Copy),
            passData,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref ClusterFrameMetricsReadbackParameters data) =>
            {
                Read(ref access, data.CandidateCount, 0, sizeof(uint));
                Read(ref access, data.CandidateArgs, 0, 12);
                Read(ref access, data.DrawArgs, 0, 16);
                Read(ref access, data.Phase2CandidateCount, 0, sizeof(uint));
                Read(ref access, data.Phase2CandidateArgs, 0, 12);
                Read(ref access, data.Phase2DrawArgs, 0, 16);
                Read(ref access, data.RasterReserve, 2 * sizeof(uint), 2 * sizeof(uint));
                Read(ref access, data.ShadeCount, 0, sizeof(uint));
                Read(ref access, data.ShadeReserve, 0, sizeof(uint));
                Read(ref access, data.DeformReserve, sizeof(uint), sizeof(uint));
                Read(ref access, data.CacheAllocation, 0, 2 * sizeof(uint));
                Read(ref access, data.SoftwareDebug, 0, sizeof(uint));
                Read(ref access, data.PhaseOneTessellationCounters, 0, 4 * sizeof(uint));
                Read(ref access, data.PhaseTwoTessellationCounters, 0, 4 * sizeof(uint));
                _ = access.Read(
                    data.Visibility,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource,
                    TextureLayout.CopySource);
                _ = access.Write(data.Destination,
                    new BufferRange(0, FrameMetricsReadbackByteSize),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            ClusterFrameMetricsReadbackParameters.Record);
    }

    private static void RecordPageFaultReadback(
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
        _ = graph.AddCopyFramePass(
            "Read back Cluster page faults",
            PassQueueSelection.AnyOfType(QueueType.Copy),
            passData,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
            {
                _ = access.Read(data.Source, new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy, ResourceAccess.CopySource);
                _ = access.Write(data.Destination, new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy, ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            ClusterBufferCopyParameters.Record);
    }

    private GraphPassId RecordTraversal(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId packedViews,
        uint instanceCount)
    {
        int bindingCount = 0;
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
            CreateStorageBufferView(graph, frame.CandidateArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CandidateClusters, CandidateStride),
            PipelineSync.ComputeShading, GraphAccessMode.Write));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.PageFaults), PipelineSync.ComputeShading));
        ClusterDispatchParameters passData = new(
            new DispatchArguments(checked((instanceCount + 63u) / 64u), 1, 1));
        return AddComputePass(ref graph, "Cluster BVH traversal", _pipelines!.Traversal,
            passData, bindingCount, ClusterDispatchParameters.Record);
    }

    private GraphPassId RecordCullPhaseOne(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId packedViews)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DrawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2CandidateArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2DrawArgs), PipelineSync.ComputeShading));
        ClusterDispatchParameters clear = new(new DispatchArguments(1, 1, 1));
        _ = AddComputePass(ref graph, "Reset Cluster cull",
            _pipelines!.CullReset, clear, bindingCount, ClusterDispatchParameters.Record);

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
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DrawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading, GraphAccessMode.Write));
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
            PipelineSync.ComputeShading, GraphAccessMode.Write));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2CandidateArgs), PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters cull = new(
            RequireDispatchIndirectLayout(), frame.CandidateArgs, 0);
        return AddComputePass(ref graph, "Cluster phase-one cull", _pipelines.CullPhase1,
            cull, bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);
    }

    private GraphPassId RecordCullPhaseTwo(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId packedViews)
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
            CreateReadOnlyBufferView(graph, frame.Phase2CandidateClusters, CandidateStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.Phase2CandidateCount, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.DrawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading, GraphAccessMode.Write));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.CurrentHiZ,
                Format.R32Float,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2DrawArgs), PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters cull = new(
            RequireDispatchIndirectLayout(), frame.Phase2CandidateArgs, 0);
        return AddComputePass(ref graph, "Cluster phase-two cull", _pipelines!.CullPhase2,
            cull, bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);
    }

    private void RecordRasterPhase(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterDeformStageSet deformStages,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        GraphBufferId drawArgs,
        GraphBufferId readOffsetArgs,
        bool resetCacheAllocation,
        bool installCommonPasses,
        bool recordBinning = true,
        bool recordDeform = true,
        bool recordRaster = true)
    {
        int phase = resetCacheAllocation ? 0 : 1;
        int dynamicStart = _stagePasses.Count;
        _recordStagePasses = true;
        try
        {
            if (recordBinning)
            {
                _stagePassGroupDestination = _binningStagePasses[phase];
                RecordRasterWorkGeneration(
                    ref graph,
                    frame,
                    drawArgs,
                    readOffsetArgs,
                    resetCacheAllocation);
            }
            if (recordDeform)
            {
                _stagePassGroupDestination = _deformStagePasses[phase];
                RecordDeform(ref graph, frame, deformStages, resetCacheAllocation);
            }
            if (recordRaster)
            {
                _stagePassGroupDestination = _rasterBeforeDepthStagePasses[phase];
                RecordTessellation(ref graph, frame, rasterStages, packedViews, resetCacheAllocation);
                RecordSoftwareRaster(ref graph, frame, rasterStages, packedViews, resetCacheAllocation);
            }
        }
        finally
        {
            _stagePassGroupDestination = null;
            _recordStagePasses = false;
        }

        GraphPassId cullBoundary = resetCacheAllocation ? _phaseOneCullPass : _phaseTwoCullPass;
        GraphPassId depthMerge;
        if (installCommonPasses)
        {
            depthMerge = RecordDepthMerge(ref graph, frame, resetCacheAllocation);
            if (resetCacheAllocation)
                _phaseOneDepthMergePass = depthMerge;
            else
                _phaseTwoDepthMergePass = depthMerge;
        }
        else
        {
            depthMerge = resetCacheAllocation ? _phaseOneDepthMergePass : _phaseTwoDepthMergePass;
        }

        for (int index = dynamicStart; index < _stagePasses.Count; index++)
        {
            graph.OrderAfter(_stagePasses[index], cullBoundary);
            graph.OrderAfter(depthMerge, _stagePasses[index]);
        }

        if (recordBinning)
        {
            GraphPassId? firstConsumer = _deformStagePasses[phase].Count != 0
                ? _deformStagePasses[phase][0]
                : _rasterBeforeDepthStagePasses[phase].Count != 0
                    ? _rasterBeforeDepthStagePasses[phase][0]
                    : null;
            if (firstConsumer is { } consumer)
            {
                foreach (GraphPassId pass in _binningStagePasses[phase])
                    graph.OrderAfter(consumer, pass);
            }
        }
        if (recordDeform && _rasterBeforeDepthStagePasses[phase].Count != 0)
        {
            GraphPassId firstRaster = _rasterBeforeDepthStagePasses[phase][0];
            foreach (GraphPassId pass in _deformStagePasses[phase])
                graph.OrderAfter(firstRaster, pass);
        }

        int hardwareStart = _hardwareRasterPassBindings.Count;
        if (recordRaster)
        {
            _recordStagePasses = true;
            _stagePassGroupDestination = _hardwareRasterStagePasses[phase];
            try
            {
                RecordHardwareRaster(ref graph, frame, rasterStages, packedViews, resetCacheAllocation);
            }
            finally
            {
                _stagePassGroupDestination = null;
                _recordStagePasses = false;
            }
        }
        for (int index = hardwareStart; index < _hardwareRasterPassBindings.Count; index++)
            graph.OrderAfter(_hardwareRasterPassBindings[index].Pass, depthMerge);
        if (!installCommonPasses)
        {
            GraphPassId hiZ = resetCacheAllocation ? _phaseOneHiZPass : _phaseTwoHiZPass;
            for (int index = hardwareStart; index < _hardwareRasterPassBindings.Count; index++)
                graph.OrderAfter(hiZ, _hardwareRasterPassBindings[index].Pass);
        }
    }

    private void RecordRasterWorkGeneration(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId drawArgs,
        GraphBufferId readOffsetArgs,
        bool resetCacheAllocation)
    {
        GraphBufferId binningUniforms = UploadUniform(
            ref graph,
            new ClusterRasterDeformBinningUniforms
            {
                RasterMaxBins = frame.RasterBinCount,
                DeformMaxBins = frame.DeformBinCount,
                SlotCapacity = frame.SlotCapacity,
                MaxVisibleClusters = _options.MaxCandidates,
                ResetCacheAllocationState = resetCacheAllocation ? 1u : 0u,
            },
            resetCacheAllocation
                ? "Cluster phase one raster/deform binning uniforms"
                : "Cluster phase two raster/deform binning uniforms");

        uint binClearGroups = Math.Max(
            1u,
            (Math.Max(frame.RasterBinCount, frame.DeformBinCount) + 63u) / 64u);

        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.BinningDispatchArgs),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheAllocationCounter, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters reset = new(
            new DispatchArguments(binClearGroups, 1, 1));
        _ = AddComputePass(
            ref graph,
            resetCacheAllocation
                ? "Cluster phase one raster/deform bins reset"
                : "Cluster phase two raster/deform bins reset",
            _pipelines!.RasterDeformBinReset,
            reset,
            bindingCount,
            ClusterDispatchParameters.Record);

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
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.RasterSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.DeformSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters count = new(
            RequireDispatchIndirectLayout(), frame.BinningDispatchArgs, 0);
        _ = AddComputePass(
            ref graph,
            resetCacheAllocation
                ? "Cluster phase one raster/deform bins count"
                : "Cluster phase two raster/deform bins count",
            _pipelines.RasterDeformBinCount,
            count,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binningUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.BinnedDrawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.BinnedHardwareDrawArgs),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.SoftwareDispatchArgs),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.RasterReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformDispatchArgs),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters reserve = new(new DispatchArguments(
            Math.Max(1u, (Math.Max(frame.RasterBinCount, frame.DeformBinCount) + 127u) / 128u), 1, 1));
        _ = AddComputePass(
            ref graph,
            resetCacheAllocation
                ? "Cluster phase one raster/deform bins reserve"
                : "Cluster phase two raster/deform bins reserve",
            _pipelines.RasterDeformBinReserve,
            reserve,
            bindingCount,
            ClusterDispatchParameters.Record);

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
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.BinnedClusters, BinnedClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, drawArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, readOffsetArgs), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.RasterSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.DeformSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformBinnedClusters, BinnedClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters scatter = new(
            RequireDispatchIndirectLayout(), frame.BinningDispatchArgs, 0);
        _ = AddComputePass(
            ref graph,
            resetCacheAllocation
                ? "Cluster phase one raster/deform bins scatter"
                : "Cluster phase two raster/deform bins scatter",
            _pipelines.RasterDeformBinScatter,
            scatter,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);

        RecordHardwareArgumentCopy(ref graph, frame, resetCacheAllocation);
    }

    private void RecordTessellation(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        bool phaseOne)
    {
        RecordTessellationReset(ref graph, frame, phaseOne);

        for (int invocationIndex = 0; invocationIndex < rasterStages.Invocations.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = rasterStages.Invocations[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferId uniforms = UploadUniform(
                ref graph,
                new ClusterTessellationUniforms
                {
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                    CurrentBin = bin,
                    DispatchGroupWidth = 65_535u,
                    MaxSplitWorkItems = _options.MaxTessellationSplitWorkItems,
                    MaxDicedTriangles = _options.MaxDicedTriangles,
                    MaxFactor = _options.MaxTessellationFactor,
                    TargetEdgePixels = _options.TessellationTargetEdgePixels,
                },
                phaseOne
                    ? "Cluster phase one tessellation uniforms"
                    : "Cluster phase two tessellation uniforms");

            ulong factorOffset = checked((ulong)bin * ClusterIndirectAbi.DispatchBytes);
            ClusterIndirectDispatchParameters factor = new(
                RequireDispatchIndirectLayout(),
                frame.SoftwareDispatchArgs,
                factorOffset);
            GraphPassId factorPass = AddComputePass(
                ref graph,
                phaseOne
                    ? $"Cluster phase one tessellation factor bin {bin}"
                    : $"Cluster phase two tessellation factor bin {bin}",
                invocation.TessellationFactor,
                factor,
                0,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record);
            _tessellationFactorPassBindings.Add(new ClusterTessellationPassBinding(
                factorPass,
                checked((uint)invocationIndex),
                uniforms));
            _pageHeapReaderPasses.Add(factorPass);
        }

        RecordTessellationDice(ref graph, frame, rasterStages, packedViews, phaseOne);
        RecordTessellationVisibility(ref graph, frame, packedViews, phaseOne);
    }

    private void RecordTessellationReset(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        ClusterBufferClearParameters clear = new(frame.TessellationCounters);
        _ = TrackStagePass(graph.AddCopyFramePass(
            phaseOne
                ? "Reset Cluster phase one tessellation counters"
                : "Reset Cluster phase two tessellation counters",
            PassQueueSelection.AnyOfType(QueueType.Copy),
            clear,
            default,
            static (ref PassDefinition access, ref ClusterBufferClearParameters data) =>
                _ = access.Write(
                    data.Buffer,
                    BufferRange.Whole,
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete),
            ClusterBufferClearParameters.Record));
        RecordTessellationDispatchReset(
            ref graph,
            frame.TessellationDispatchInitialization,
            frame.TessellationDiceDispatchArgs,
            phaseOne ? "dice phase one" : "dice phase two");
        RecordTessellationDispatchReset(
            ref graph,
            frame.TessellationDispatchInitialization,
            frame.TessellationRasterDispatchArgs,
            phaseOne ? "visibility phase one" : "visibility phase two");
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
            4 * sizeof(uint));
        return TrackStagePass(graph.AddCopyFramePass(
            "Snapshot Cluster phase one tessellation counters",
            PassQueueSelection.AnyOfType(QueueType.Copy),
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
            ClusterBufferCopyParameters.Record));
    }

    private void RecordTessellationDispatchReset(
        ref RenderGraphEdit graph,
        GraphBufferId source,
        GraphBufferId destination,
        string label)
    {
        ClusterBufferCopyParameters copy = new(
            source,
            0,
            destination,
            0,
            ClusterIndirectAbi.DispatchBytes);
        _ = TrackStagePass(graph.AddCopyFramePass(
            $"Reset Cluster tessellation {label} dispatch",
            PassQueueSelection.AnyOfType(QueueType.Copy),
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
            ClusterBufferCopyParameters.Record));
    }

    private void RecordTessellationDice(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        bool phaseOne)
    {
        for (int invocationIndex = 0; invocationIndex < rasterStages.Invocations.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = rasterStages.Invocations[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferId uniforms = UploadUniform(
                ref graph,
                new ClusterTessellationUniforms
                {
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                    CurrentBin = bin,
                    DispatchGroupWidth = 65_535u,
                    MaxSplitWorkItems = _options.MaxTessellationSplitWorkItems,
                    MaxDicedTriangles = _options.MaxDicedTriangles,
                    MaxFactor = _options.MaxTessellationFactor,
                    TargetEdgePixels = _options.TessellationTargetEdgePixels,
                },
                phaseOne
                    ? "Cluster phase one dicing uniforms"
                    : "Cluster phase two dicing uniforms");
            ClusterIndirectDispatchParameters dice = new(
                RequireDispatchIndirectLayout(),
                frame.TessellationDiceDispatchArgs,
                0);
            GraphPassId dicePass = AddComputePass(
                ref graph,
                phaseOne
                    ? $"Cluster phase one bounded dicing bin {bin}"
                    : $"Cluster phase two bounded dicing bin {bin}",
                invocation.TessellationDice,
                dice,
                0,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record);
            _tessellationDicePassBindings.Add(new ClusterTessellationPassBinding(
                dicePass,
                checked((uint)invocationIndex),
                uniforms));
            _pageHeapReaderPasses.Add(dicePass);
        }
    }

    private void RecordTessellationVisibility(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId packedViews,
        bool phaseOne)
    {
        GraphBufferId uniforms = UploadUniform(
            ref graph,
            new ClusterTessellationVisibilityUniforms
            {
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                MaxDicedTriangles = _options.MaxDicedTriangles,
            },
            phaseOne
                ? "Cluster phase one diced visibility uniforms"
                : "Cluster phase two diced visibility uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(
                graph,
                packedViews,
                ClusterViewUniforms.SizeInBytes),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.DicedTriangles, DicedTriangleStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.TessellationCounters),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.Depth,
                Format.D32Float,
                new TextureSubresourceRange(
                    0,
                    1,
                    0,
                    frame.ViewCapacity,
                    TextureAspects.Depth),
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
            CreateStorageTextureView(
                graph,
                frame.VisBuffer,
                Format.R32UInt,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
            CreateStorageTextureView(
                graph,
                frame.SoftwareDepth,
                Format.R32UInt,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters visibility = new(
            RequireDispatchIndirectLayout(),
            frame.TessellationRasterDispatchArgs,
            0);
        _ = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one diced visibility raster"
                : "Cluster phase two diced visibility raster",
            _pipelines!.TessellationVisibilityRaster,
            visibility,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);
    }

    private void RecordHardwareArgumentCopy(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        ulong bytes = checked((ulong)frame.RasterBinCount * ClusterIndirectAbi.DrawBytes);
        ClusterBufferCopyParameters passData = new(
            frame.BinnedHardwareDrawArgs,
            0,
            frame.HardwareIndirectArgs,
            0,
            bytes);
        _ = TrackStagePass(graph.AddCopyFramePass(
            phaseOne
                ? "Copy Cluster phase one hardware arguments"
                : "Copy Cluster phase two hardware arguments",
            PassQueueSelection.AnyOfType(QueueType.Copy),
            passData,
            default,
            static (ref PassDefinition access, ref ClusterBufferCopyParameters data) =>
            {
                _ = access.Read(
                    data.Source,
                    new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource);
                _ = access.Write(
                    data.Destination,
                    new BufferRange(0, data.ByteCount),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            ClusterBufferCopyParameters.Record));
    }

    private void RecordDeform(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterDeformStageSet deformStages,
        bool phaseOne)
    {
        RecordDeformMaterialPasses(
            ref graph,
            frame,
            deformStages,
            phaseOne,
            pass: 0u,
            "size");

        GraphBufferId prefixUniforms = UploadUniform(
            ref graph,
            new ClusterDeformPrefixUniforms
            {
                MaxDeformCacheBytes = checked((uint)_options.DeformCacheBytes),
            },
            phaseOne
                ? "Cluster phase one deform cache prefix uniforms"
                : "Cluster phase two deform cache prefix uniforms");
        ulong prefixDispatchOffset = checked(
            (ulong)frame.DeformBinCount * ClusterIndirectAbi.DispatchBytes);

        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, prefixUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.DeformReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(
                graph,
                frame.DeformBinnedClusters,
                BinnedClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCacheRequests, 2 * sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCachePrefixOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCachePrefixGroups, 2 * sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheAllocationCounter, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters localPrefix = new(
            RequireDispatchIndirectLayout(),
            frame.DeformDispatchArgs,
            prefixDispatchOffset);
        _ = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one deform cache local prefix"
                : "Cluster phase two deform cache local prefix",
            _pipelines!.DeformCachePrefixLocal,
            localPrefix,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, prefixUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.DeformReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(
                graph,
                frame.DeformBinnedClusters,
                BinnedClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCacheRequests, 2 * sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCachePrefixOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCachePrefixGroups, 2 * sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheAllocationCounter, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters globalPrefix = new(new DispatchArguments(1, 1, 1));
        _ = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one deform cache global prefix"
                : "Cluster phase two deform cache global prefix",
            _pipelines.DeformCachePrefixGlobal,
            globalPrefix,
            bindingCount,
            ClusterDispatchParameters.Record);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, prefixUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.DeformReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(
                graph,
                frame.DeformBinnedClusters,
                BinnedClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCacheRequests, 2 * sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCachePrefixOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.DeformCachePrefixGroups, 2 * sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.CacheAllocationCounter, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters writeOffsets = new(
            RequireDispatchIndirectLayout(),
            frame.DeformDispatchArgs,
            prefixDispatchOffset);
        _ = AddComputePass(
            ref graph,
            phaseOne
                ? "Cluster phase one deform cache offset write"
                : "Cluster phase two deform cache offset write",
            _pipelines.DeformCachePrefixWrite,
            writeOffsets,
            bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(
                    data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect,
                    ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);

        RecordDeformMaterialPasses(
            ref graph,
            frame,
            deformStages,
            phaseOne,
            pass: 1u,
            "write");
    }

    private void RecordDeformMaterialPasses(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterDeformStageSet deformStages,
        bool phaseOne,
        uint pass,
        string passName)
    {
        for (int invocationIndex = 0; invocationIndex < deformStages.Invocations.Count; invocationIndex++)
        {
            ClusterDeformInvocation invocation = deformStages.Invocations[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferId pushConstants = UploadUniform(
                ref graph,
                new ClusterDeformUniforms
                {
                    MaxDeformCacheBytes = checked((uint)_options.DeformCacheBytes),
                    MaxClusterVertices = ClusterVertexCapacity,
                    CurrentBin = bin,
                    Pass = pass,
                },
                phaseOne
                    ? $"Cluster phase one deform {passName} bin uniforms"
                    : $"Cluster phase two deform {passName} bin uniforms");
            ulong indirectOffset = checked((ulong)bin * ClusterIndirectAbi.DispatchBytes);
            ClusterIndirectDispatchParameters passData = new(
                RequireDispatchIndirectLayout(),
                frame.DeformDispatchArgs,
                indirectOffset);
            GraphPassId deformPass = AddComputePass(
                ref graph,
                phaseOne
                    ? $"Cluster phase one deform {passName} bin"
                    : $"Cluster phase two deform {passName} bin",
                invocation.Pipeline,
                passData,
                0,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record);
            _deformPassBindings.Add(new ClusterDeformPassBinding(
                deformPass,
                checked((uint)invocationIndex),
                pushConstants));
            _pageHeapReaderPasses.Add(deformPass);
        }
    }

    private void RecordSoftwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        bool phaseOne)
    {
        for (int invocationIndex = 0; invocationIndex < rasterStages.Invocations.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = rasterStages.Invocations[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferId pushConstants = UploadUniform(
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
            ulong indirectOffset = checked(
                (ulong)(frame.RasterBinCount + bin) * ClusterIndirectAbi.DispatchBytes);
            int bindingCount = 0;
            ClusterSoftwareRasterPassData passData = new(
                RequireDispatchIndirectLayout(),
                frame.SoftwareDispatchArgs,
                indirectOffset);
            GraphPassId softwarePass = AddComputePass(
                ref graph,
                phaseOne
                    ? "Cluster phase one software raster bin"
                    : "Cluster phase two software raster bin",
                invocation.Software,
                passData,
                bindingCount,
                ClusterSoftwareRasterPassData.Declare,
                ClusterSoftwareRasterPassData.Record);
            _softwareRasterPassBindings.Add(new ClusterSoftwareRasterPassBinding(
                softwarePass,
                checked((uint)invocationIndex),
                pushConstants));
            _pageHeapReaderPasses.Add(softwarePass);
        }
    }

    private GraphPassId RecordDepthMerge(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        GraphDepthStencilViewId depthView = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(
                0,
                1,
                0,
                frame.ViewCapacity,
                TextureAspects.Depth),
            Format.D32Float,
            TextureViewDimension.Texture2DArray,
            label: phaseOne
                ? "Cluster phase one depth merge view"
                : "Cluster phase two depth merge view");
        ClusterRasterPipeline pipeline = _pipelines!.DepthMerge;
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.SoftwareDepth,
                Format.R32UInt,
                null,
                TextureViewDimension.Texture2DArray),
            PipelineSync.PixelShading));
        ClusterFullscreenParameters passData = new(frame.Width, frame.Height, 1);
        GraphPassId pass = AddRasterPass(
            ref graph,
            phaseOne
                ? "Cluster phase one software depth merge"
                : "Cluster phase two software depth merge",
            pipeline,
            passData,
            bindingCount,
            (ref PassDefinition access, ref ClusterFullscreenParameters _) =>
                access.DepthStencilAttachment(
                    depthView,
                    LoadType.Load,
                    StoreType.Store,
                    WriteCoverage.Partial,
                    1f,
                    LoadType.Discard,
                    StoreType.Discard,
                    WriteCoverage.Complete,
                    0),
            ClusterFullscreenParameters.Record);
        _depthMergePasses.Add(pass);
        return pass;
    }

    private void RecordHardwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterRasterStageSet rasterStages,
        GraphBufferId packedViews,
        bool phaseOne)
    {
        GraphColorAttachmentViewId visView = graph.CreateColorAttachmentView(
            frame.VisBuffer,
            new TextureSubresourceRange(
                0,
                1,
                0,
                frame.ViewCapacity,
                TextureAspects.Color),
            format: Format.R32UInt,
            dimension: TextureViewDimension.Texture2DArray,
            label: phaseOne
                ? "Cluster phase one hardware visibility view"
                : "Cluster phase two hardware visibility view");
        GraphDepthStencilViewId depthView = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(
                0,
                1,
                0,
                frame.ViewCapacity,
                TextureAspects.Depth),
            Format.D32Float,
            TextureViewDimension.Texture2DArray,
            label: phaseOne
                ? "Cluster phase one hardware depth view"
                : "Cluster phase two hardware depth view");
        GraphBufferId drawUniforms = UploadUniform(
            ref graph,
            new ClusterDrawUniforms
            {
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
            },
            phaseOne
                ? "Cluster phase one hardware draw uniforms"
                : "Cluster phase two hardware draw uniforms");

        for (int invocationIndex = 0; invocationIndex < rasterStages.Invocations.Count; invocationIndex++)
        {
            ClusterRasterInvocation invocation = rasterStages.Invocations[invocationIndex];
            uint bin = invocation.BinIndex;
            GraphBufferId dispatchUniforms = UploadUniform(
                ref graph,
                new ClusterDrawDispatchUniforms
                {
                    DrawArgsByteOffset = checked(bin * ClusterIndirectAbi.DrawStride),
                },
                phaseOne
                    ? "Cluster phase one hardware bin dispatch uniforms"
                    : "Cluster phase two hardware bin dispatch uniforms");
            ulong offset = checked((ulong)bin * ClusterIndirectAbi.DrawBytes);
            ClusterRasterPipeline pipeline = invocation.Hardware;
            int bindingCount = 0;
            ClusterHardwareRasterPassData passData = new(
                RequireDrawIndirectLayout(),
                frame.HardwareIndirectArgs,
                offset,
                frame.Width,
                frame.Height);
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
                    ClusterHardwareRasterPassData.Declare(ref access, ref data);
                },
                ClusterHardwareRasterPassData.Record);
            _hardwareRasterPassBindings.Add(new ClusterHardwareRasterPassBinding(
                hardwarePass,
                checked((uint)invocationIndex),
                drawUniforms,
                dispatchUniforms));
            _pageHeapReaderPasses.Add(hardwarePass);
        }
    }

    private GraphPassId RecordHiZ(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        int mipCount = _history!.HiZMipCount;
        if (mipCount < 2)
            throw new InvalidOperationException("Cluster HiZ requires at least two mip levels.");
        TextureSubresourceRange depthRange = new(
            0,
            1,
            0,
            frame.ViewCapacity,
            TextureAspects.Depth);
        TextureSubresourceRange mip0 = Mip(0, frame.ViewCapacity);
        TextureSubresourceRange mip1 = Mip(1, frame.ViewCapacity);
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.Depth,
                Format.D32Float,
                depthRange,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
            CreateStorageTextureView(
                graph,
                frame.CurrentHiZ,
                Format.R32Float,
                mip0,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
            CreateStorageTextureView(
                graph,
                frame.CurrentHiZ,
                Format.R32Float,
                mip1,
                TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        ClusterDispatchParameters first = new(new DispatchArguments(
            Groups(MipExtent(frame.Width, 1), 8),
            Groups(MipExtent(frame.Height, 1), 8),
            1));
        GraphPassId firstPass = AddComputePass(
            ref graph,
            phaseOne
                ? "Build Cluster phase one HiZ mips 0-1"
                : "Build Cluster final HiZ mips 0-1",
            _pipelines!.HiZFirst,
            first,
            bindingCount,
            ClusterDispatchParameters.Record);
        _layerDispatches.Add(new ClusterLayerDispatch(
            firstPass,
            Groups(MipExtent(frame.Width, 1), 8),
            Groups(MipExtent(frame.Height, 1), 8)));

        int sourceMip = 1;
        while (sourceMip + 2 < mipCount)
        {
            int middleMip = sourceMip + 1;
            int destinationMip = sourceMip + 2;
            bindingCount = 0;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(sourceMip, frame.ViewCapacity),
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Read));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(middleMip, frame.ViewCapacity),
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(destinationMip, frame.ViewCapacity),
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            ClusterDispatchParameters pair = new(new DispatchArguments(
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8),
                1));
            GraphPassId pairPass = AddComputePass(
                ref graph,
                phaseOne
                    ? "Build Cluster phase one HiZ mip pair"
                    : "Build Cluster final HiZ mip pair",
                _pipelines.HiZDownsampleTwo,
                pair,
                bindingCount,
                ClusterDispatchParameters.Record);
            _layerDispatches.Add(new ClusterLayerDispatch(
                pairPass,
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8)));
            sourceMip = destinationMip;
        }
        if (sourceMip + 1 < mipCount)
        {
            int destinationMip = sourceMip + 1;
            bindingCount = 0;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(sourceMip, frame.ViewCapacity),
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Read));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(destinationMip, frame.ViewCapacity),
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            ClusterDispatchParameters final = new(new DispatchArguments(
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8),
                1));
            GraphPassId finalPass = AddComputePass(
                ref graph,
                phaseOne
                    ? "Build Cluster phase one HiZ final mip"
                    : "Build Cluster final HiZ final mip",
                _pipelines.HiZDownsample,
                final,
                bindingCount,
                ClusterDispatchParameters.Record);
            _layerDispatches.Add(new ClusterLayerDispatch(
                finalPass,
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8)));
        }
        return firstPass;
    }

    private void RecordShade(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterShadeStageSet shadeStages,
        bool installCommonPasses)
    {
        GraphBufferId binUniforms = UploadUniform(
            ref graph,
            new ClusterShadeBinUniforms
            {
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                ShadeBinCount = frame.ShadeBinCount,
                SlotCapacity = frame.SlotCapacity,
            },
            "Cluster shade bin uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters clear = new(new DispatchArguments(
            Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        _ = AddComputePass(ref graph, "Clear Cluster shade bins",
            _pipelines!.ShadeBinClearPrepare, clear, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true);

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(
                graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
            PipelineSync.ComputeShading));
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
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.ShadeSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        ClusterDispatchParameters count = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphPassId countPass = AddComputePass(ref graph, "Count Cluster shade bins",
            _pipelines.ShadeBinCount, count, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true);
        _layerDispatches.Add(new ClusterLayerDispatch(
            countPass,
            Groups(frame.Width, 8),
            Groups(frame.Height, 8)));

        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, binUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeBinOffsets, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeIndirectArgs),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeReserveCounters, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters reserve = new(new DispatchArguments(
            Math.Max(1u, (frame.ShadeBinCount + 127u) / 128u), 1, 1));
        _ = AddComputePass(ref graph, "Reserve Cluster shade bins",
            _pipelines.ShadeBinReserve, reserve, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true);

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
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.ShadeSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.ShadeScatterCounts, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddPageHeapBinding(ref bindingCount, CreateReadOnlyBufferView(graph, frame.PageHeap));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.PixelCoordinates, 2 * sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters scatter = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphPassId scatterPass = AddComputePass(ref graph, "Scatter Cluster shade bins",
            _pipelines.ShadeBinScatter, scatter, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true);
        _layerDispatches.Add(new ClusterLayerDispatch(
            scatterPass,
            Groups(frame.Width, 8),
            Groups(frame.Height, 8)));

        GraphPassId resolvePass;
        if (installCommonPasses)
        {
            bool trackDynamic = _recordStagePasses;
            _recordStagePasses = false;
            try
            {
                GraphBufferId resolveUniforms = UploadUniform(
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
                    PipelineSync.ComputeShading));
                ClusterDispatchParameters resolve = new(new DispatchArguments(
                    Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
                resolvePass = AddComputePass(ref graph, "Resolve Cluster visibility background",
                    _pipelines.Resolve, resolve, bindingCount,
                    ClusterDispatchParameters.Record, asyncCompute: true);
                _layerDispatches.Add(new ClusterLayerDispatch(
                    resolvePass,
                    Groups(frame.Width, 8),
                    Groups(frame.Height, 8)));
                _shadeResolvePass = resolvePass;
            }
            finally
            {
                _recordStagePasses = trackDynamic;
            }
        }
        else
        {
            resolvePass = _shadeResolvePass;
        }

        _shadePasses = new GraphPassId[shadeStages.Invocations.Count];
        GraphPassId predecessor = resolvePass;
        for (int invocationIndex = 0; invocationIndex < shadeStages.Invocations.Count; invocationIndex++)
        {
            ClusterShadeInvocation invocation = shadeStages.Invocations[invocationIndex];
            uint bin = invocation.BinIndex;
            var dispatch = new ClusterIndirectDispatchParameters(
                RequireDispatchIndirectLayout(),
                frame.ShadeIndirectArgs,
                checked((ulong)bin * ClusterIndirectAbi.DispatchBytes));
            GraphPassId pass = TrackStagePass(graph.AddComputeFramePass(
                $"Shade Cluster bin {bin}",
                PassQueueSelection.AnyOfType(
                    _options.EnableAsyncCompute ? QueueType.Compute : QueueType.Graphics),
                dispatch,
                default,
                invocation.Pipeline.Pipeline,
                ShaderVariableLayout.Null,
                default,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record));
            graph.OrderAfter(pass, predecessor);
            _shadePasses[invocationIndex] = pass;
            predecessor = pass;
        }
    }

    private GraphPassId AddRasterPass<TState>(
        ref RenderGraphEdit graph,
        string name,
        ClusterRasterPipeline pipeline,
        in TState state,
        int bindingCount,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback)
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
                default,
                pipeline.Pipeline,
                pipeline.ParameterLayout,
                bindings,
                declaration,
                callback));
            if (readsPageHeap)
                _pageHeapReaderPasses.Add(pass);
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
        RasterFrameCallback<TState> callback) =>
        AddRasterPass(ref graph, name, pipeline, state, bindingCount,
            DeclareNoAdditionalAccess<TState>, callback);

    private GraphPassId AddComputePass<TState>(
        ref RenderGraphEdit graph,
        string name,
        ClusterComputePipeline pipeline,
        in TState state,
        int bindingCount,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback,
        bool asyncCompute = false)
    {
        bool readsPageHeap = _bindingScratchReadsPageHeap;
        _bindingScratchReadsPageHeap = false;
        Span<GraphParameterResourceBinding> bindings =
            _renderGraphBindings.AsSpan(0, bindingCount);
        try
        {
            GraphPassId pass = TrackStagePass(graph.AddComputeFramePass(
                name,
                PassQueueSelection.AnyOfType(_options.EnableAsyncCompute && asyncCompute
                    ? QueueType.Compute
                    : QueueType.Graphics),
                state,
                default,
                pipeline.Pipeline,
                pipeline.ParameterLayout,
                bindings,
                declaration,
                callback));
            if (readsPageHeap)
                _pageHeapReaderPasses.Add(pass);
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
        bool asyncCompute = false) =>
        AddComputePass(ref graph, name, pipeline, state, bindingCount,
            DeclareNoAdditionalAccess<TState>, callback, asyncCompute);

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

    private static TextureSubresourceRange Mip(int mip, uint viewCapacity)
        => new(checked((uint)mip), 1, 0, viewCapacity, TextureAspects.Color);

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
