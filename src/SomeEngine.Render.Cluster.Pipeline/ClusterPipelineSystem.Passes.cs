using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
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
        ClusterShadeStageSet shadeStages,
        in RenderView declarationView,
        in ClusterViewUniforms declarationUniforms)
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
            GraphBufferId viewConstants = UploadUniform(
                ref graph,
                declarationUniforms,
                "Cluster view uniforms");

            _traversalPass = RecordTraversal(ref graph, frame, viewConstants, 1);
            RecordCullPhaseOne(ref graph, frame, viewConstants);
            RecordRasterPhase(
                ref graph,
                frame,
                deformStages,
                in declarationView,
                frame.DrawArgs,
                frame.ReadOffsetZero,
                resetCacheAllocation: true);
            RecordHiZ(ref graph, frame, phaseOne: true);

            RecordCullPhaseTwo(ref graph, frame, viewConstants);
            RecordRasterPhase(
                ref graph,
                frame,
                deformStages,
                in declarationView,
                frame.Phase2DrawArgs,
                frame.DrawArgs,
                resetCacheAllocation: false);
            RecordHiZ(ref graph, frame, phaseOne: false);

            RecordShade(ref graph, frame, shadeStages, in declarationView);
            RecordPageFaultReadback(ref graph, frame);
            RecordFrameMetricsReadback(ref graph, frame);
            _outputs = new ClusterPipelineOutputs(
                frame.VisBuffer,
                frame.Depth,
                frame.SceneColor,
                frame.Width,
                frame.Height);
        }
        finally
        {
            _frameResourceScratch.End();
        }
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
            frame.PageFaultReadback,
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
        GraphBufferId uniforms,
        uint instanceCount)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.Bvh, stride: 64), PipelineSync.ComputeShading));
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

    private void RecordCullPhaseOne(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId uniforms)
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
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PageHeap), PipelineSync.ComputeShading));
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
            CreateSampledTextureView(graph, frame.PreviousHiZ, Format.R32Float, null),
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
        _ = AddComputePass(ref graph, "Cluster phase-one cull", _pipelines.CullPhase1,
            cull, bindingCount,
            static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                _ = access.Read(data.Arguments,
                    new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record);
    }

    private void RecordCullPhaseTwo(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        GraphBufferId uniforms)
    {
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PageHeap), PipelineSync.ComputeShading));
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
            CreateSampledTextureView(graph, frame.CurrentHiZ, Format.R32Float, null),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.Phase2DrawArgs), PipelineSync.ComputeShading));
        ClusterIndirectDispatchParameters cull = new(
            RequireDispatchIndirectLayout(), frame.Phase2CandidateArgs, 0);
        _ = AddComputePass(ref graph, "Cluster phase-two cull", _pipelines!.CullPhase2,
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
        in RenderView view,
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
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PageHeap), PipelineSync.ComputeShading));
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
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PageHeap), PipelineSync.ComputeShading));
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

        RecordDeform(ref graph, frame, deformStages, resetCacheAllocation);
        RecordSoftwareRaster(ref graph, frame, in view, resetCacheAllocation);
        RecordDepthMerge(ref graph, frame, resetCacheAllocation);
        RecordHardwareRaster(ref graph, frame, in view, resetCacheAllocation);
    }

    private static void RecordHardwareArgumentCopy(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        ulong bytes = checked((ulong)frame.RasterBinCount * ClusterIndirectAbi.DrawBytes);
        ClusterBufferCopyParameters passData = new(
            frame.BinnedHardwareDrawArgs,
            frame.HardwareIndirectArgs,
            bytes);
        _ = graph.AddCopyFramePass(
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
            ClusterBufferCopyParameters.Record);
    }

    private void RecordDeform(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterDeformStageSet deformStages,
        bool phaseOne)
    {
        for (uint bin = 0; bin < frame.DeformBinCount; bin++)
        {
            ClusterDeformInvocation invocation =
                deformStages.Invocations[checked((int)bin)];
            GraphBufferId pushConstants = UploadUniform(
                ref graph,
                new ClusterDeformUniforms
                {
                    MaxDeformCacheBytes = checked((uint)_options.DeformCacheBytes),
                    MaxClusterVertices = ClusterVertexCapacity,
                    CurrentBin = bin,
                },
                phaseOne
                    ? "Cluster phase one deform bin uniforms"
                    : "Cluster phase two deform bin uniforms");
            ulong indirectOffset = checked((ulong)bin * ClusterIndirectAbi.DispatchBytes);
            int bindingCount = 0;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.InstanceData),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, pushConstants),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.PageHeap),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.DeformBinnedClusters,
                    BinnedClusterStride),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.DeformBinMeta,
                    DeformBinStride),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.DeformCache),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.CacheAllocationCounter,
                    sizeof(uint)),
                PipelineSync.ComputeShading));
            ClusterIndirectDispatchParameters passData = new(
                RequireDispatchIndirectLayout(),
                frame.DeformDispatchArgs,
                indirectOffset);
            _ = AddComputePass(
                ref graph,
                phaseOne
                    ? "Cluster phase one deform bin"
                    : "Cluster phase two deform bin",
                invocation.Pipeline,
                passData,
                bindingCount,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record);
        }
    }

    private void RecordSoftwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        in RenderView view,
        bool phaseOne)
    {
        for (uint bin = 0; bin < frame.RasterBinCount; bin++)
        {
            GraphBufferId pushConstants = UploadUniform(
                ref graph,
                new ClusterSoftwareRasterUniforms
                {
                    ViewProj = view.View * view.Projection,
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
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.InstanceData),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, pushConstants),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.PageHeap),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.BinnedClusters,
                    BinnedClusterStride),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.RasterBinMeta,
                    RasterBinStride),
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
                        1,
                        TextureAspects.Depth)),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.VisBuffer,
                    Format.R32UInt,
                    null),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.SoftwareDepth,
                    Format.R32UInt,
                    null),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.DeformCache),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.CacheOffsets,
                    sizeof(uint)),
                PipelineSync.ComputeShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
                CreateStorageBufferView(graph, frame.SoftwareDebug),
                PipelineSync.ComputeShading));
            ClusterIndirectDispatchParameters passData = new(
                RequireDispatchIndirectLayout(),
                frame.SoftwareDispatchArgs,
                indirectOffset);
            _ = AddComputePass(
                ref graph,
                phaseOne
                    ? "Cluster phase one software raster bin"
                    : "Cluster phase two software raster bin",
                _pipelines!.SoftwareRaster,
                passData,
                bindingCount,
                static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) =>
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record);
        }
    }

    private void RecordDepthMerge(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        GraphDepthStencilViewId depthView = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            Format.D32Float,
            TextureViewDimension.Texture2D,
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
                null),
            PipelineSync.PixelShading));
        ClusterFullscreenParameters passData = new(frame.Width, frame.Height);
        _ = AddRasterPass(
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
    }

    private void RecordHardwareRaster(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        in RenderView view,
        bool phaseOne)
    {
        GraphColorAttachmentViewId visView = graph.CreateColorAttachmentView(
            frame.VisBuffer,
            format: Format.R32UInt,
            dimension: TextureViewDimension.Texture2D,
            label: phaseOne
                ? "Cluster phase one hardware visibility view"
                : "Cluster phase two hardware visibility view");
        GraphDepthStencilViewId depthView = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            Format.D32Float,
            TextureViewDimension.Texture2D,
            label: phaseOne
                ? "Cluster phase one hardware depth view"
                : "Cluster phase two hardware depth view");
        GraphBufferId drawUniforms = UploadUniform(
            ref graph,
            new ClusterDrawUniforms
            {
                ViewProj = view.View * view.Projection,
                View = view.View,
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
            },
            phaseOne
                ? "Cluster phase one hardware draw uniforms"
                : "Cluster phase two hardware draw uniforms");

        for (uint bin = 0; bin < frame.RasterBinCount; bin++)
        {
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
            ClusterRasterPipeline pipeline = _pipelines!.HardwareRaster;
            int bindingCount = 0;
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.InstanceData),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, drawUniforms),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
                CreateConstantBufferView(graph, dispatchUniforms),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.PageHeap),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.BinnedClusters,
                    BinnedClusterStride),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.BinnedHardwareDrawArgs),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.DeformCache),
                PipelineSync.AllShading));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
                CreateReadOnlyBufferView(graph, frame.CacheOffsets, sizeof(uint)),
                PipelineSync.AllShading));
            ClusterIndirectDrawParameters passData = new(
                RequireDrawIndirectLayout(),
                frame.HardwareIndirectArgs,
                offset,
                frame.Width,
                frame.Height);
            _ = AddRasterPass(
                ref graph,
                phaseOne
                    ? "Cluster phase one hardware visibility bin"
                    : "Cluster phase two hardware visibility bin",
                pipeline,
                passData,
                bindingCount,
                (ref PassDefinition access, ref ClusterIndirectDrawParameters data) =>
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
                    _ = access.Read(
                        data.Arguments,
                        new BufferRange(data.Offset, ClusterIndirectAbi.DrawBytes),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument);
                },
                ClusterIndirectDrawParameters.Record);
        }
    }

    private void RecordHiZ(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        bool phaseOne)
    {
        int mipCount = _history!.HiZMipCount;
        if (mipCount < 2)
            throw new InvalidOperationException("Cluster HiZ requires at least two mip levels.");
        TextureSubresourceRange depthRange = new(0, 1, 0, 1, TextureAspects.Depth);
        TextureSubresourceRange mip0 = Mip(0);
        TextureSubresourceRange mip1 = Mip(1);
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.Depth,
                Format.D32Float,
                depthRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
            CreateStorageTextureView(
                graph,
                frame.CurrentHiZ,
                Format.R32Float,
                mip0),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
            CreateStorageTextureView(
                graph,
                frame.CurrentHiZ,
                Format.R32Float,
                mip1),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        ClusterDispatchParameters first = new(new DispatchArguments(
            Groups(MipExtent(frame.Width, 1), 8),
            Groups(MipExtent(frame.Height, 1), 8),
            1));
        _ = AddComputePass(
            ref graph,
            phaseOne
                ? "Build Cluster phase one HiZ mips 0-1"
                : "Build Cluster final HiZ mips 0-1",
            _pipelines!.HiZFirst,
            first,
            bindingCount,
            ClusterDispatchParameters.Record);

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
                    Mip(sourceMip)),
                PipelineSync.ComputeShading,
                GraphAccessMode.Read));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(middleMip)),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(destinationMip)),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            ClusterDispatchParameters pair = new(new DispatchArguments(
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8),
                1));
            _ = AddComputePass(
                ref graph,
                phaseOne
                    ? "Build Cluster phase one HiZ mip pair"
                    : "Build Cluster final HiZ mip pair",
                _pipelines.HiZDownsampleTwo,
                pair,
                bindingCount,
                ClusterDispatchParameters.Record);
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
                    Mip(sourceMip)),
                PipelineSync.ComputeShading,
                GraphAccessMode.Read));
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(destinationMip)),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            ClusterDispatchParameters final = new(new DispatchArguments(
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8),
                1));
            _ = AddComputePass(
                ref graph,
                phaseOne
                    ? "Build Cluster phase one HiZ final mip"
                    : "Build Cluster final HiZ final mip",
                _pipelines.HiZDownsample,
                final,
                bindingCount,
                ClusterDispatchParameters.Record);
        }
    }

    private void RecordShade(
        ref RenderGraphEdit graph,
        in FrameResources frame,
        ClusterShadeStageSet shadeStages,
        in RenderView view)
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
            CreateSampledTextureView(graph, frame.VisBuffer, Format.R32UInt, null),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.ShadeSlots, sizeof(uint)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PageHeap), PipelineSync.ComputeShading));
        ClusterDispatchParameters count = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        _ = AddComputePass(ref graph, "Count Cluster shade bins",
            _pipelines.ShadeBinCount, count, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true);

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
            CreateSampledTextureView(graph, frame.VisBuffer, Format.R32UInt, null),
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
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PageHeap), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.PixelCoordinates, sizeof(uint)),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters scatter = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        _ = AddComputePass(ref graph, "Scatter Cluster shade bins",
            _pipelines.ShadeBinScatter, scatter, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true);

        Matrix4x4 viewProjection = view.View * view.Projection;
        GraphBufferId resolveUniforms = UploadUniform(
            ref graph,
            new ClusterResolveUniforms
            {
                ViewProj = viewProjection,
                View = view.View,
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
            },
            "Cluster resolve uniforms");
        bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.InstanceData), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(
                graph, frame.InstanceProperties, frame.InstancePropertiesRange),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, resolveUniforms), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(graph, frame.VisBuffer, Format.R32UInt, null),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.SampledTexture(
            CreateSampledTextureView(
                graph,
                frame.Depth,
                Format.D32Float,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth)),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.VisibleClusters, VisibleClusterStride),
            PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ReadOnlyBuffer(
            CreateReadOnlyBufferView(graph, frame.PageHeap), PipelineSync.ComputeShading));
        AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
            CreateStorageTextureView(
                graph, frame.SceneColor, Format.R16G16B16A16Float, null),
            PipelineSync.ComputeShading));
        ClusterDispatchParameters resolve = new(new DispatchArguments(
            Groups(frame.Width, 8), Groups(frame.Height, 8), 1));
        GraphPassId resolvePass = AddComputePass(ref graph, "Resolve Cluster visibility background",
            _pipelines.Resolve, resolve, bindingCount,
            ClusterDispatchParameters.Record, asyncCompute: true);

        _shadePasses = new GraphPassId[shadeStages.Invocations.Count];
        GraphPassId predecessor = resolvePass;
        for (int index = 0; index < shadeStages.Invocations.Count; index++)
        {
            ClusterShadeInvocation invocation = shadeStages.Invocations[index];
            var dispatch = new ClusterIndirectDispatchParameters(
                RequireDispatchIndirectLayout(),
                frame.ShadeIndirectArgs,
                checked((ulong)index * ClusterIndirectAbi.DispatchBytes));
            GraphPassId pass = graph.AddComputeFramePass(
                $"Shade Cluster material invocation {index}",
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
                ClusterIndirectDispatchParameters.Record);
            graph.OrderAfter(pass, predecessor);
            _shadePasses[index] = pass;
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
        Span<GraphParameterResourceBinding> bindings =
            _renderGraphBindings.AsSpan(0, bindingCount);
        try
        {
            return graph.AddRasterFramePass(
                name,
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                state,
                default,
                pipeline.Pipeline,
                pipeline.ParameterLayout,
                bindings,
                declaration,
                callback);
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
        Span<GraphParameterResourceBinding> bindings =
            _renderGraphBindings.AsSpan(0, bindingCount);
        try
        {
            return graph.AddComputeFramePass(
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
                callback);
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

    private static TextureSubresourceRange Mip(int mip)
        => new(checked((uint)mip), 1, 0, 1, TextureAspects.Color);

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
