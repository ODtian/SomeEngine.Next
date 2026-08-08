using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterRendererSystem
{
    private void RecordFrame(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in ClusterRenderTarget target,
        in ClusterRenderBinding binding,
        ClusterMaterialSnapshot snapshot,
        in RenderView view,
        in ClusterViewUniforms viewUniforms,
        LightCollector lights,
        bool hasHistory)
    {
        using FrameResources frame = CreateFrameResources(
            ref graph,
            in target,
            in binding,
            snapshot,
            lights);
        if (_history!.RequiresInitialization)
            AuthorHistoryInitialization(ref graph, frame);
        BufferHandle viewConstants = UploadUniform(ref graph, viewUniforms, "Cluster view uniforms");

        RecordTraversal(ref graph, frame, viewConstants, checked((uint)binding.DispatchExtent));
        RecordCullPhaseOne(ref graph, frame, viewConstants);
        RecordRasterPhase(
            ref graph,
            frame,
            in view,
            frame.DrawArgs,
            frame.ReadOffsetZero,
            resetCacheAllocation: true);
        RecordHiZ(ref graph, frame, phaseOne: true);

        RecordCullPhaseTwo(ref graph, frame, viewConstants);
        RecordRasterPhase(
            ref graph,
            frame,
            in view,
            frame.Phase2DrawArgs,
            frame.DrawArgs,
            resetCacheAllocation: false);
        RecordHiZ(ref graph, frame, phaseOne: false);

        RecordShade(ref graph, frame, in view, hasHistory);
        TextureHandle postScene = RecordTemporal(ref graph, frame, hasHistory);
        RecordHistoryCopies(ref graph, frame, postScene);
        RecordPageFaultReadback(ref graph, frame);
        RecordTonemap(ref graph, frame, postScene);
        RecordDiagnosticsReadback(ref graph, frame);
    }

    private void RecordDiagnosticsReadback(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame)
    {
        if (!_options.EnableDiagnosticsReadback)
            return;

        using IUnsafeRenderGraphBuilder builder =
            graph.AddUnsafePass<ClusterDiagnosticsReadbackPassData>(
                "Read back Cluster frame diagnostics",
                out ClusterDiagnosticsReadbackPassData passData);
        passData.CandidateCount = frame.CandidateCount;
        passData.CandidateArgs = frame.CandidateArgs;
        passData.DrawArgs = frame.DrawArgs;
        passData.Phase2CandidateCount = frame.Phase2CandidateCount;
        passData.Phase2CandidateArgs = frame.Phase2CandidateArgs;
        passData.Phase2DrawArgs = frame.Phase2DrawArgs;
        passData.RasterReserve = frame.RasterReserveCounters;
        passData.ShadeReserve = frame.ShadeReserveCounters;
        passData.DeformReserve = frame.DeformReserveCounters;
        passData.CacheAllocation = frame.CacheAllocationCounter;
        passData.SoftwareDebug = frame.SoftwareDebug;
        passData.Destination = frame.DiagnosticsReadback;
        builder.UseBuffer(passData.CandidateCount, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.CandidateArgs, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.DrawArgs, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.Phase2CandidateCount, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.Phase2CandidateArgs, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.Phase2DrawArgs, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.RasterReserve, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.ShadeReserve, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.DeformReserve, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.CacheAllocation, GraphResourceUsage.CopySource);
        builder.UseBuffer(passData.SoftwareDebug, GraphResourceUsage.CopySource);
        builder.UseBuffer(
            passData.Destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.WriteAll,
            new BufferRange(0, DiagnosticsReadbackByteSize));
        builder.SetRenderFunc<ClusterDiagnosticsReadbackPassData>(
            static (data, context) =>
            {
                context.CopyBufferRegion(data.CandidateCount, 0, data.Destination, CandidateCountReadbackOffset, sizeof(uint));
                context.CopyBufferRegion(data.CandidateArgs, 0, data.Destination, CandidateArgsReadbackOffset, 12);
                context.CopyBufferRegion(data.DrawArgs, 0, data.Destination, DrawArgsReadbackOffset, 16);
                context.CopyBufferRegion(data.Phase2CandidateCount, 0, data.Destination, Phase2CandidateCountReadbackOffset, sizeof(uint));
                context.CopyBufferRegion(data.Phase2CandidateArgs, 0, data.Destination, Phase2CandidateArgsReadbackOffset, 12);
                context.CopyBufferRegion(data.Phase2DrawArgs, 0, data.Destination, Phase2DrawArgsReadbackOffset, 16);
                context.CopyBufferRegion(data.RasterReserve, 2 * sizeof(uint), data.Destination, RasterReserveReadbackOffset, 2 * sizeof(uint));
                context.CopyBufferRegion(data.ShadeReserve, 0, data.Destination, ShadeReserveReadbackOffset, sizeof(uint));
                context.CopyBufferRegion(data.DeformReserve, sizeof(uint), data.Destination, DeformReserveReadbackOffset, sizeof(uint));
                context.CopyBufferRegion(data.CacheAllocation, 0, data.Destination, CacheAllocationReadbackOffset, 2 * sizeof(uint));
                context.CopyBufferRegion(data.SoftwareDebug, 0, data.Destination, SoftwareDebugReadbackOffset, sizeof(uint));
            });
    }

    private static void RecordPageFaultReadback(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame)
    {
        ulong byteCount = checked(
            sizeof(uint) + (ulong)frame.PageFaultCapacity * sizeof(uint));
        BufferRange range = new(0, byteCount);
        using IUnsafeRenderGraphBuilder builder =
            graph.AddUnsafePass<ClusterBufferCopyPassData>(
                "Read back Cluster page faults",
                out ClusterBufferCopyPassData passData);
        passData.Source = frame.PageFaults;
        passData.Destination = frame.PageFaultReadback;
        passData.ByteCount = byteCount;
        builder.UseBuffer(
            passData.Source,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            range);
        builder.UseBuffer(
            passData.Destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.WriteAll,
            range);
        builder.SetRenderFunc<ClusterBufferCopyPassData>(
            static (data, context) =>
                context.CopyBufferRegion(
                    data.Source,
                    0,
                    data.Destination,
                    0,
                    data.ByteCount));
    }

    private void RecordTraversal(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        BufferHandle uniforms,
        uint instanceCount)
    {
        using IComputeRenderGraphBuilder builder =
            AddComputePass<ClusterDispatchPassData>(
                ref graph,
                "Cluster BVH traversal",
                _shaders!.Traversal,
                out ClusterDispatchPassData passData);
        passData.Dispatch =
            new ClusterDispatch(checked((instanceCount + 63u) / 64u), 1, 1);
        builder.UseBuffer(CreateConstantBufferView(graph, uniforms));
        builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.Bvh, stride: 64));
        builder.UseBuffer(CreateStorageBufferView(graph, frame.CandidateArgs), GraphAccess.ReadWrite);
        builder.UseBuffer(
            CreateConstantBufferView(
                graph,
                frame.InstanceProperties,
                frame.InstancePropertiesRange));
        builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
        builder.UseBuffer(
            CreateStorageBufferView(graph, frame.CandidateClusters, CandidateStride),
            GraphAccess.Write);
        builder.UseBuffer(
            CreateStorageBufferView(graph, frame.CandidateCount, sizeof(uint)),
            GraphAccess.ReadWrite);
        builder.UseBuffer(
            CreateStorageBufferView(graph, frame.PageFaults),
            GraphAccess.ReadWrite);
        builder.SetRenderFunc<ClusterDispatchPassData>(
            ClusterDispatchPassData.Execute);
    }

    private void RecordCullPhaseOne(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        BufferHandle uniforms)
    {
        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Clear Cluster phase-one cull",
                   _shaders!.CullClearPhase1,
                   out ClusterDispatchPassData passData))
        {
            passData.Dispatch = new ClusterDispatch(1, 1, 1);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DrawArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.Phase2CandidateCount,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.Phase2CandidateArgs),
                GraphAccess.ReadWrite);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterIndirectPassData>(
                   ref graph,
                   "Cluster phase-one cull",
                   _shaders.CullPhase1,
                   out ClusterIndirectPassData passData))
        {
            passData.IndirectArguments = frame.CandidateArgs;
            passData.IndirectOffset = 0;
            builder.UseBuffer(CreateConstantBufferView(graph, uniforms));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DrawArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride),
                GraphAccess.Write);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.CandidateClusters,
                    CandidateStride));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.Phase2CandidateClusters,
                    CandidateStride),
                GraphAccess.Write);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.CandidateCount,
                    sizeof(uint)));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.Phase2CandidateCount,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.PreviousHiZ,
                    Format.R32Float,
                    null));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.Phase2CandidateArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                passData.IndirectArguments,
                GraphResourceUsage.IndirectArgument,
                GraphAccess.Read,
                new BufferRange(0, ClusterIndirectAbi.DispatchBytes));
            builder.SetRenderFunc<ClusterIndirectPassData>(
                ClusterIndirectPassData.Execute);
        }
    }

    private void RecordCullPhaseTwo(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        BufferHandle uniforms)
    {
        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Clear Cluster phase-two cull",
                   _shaders!.CullClearPhase2,
                   out ClusterDispatchPassData passData))
        {
            passData.Dispatch = new ClusterDispatch(1, 1, 1);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.Phase2DrawArgs),
                GraphAccess.ReadWrite);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterIndirectPassData>(
                   ref graph,
                   "Cluster phase-two cull",
                   _shaders.CullPhase2,
                   out ClusterIndirectPassData passData))
        {
            passData.IndirectArguments = frame.Phase2CandidateArgs;
            passData.IndirectOffset = 0;
            builder.UseBuffer(CreateConstantBufferView(graph, uniforms));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DrawArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride),
                GraphAccess.Write);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.Phase2CandidateClusters,
                    CandidateStride));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.Phase2CandidateCount,
                    sizeof(uint)));
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    null));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.Phase2DrawArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                passData.IndirectArguments,
                GraphResourceUsage.IndirectArgument,
                GraphAccess.Read,
                new BufferRange(0, ClusterIndirectAbi.DispatchBytes));
            builder.SetRenderFunc<ClusterIndirectPassData>(
                ClusterIndirectPassData.Execute);
        }
    }

    private void RecordRasterPhase(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        in RenderView view,
        BufferHandle drawArgs,
        BufferHandle readOffsetArgs,
        bool resetCacheAllocation)
    {
        BufferHandle binningUniforms = UploadUniform(
            ref graph,
            new ClusterRasterDeformBinningUniforms
            {
                RasterMaxBins = frame.MaterialCount,
                DeformMaxBins = frame.MaterialCount,
                SlotCapacity = frame.SlotCapacity,
                RasterBinFieldIndex = ClusterMaterialTable.RasterBinField,
                DeformBinFieldIndex = ClusterMaterialTable.DeformBinField,
                MaxVisibleClusters = _options.MaxCandidates,
                ResetCacheAllocationState = resetCacheAllocation ? 1u : 0u,
            },
            resetCacheAllocation
                ? "Cluster phase one raster/deform binning uniforms"
                : "Cluster phase two raster/deform binning uniforms");
        uint binClearGroups = Math.Max(1u, (frame.MaterialCount + 63u) / 64u);
        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   resetCacheAllocation
                       ? "Cluster phase one raster/deform bins reset"
                       : "Cluster phase two raster/deform bins reset",
                   _shaders!.RasterDeformBinReset,
                   out ClusterDispatchPassData passData))
        {
            passData.Dispatch = new ClusterDispatch(binClearGroups, 1, 1);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, binningUniforms));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, drawArgs));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.BinningDispatchArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.RasterReserveCounters,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.DeformReserveCounters,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.CacheAllocationCounter,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterIndirectPassData>(
                   ref graph,
                   resetCacheAllocation
                       ? "Cluster phase one raster/deform bins count"
                       : "Cluster phase two raster/deform bins count",
                   _shaders.RasterDeformBinCount,
                   out ClusterIndirectPassData passData))
        {
            passData.IndirectArguments = frame.BinningDispatchArgs;
            passData.IndirectOffset = 0;
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, binningUniforms));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, drawArgs));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.SlotBuffer,
                    sizeof(uint)));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                passData.IndirectArguments,
                GraphResourceUsage.IndirectArgument,
                GraphAccess.Read,
                new BufferRange(0, ClusterIndirectAbi.DispatchBytes));
            builder.SetRenderFunc<ClusterIndirectPassData>(
                ClusterIndirectPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   resetCacheAllocation
                       ? "Cluster phase one raster/deform bins reserve"
                       : "Cluster phase two raster/deform bins reserve",
                   _shaders.RasterDeformBinReserve,
                   out ClusterDispatchPassData passData))
        {
            passData.Dispatch = new ClusterDispatch(
                Math.Max(1u, (frame.MaterialCount + 127u) / 128u),
                1,
                1);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, binningUniforms));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.BinnedDrawArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.BinnedHardwareDrawArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.SoftwareDispatchArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.RasterReserveCounters,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DeformDispatchArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.DeformReserveCounters,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterIndirectPassData>(
                   ref graph,
                   resetCacheAllocation
                       ? "Cluster phase one raster/deform bins scatter"
                       : "Cluster phase two raster/deform bins scatter",
                   _shaders.RasterDeformBinScatter,
                   out ClusterIndirectPassData passData))
        {
            passData.IndirectArguments = frame.BinningDispatchArgs;
            passData.IndirectOffset = 0;
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.RasterBinMeta, RasterBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, binningUniforms));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.BinnedClusters,
                    BinnedClusterStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateReadOnlyBufferView(graph, drawArgs));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, readOffsetArgs));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.SlotBuffer,
                    sizeof(uint)));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DeformBinMeta, DeformBinStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.DeformBinnedClusters,
                    BinnedClusterStride),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                passData.IndirectArguments,
                GraphResourceUsage.IndirectArgument,
                GraphAccess.Read,
                new BufferRange(0, ClusterIndirectAbi.DispatchBytes));
            builder.SetRenderFunc<ClusterIndirectPassData>(
                ClusterIndirectPassData.Execute);
        }
        RecordHardwareArgumentCopy(ref graph, frame, resetCacheAllocation);

        RecordDeform(ref graph, frame, resetCacheAllocation);
        RecordSoftwareRaster(ref graph, frame, in view, resetCacheAllocation);
        RecordDepthMerge(ref graph, frame, resetCacheAllocation);
        RecordHardwareRaster(ref graph, frame, in view, resetCacheAllocation);
    }

    private static void RecordHardwareArgumentCopy(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        bool phaseOne)
    {
        ulong bytes = checked((ulong)frame.MaterialCount * ClusterIndirectAbi.DrawBytes);
        using IUnsafeRenderGraphBuilder builder =
            graph.AddUnsafePass<ClusterBufferCopyPassData>(
                phaseOne
                    ? "Copy Cluster phase one hardware arguments"
                    : "Copy Cluster phase two hardware arguments",
                out ClusterBufferCopyPassData passData);
        passData.Source = frame.BinnedHardwareDrawArgs;
        passData.Destination = frame.HardwareIndirectArgs;
        passData.ByteCount = bytes;
        builder.UseBuffer(passData.Source, GraphResourceUsage.CopySource);
        builder.UseBuffer(
            passData.Destination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.WriteAll);
        builder.SetRenderFunc<ClusterBufferCopyPassData>(
            static (data, context) =>
                context.CopyBufferRegion(
                    data.Source,
                    0,
                    data.Destination,
                    0,
                    data.ByteCount));
    }

    private void RecordDeform(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        bool phaseOne)
    {
        foreach (MaterialResources material in frame.Materials)
        {
            uint bin = material.State.Bin;
            BufferHandle pushConstants = UploadUniform(
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
            using IComputeRenderGraphBuilder builder =
                AddComputePass<ClusterIndirectPassData>(
                    ref graph,
                    phaseOne
                        ? "Cluster phase one deform bin"
                        : "Cluster phase two deform bin",
                    _shaders!.DeformCachePopulate,
                    out ClusterIndirectPassData passData);
            passData.IndirectArguments = frame.DeformDispatchArgs;
            passData.IndirectOffset = indirectOffset;
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.DeformCache),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.CacheOffsets, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.CacheAllocationCounter,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.DeformBinnedClusters,
                    BinnedClusterStride));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.DeformBinMeta,
                    DeformBinStride));
            builder.UseBuffer(CreateConstantBufferView(graph, pushConstants));
            builder.UseBuffer(
                passData.IndirectArguments,
                GraphResourceUsage.IndirectArgument,
                GraphAccess.Read,
                new BufferRange(indirectOffset, ClusterIndirectAbi.DispatchBytes));
            builder.SetRenderFunc<ClusterIndirectPassData>(
                ClusterIndirectPassData.Execute);
        }
    }

    private void RecordSoftwareRaster(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        in RenderView view,
        bool phaseOne)
    {
        foreach (MaterialResources material in frame.Materials)
        {
            uint bin = material.State.Bin;
            BufferHandle pushConstants = UploadUniform(
                ref graph,
                new ClusterSoftwareRasterUniforms
                {
                    ViewProj = view.View * view.Projection,
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                    MaxBins = frame.MaterialCount,
                    CurrentBin = bin,
                },
                phaseOne
                    ? "Cluster phase one software raster bin uniforms"
                    : "Cluster phase two software raster bin uniforms");
            ulong indirectOffset = checked(
                (ulong)(frame.MaterialCount + bin) * ClusterIndirectAbi.DispatchBytes);
            using IComputeRenderGraphBuilder builder =
                AddComputePass<ClusterIndirectPassData>(
                    ref graph,
                    phaseOne
                        ? "Cluster phase one software raster bin"
                        : "Cluster phase two software raster bin",
                    _shaders!.SoftwareRaster,
                    out ClusterIndirectPassData passData);
            passData.IndirectArguments = frame.SoftwareDispatchArgs;
            passData.IndirectOffset = indirectOffset;
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.VisBuffer,
                    Format.R32UInt,
                    null),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.SoftwareDepth,
                    Format.R32UInt,
                    null),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.SoftwareDebug),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.BinnedClusters,
                    BinnedClusterStride));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.RasterBinMeta,
                    RasterBinStride));
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.Depth,
                    Format.D32Float,
                    new TextureSubresourceRange(
                        0,
                        1,
                        0,
                        1,
                        TextureAspects.Depth)));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.DeformCache));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.CacheOffsets,
                    sizeof(uint)));
            builder.UseBuffer(CreateConstantBufferView(graph, pushConstants));
            builder.UseBuffer(
                passData.IndirectArguments,
                GraphResourceUsage.IndirectArgument,
                GraphAccess.Read,
                new BufferRange(indirectOffset, ClusterIndirectAbi.DispatchBytes));
            builder.SetRenderFunc<ClusterIndirectPassData>(
                ClusterIndirectPassData.Execute);
        }
    }

    private void RecordDepthMerge(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        bool phaseOne)
    {
        TextureViewHandle depthView = graph.CreateTextureView(
            frame.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            GraphTextureViewUsage.DepthStencilAttachment,
            name: phaseOne
                ? "Cluster phase one depth merge view"
                : "Cluster phase two depth merge view");
        ClusterRasterShader shader = _shaders!.DepthMerge;
        using IRasterRenderGraphBuilder builder =
            graph.AddRasterRenderPass<ClusterFullscreenPassData>(
                phaseOne
                    ? "Cluster phase one software depth merge"
                    : "Cluster phase two software depth merge",
                out ClusterFullscreenPassData passData);
        passData.Width = frame.Width;
        passData.Height = frame.Height;
        builder.SetPipeline(shader.Pipeline);
        builder.SetParameterBlock(shader.Program.ParameterLayout);
        builder.SetRenderAttachmentDepth(
            depthView,
            GraphAccess.ReadWrite,
            LoadType.Load);
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                frame.SoftwareDepth,
                Format.R32UInt,
                null));
        builder.SetRenderFunc<ClusterFullscreenPassData>(
            ClusterFullscreenPassData.Execute);
    }

    private void RecordHardwareRaster(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        in RenderView view,
        bool phaseOne)
    {
        TextureViewHandle visView = graph.CreateTextureView(
            frame.VisBuffer,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: phaseOne
                ? "Cluster phase one hardware visibility view"
                : "Cluster phase two hardware visibility view");
        TextureViewHandle depthView = graph.CreateTextureView(
            frame.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            GraphTextureViewUsage.DepthStencilAttachment,
            name: phaseOne
                ? "Cluster phase one hardware depth view"
                : "Cluster phase two hardware depth view");
        BufferHandle drawUniforms = UploadUniform(
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

        foreach (MaterialResources material in frame.Materials)
        {
            uint bin = material.State.Bin;
            BufferHandle dispatchUniforms = UploadUniform(
                ref graph,
                new ClusterDrawDispatchUniforms
                {
                    DrawArgsByteOffset = checked(bin * ClusterIndirectAbi.DrawStride),
                },
                phaseOne
                    ? "Cluster phase one hardware bin dispatch uniforms"
                    : "Cluster phase two hardware bin dispatch uniforms");
            ulong offset = checked((ulong)bin * ClusterIndirectAbi.DrawBytes);
            ClusterRasterShader shader = _shaders!.HardwareRaster;
            using IRasterRenderGraphBuilder builder =
                graph.AddRasterRenderPass<ClusterHardwareRasterPassData>(
                    phaseOne
                        ? "Cluster phase one hardware visibility bin"
                        : "Cluster phase two hardware visibility bin",
                    out ClusterHardwareRasterPassData passData);
            passData.Layout = RequireDrawIndirectLayout();
            passData.IndirectArguments = frame.HardwareIndirectArgs;
            passData.IndirectOffset = offset;
            passData.Width = frame.Width;
            passData.Height = frame.Height;
            builder.SetPipeline(shader.Pipeline);
            builder.SetParameterBlock(shader.Program.ParameterLayout);
            builder.SetRenderAttachment(
                visView,
                0,
                GraphAccess.Write,
                LoadType.Load);
            builder.SetRenderAttachmentDepth(
                depthView,
                GraphAccess.ReadWrite,
                LoadType.Load);
            builder.UseBuffer(
                passData.IndirectArguments,
                GraphResourceUsage.IndirectArgument,
                GraphAccess.Read,
                new BufferRange(offset, ClusterIndirectAbi.DrawBytes));
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(CreateConstantBufferView(graph, drawUniforms));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseBuffer(CreateConstantBufferView(graph, dispatchUniforms));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.BinnedClusters,
                    BinnedClusterStride));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.BinnedHardwareDrawArgs));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.DeformCache));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.CacheOffsets,
                    sizeof(uint)));
            builder.SetRenderFunc<ClusterHardwareRasterPassData>(
                ClusterHardwareRasterPassData.Execute);
        }
    }

    private void RecordHiZ(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        bool phaseOne)
    {
        int mipCount = _history!.HiZMipCount;
        if (mipCount < 2)
            throw new InvalidOperationException("Cluster HiZ requires at least two mip levels.");
        TextureSubresourceRange depthRange = new(0, 1, 0, 1, TextureAspects.Depth);
        TextureSubresourceRange mip0 = Mip(0);
        TextureSubresourceRange mip1 = Mip(1);
        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   phaseOne
                       ? "Build Cluster phase one HiZ mips 0-1"
                       : "Build Cluster final HiZ mips 0-1",
                   _shaders!.HiZFirst,
                   out ClusterDispatchPassData passData))
        {
            passData.Dispatch = new ClusterDispatch(
                Groups(MipExtent(frame.Width, 1), 8),
                Groups(MipExtent(frame.Height, 1), 8),
                1);
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.Depth,
                    Format.D32Float,
                    depthRange));
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    mip0),
                GraphAccess.WriteAll);
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    mip1),
                GraphAccess.WriteAll);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        int sourceMip = 1;
        while (sourceMip + 2 < mipCount)
        {
            int middleMip = sourceMip + 1;
            int destinationMip = sourceMip + 2;
            using IComputeRenderGraphBuilder builder =
                AddComputePass<ClusterDispatchPassData>(
                    ref graph,
                    phaseOne
                        ? "Build Cluster phase one HiZ mip pair"
                        : "Build Cluster final HiZ mip pair",
                    _shaders.HiZDownsampleTwo,
                    out ClusterDispatchPassData passData);
            passData.Dispatch = new ClusterDispatch(
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8),
                1);
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(sourceMip)),
                GraphAccess.Read);
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(middleMip)),
                GraphAccess.WriteAll);
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(destinationMip)),
                GraphAccess.WriteAll);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
            sourceMip = destinationMip;
        }
        if (sourceMip + 1 < mipCount)
        {
            int destinationMip = sourceMip + 1;
            using IComputeRenderGraphBuilder builder =
                AddComputePass<ClusterDispatchPassData>(
                    ref graph,
                    phaseOne
                        ? "Build Cluster phase one HiZ final mip"
                        : "Build Cluster final HiZ final mip",
                    _shaders.HiZDownsample,
                    out ClusterDispatchPassData passData);
            passData.Dispatch = new ClusterDispatch(
                Groups(MipExtent(frame.Width, destinationMip), 8),
                Groups(MipExtent(frame.Height, destinationMip), 8),
                1);
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(sourceMip)),
                GraphAccess.Read);
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.CurrentHiZ,
                    Format.R32Float,
                    Mip(destinationMip)),
                GraphAccess.WriteAll);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }
    }

    private void RecordShade(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        in RenderView view,
        bool hasHistory)
    {
        BufferHandle binUniforms = UploadUniform(
            ref graph,
            new ClusterShadeBinUniforms
            {
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                MaterialCount = frame.MaterialCount,
                SlotCapacity = frame.SlotCapacity,
                BinFieldIndex = ClusterMaterialTable.ShadeBinField,
            },
            "Cluster shade bin uniforms");
        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Clear Cluster shade bins",
                   _shaders!.ShadeBinClearPrepare,
                   out ClusterDispatchPassData passData,
                   asyncCompute: true))
        {
            passData.Dispatch = new ClusterDispatch(
                Math.Max(1u, (frame.MaterialCount + 127u) / 128u),
                1,
                1);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, binUniforms));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.ShadeScatterCounts,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.ShadeReserveCounters,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Count Cluster shade bins",
                   _shaders.ShadeBinCount,
                   out ClusterDispatchPassData passData,
                   asyncCompute: true))
        {
            passData.Dispatch =
                new ClusterDispatch(Groups(frame.Width, 8), Groups(frame.Height, 8), 1);
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, binUniforms));
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.VisBuffer,
                    Format.R32UInt,
                    null));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.SlotBuffer,
                    sizeof(uint)));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Reserve Cluster shade bins",
                   _shaders.ShadeBinReserve,
                   out ClusterDispatchPassData passData,
                   asyncCompute: true))
        {
            passData.Dispatch = new ClusterDispatch(
                Math.Max(1u, (frame.MaterialCount + 127u) / 128u),
                1,
                1);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.ShadeBinCounts, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, binUniforms));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.ShadeBinOffsets, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.ShadeIndirectArgs),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.ShadeScatterCounts,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.ShadeReserveCounters,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Scatter Cluster shade bins",
                   _shaders.ShadeBinScatter,
                   out ClusterDispatchPassData passData,
                   asyncCompute: true))
        {
            passData.Dispatch =
                new ClusterDispatch(Groups(frame.Width, 8), Groups(frame.Height, 8), 1);
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseBuffer(CreateConstantBufferView(graph, binUniforms));
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.VisBuffer,
                    Format.R32UInt,
                    null));
            builder.UseBuffer(
                CreateStorageBufferView(graph, frame.ShadeBinOffsets, sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.SlotBuffer,
                    sizeof(uint)));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.ShadeScatterCounts,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.UseBuffer(
                CreateStorageBufferView(
                    graph,
                    frame.PixelCoordinates,
                    sizeof(uint)),
                GraphAccess.ReadWrite);
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        Matrix4x4 viewProjection = view.View * view.Projection;
        BufferHandle resolveUniforms = UploadUniform(
            ref graph,
            new ClusterResolveUniforms
            {
                ViewProj = viewProjection,
                View = view.View,
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
            },
            "Cluster resolve uniforms");
        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Resolve Cluster visibility background",
                   _shaders.Resolve,
                   out ClusterDispatchPassData passData,
                   asyncCompute: true))
        {
            passData.Dispatch =
                new ClusterDispatch(Groups(frame.Width, 8), Groups(frame.Height, 8), 1);
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.SceneColor,
                    Format.R16G16B16A16Float,
                    null),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, resolveUniforms));
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.VisBuffer,
                    Format.R32UInt,
                    null));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }

        if (!Matrix4x4.Invert(view.View, out Matrix4x4 worldFromView))
            throw new InvalidOperationException("The Cluster shade view is not invertible.");
        Vector3 cameraPosition = new(worldFromView.M41, worldFromView.M42, worldFromView.M43);
        Matrix4x4 previousViewProjection = hasHistory
            ? _history!.PreviousView * _history.PreviousProjection
            : viewProjection;
        foreach (MaterialResources material in frame.Materials)
        {
            uint bin = material.State.Bin;
            BufferHandle uniforms = UploadUniform(
                ref graph,
                new ClusterShadeUniforms
                {
                    ViewProj = viewProjection,
                    View = view.View,
                    PrevViewProj = previousViewProjection,
                    MotionViewProj = viewProjection,
                    PrevMotionViewProj = previousViewProjection,
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                    ShadingBin = bin,
                    MaterialCount = frame.MaterialCount,
                    LightLayerMask = LightDefaults.LayerMask,
                    CameraPos = cameraPosition,
                    HasPreviousFrame = hasHistory ? 1u : 0u,
                    WriteMotionVectors = 1,
                },
                "Cluster material shade bin uniforms");
            ulong indirectOffset = checked((ulong)bin * ClusterIndirectAbi.DispatchBytes);
            if (material.State.ParameterKind == ClusterMaterialParameterKind.StandardPbr)
            {
                using IComputeRenderGraphBuilder builder =
                    AddComputePass<ClusterIndirectPassData>(
                        ref graph,
                        "Cluster standard PBR material shade bin",
                        material.State.Shade,
                        out ClusterIndirectPassData passData,
                        asyncCompute: true);
                passData.IndirectArguments = frame.ShadeIndirectArgs;
                passData.IndirectOffset = indirectOffset;
                builder.UseBuffer(
                    CreateConstantBufferView(
                        graph,
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange));
                builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
                builder.UseTexture(
                    CreateStorageTextureView(
                        graph,
                        frame.SceneColor,
                        Format.R16G16B16A16Float,
                        null),
                    GraphAccess.ReadWrite);
                builder.UseSampler(graph.Import(_materials!.MaterialSampler));
                builder.UseBuffer(CreateConstantBufferView(graph, uniforms));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(graph, material.Scalars));
                builder.UseTexture(
                    CreateStorageTextureView(
                        graph,
                        frame.MotionVectors,
                        Format.R16G16Float,
                        null),
                    GraphAccess.ReadWrite);
                builder.UseSampler(graph.Import(material.State.Sampler));
                builder.UseBuffer(
                    CreateConstantBufferView(graph, frame.LightCounts));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.LightBuffer,
                        LightStride));
                builder.UseBuffer(
                    CreateConstantBufferView(graph, frame.LightGridUniforms));
                builder.UseTexture(
                    CreateSampledTextureView(
                        graph,
                        frame.CookieAtlas,
                        Format.R8G8B8A8UNorm,
                        null,
                        TextureViewDimension.Texture2DArray));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(graph, frame.LightGrid, 8));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.LightIndices,
                        sizeof(uint)));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.PixelCoordinates,
                        sizeof(uint)));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.ShadeBinOffsets,
                        sizeof(uint)));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.ShadeBinCounts,
                        sizeof(uint)));
                builder.UseTexture(
                    CreateSampledTextureView(
                        graph,
                        frame.VisBuffer,
                        Format.R32UInt,
                        null));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.VisibleClusters,
                        VisibleClusterStride));
                builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
                builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.DeformCache));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.CacheOffsets,
                        sizeof(uint)));
                builder.UseTexture(
                    CreateSampledTextureView(
                        graph,
                        material.Albedo,
                        null,
                        null));
                builder.UseTexture(
                    CreateSampledTextureView(
                        graph,
                        material.Normal,
                        null,
                        null));
                builder.UseTexture(
                    CreateSampledTextureView(
                        graph,
                        material.Arm,
                        null,
                        null));
                builder.UseBuffer(
                    passData.IndirectArguments,
                    GraphResourceUsage.IndirectArgument,
                    GraphAccess.Read,
                    new BufferRange(indirectOffset, ClusterIndirectAbi.DispatchBytes));
                builder.SetRenderFunc<ClusterIndirectPassData>(
                    ClusterIndirectPassData.Execute);
            }
            else
            {
                using IComputeRenderGraphBuilder builder =
                    AddComputePass<ClusterIndirectPassData>(
                        ref graph,
                        "Cluster unlit material shade bin",
                        material.State.Shade,
                        out ClusterIndirectPassData passData,
                        asyncCompute: true);
                passData.IndirectArguments = frame.ShadeIndirectArgs;
                passData.IndirectOffset = indirectOffset;
                builder.UseBuffer(
                    CreateConstantBufferView(
                        graph,
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange));
                builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
                builder.UseTexture(
                    CreateStorageTextureView(
                        graph,
                        frame.SceneColor,
                        Format.R16G16B16A16Float,
                        null),
                    GraphAccess.ReadWrite);
                builder.UseBuffer(CreateConstantBufferView(graph, uniforms));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(graph, material.Scalars));
                builder.UseTexture(
                    CreateStorageTextureView(
                        graph,
                        frame.MotionVectors,
                        Format.R16G16Float,
                        null),
                    GraphAccess.ReadWrite);
                builder.UseSampler(graph.Import(material.State.Sampler));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.PixelCoordinates,
                        sizeof(uint)));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.ShadeBinOffsets,
                        sizeof(uint)));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.ShadeBinCounts,
                        sizeof(uint)));
                builder.UseTexture(
                    CreateSampledTextureView(
                        graph,
                        frame.VisBuffer,
                        Format.R32UInt,
                        null));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.VisibleClusters,
                        VisibleClusterStride));
                builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
                builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.DeformCache));
                builder.UseBuffer(
                    CreateReadOnlyBufferView(
                        graph,
                        frame.CacheOffsets,
                        sizeof(uint)));
                builder.UseTexture(
                    CreateSampledTextureView(
                        graph,
                        material.Albedo,
                        null,
                        null));
                builder.UseBuffer(
                    passData.IndirectArguments,
                    GraphResourceUsage.IndirectArgument,
                    GraphAccess.Read,
                    new BufferRange(indirectOffset, ClusterIndirectAbi.DispatchBytes));
                builder.SetRenderFunc<ClusterIndirectPassData>(
                    ClusterIndirectPassData.Execute);
            }
        }

        BufferHandle motionUniforms = UploadUniform(
            ref graph,
            new ClusterMotionUniforms
            {
                ViewProj = viewProjection,
                PrevViewProj = previousViewProjection,
                ScreenWidth = checked((uint)frame.Width),
                ScreenHeight = checked((uint)frame.Height),
                HasPreviousFrame = hasHistory ? 1u : 0u,
            },
            "Cluster explicit motion vector uniforms");
        using (IComputeRenderGraphBuilder builder =
               AddComputePass<ClusterDispatchPassData>(
                   ref graph,
                   "Cluster explicit motion vectors",
                   _shaders.MotionVectors,
                   out ClusterDispatchPassData passData,
                   asyncCompute: true))
        {
            passData.Dispatch =
                new ClusterDispatch(Groups(frame.Width, 8), Groups(frame.Height, 8), 1);
            builder.UseBuffer(
                CreateConstantBufferView(
                    graph,
                    frame.InstanceProperties,
                    frame.InstancePropertiesRange));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.InstanceData));
            builder.UseTexture(
                CreateStorageTextureView(
                    graph,
                    frame.MotionVectors,
                    Format.R16G16Float,
                    null),
                GraphAccess.ReadWrite);
            builder.UseBuffer(CreateConstantBufferView(graph, motionUniforms));
            builder.UseTexture(
                CreateSampledTextureView(
                    graph,
                    frame.VisBuffer,
                    Format.R32UInt,
                    null));
            builder.UseBuffer(
                CreateReadOnlyBufferView(
                    graph,
                    frame.VisibleClusters,
                    VisibleClusterStride));
            builder.UseBuffer(CreateReadOnlyBufferView(graph, frame.PageHeap));
            builder.SetRenderFunc<ClusterDispatchPassData>(
                ClusterDispatchPassData.Execute);
        }
    }

    private TextureHandle RecordTemporal(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        bool hasHistory)
    {
        if (!_options.EnableTemporalResolve || !hasHistory)
            return frame.SceneColor;
        TemporalResolveUniforms settings = TemporalResolveSettings.Default.ToUniforms();
        BufferHandle uniforms = UploadUniform(
            ref graph,
            new ClusterTemporalUniforms
            {
                HistoryWeight = settings.HistoryWeight,
                NeighborhoodClampScale = settings.NeighborhoodClampScale,
                NeighborhoodClampMin = settings.NeighborhoodClampMin,
                MotionRejectionScale = settings.MotionRejectionScale,
            },
            "Cluster temporal resolve uniforms");
        TextureViewHandle output = graph.CreateTextureView(
            frame.TemporalColor,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster temporal output view");
        TextureSubresourceRange depth = new(0, 1, 0, 1, TextureAspects.Depth);
        ClusterRasterShader shader = _shaders!.TemporalResolve;
        using IRasterRenderGraphBuilder builder =
            graph.AddRasterRenderPass<ClusterFullscreenPassData>(
                "Cluster temporal resolve",
                out ClusterFullscreenPassData passData);
        passData.Width = frame.Width;
        passData.Height = frame.Height;
        builder.SetPipeline(shader.Pipeline);
        builder.SetParameterBlock(shader.Program.ParameterLayout);
        builder.SetRenderAttachment(
            output,
            0,
            GraphAccess.WriteAll,
            LoadType.Clear,
            Vector4.Zero);
        builder.UseBuffer(CreateConstantBufferView(graph, uniforms));
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                frame.SceneColor,
                Format.R16G16B16A16Float,
                null));
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                frame.PreviousScene,
                Format.R16G16B16A16Float,
                null));
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                frame.MotionVectors,
                Format.R16G16Float,
                null));
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                frame.PreviousMotion,
                Format.R16G16Float,
                null));
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                frame.Depth,
                Format.D32Float,
                depth));
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                frame.PreviousDepth,
                Format.D32Float,
                depth));
        builder.SetRenderFunc<ClusterFullscreenPassData>(
            ClusterFullscreenPassData.Execute);
        return frame.TemporalColor;
    }

    private static void RecordHistoryCopies(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        TextureHandle postScene)
    {
        TextureSubresourceRange? color = null;
        TextureSubresourceRange depth = new(0, 1, 0, 1, TextureAspects.Depth);
        using IUnsafeRenderGraphBuilder builder =
            graph.AddUnsafePass<ClusterHistoryCopyPassData>(
                "Update Cluster temporal histories",
                out ClusterHistoryCopyPassData passData);
        passData.SceneSource = postScene;
        passData.SceneDestination = frame.CurrentSceneHistory;
        passData.MotionSource = frame.MotionVectors;
        passData.MotionDestination = frame.CurrentMotionHistory;
        passData.DepthSource = frame.Depth;
        passData.DepthDestination = frame.CurrentDepthHistory;
        passData.Width = frame.Width;
        passData.Height = frame.Height;
        builder.UseTexture(
            passData.SceneSource,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            color);
        builder.UseTexture(
            passData.SceneDestination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.WriteAll,
            color);
        builder.UseTexture(
            passData.MotionSource,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            color);
        builder.UseTexture(
            passData.MotionDestination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.WriteAll,
            color);
        builder.UseTexture(
            passData.DepthSource,
            GraphResourceUsage.CopySource,
            GraphAccess.Read,
            depth);
        builder.UseTexture(
            passData.DepthDestination,
            GraphResourceUsage.CopyDestination,
            GraphAccess.WriteAll,
            depth);
        builder.SetRenderFunc<ClusterHistoryCopyPassData>(
            ClusterHistoryCopyPassData.Execute);
    }

    private void RecordTonemap(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        in FrameResources frame,
        TextureHandle postScene)
    {
        TextureViewHandle output = graph.CreateTextureView(
            frame.Target,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Cluster presentation view");
        ClusterRasterShader shader = _shaders!.Tonemap;
        using IRasterRenderGraphBuilder builder =
            graph.AddRasterRenderPass<ClusterFullscreenPassData>(
                "Cluster tone map and present",
                out ClusterFullscreenPassData passData);
        passData.Width = frame.Width;
        passData.Height = frame.Height;
        builder.SetPipeline(shader.Pipeline);
        builder.SetParameterBlock(shader.Program.ParameterLayout);
        builder.SetRenderAttachment(
            output,
            0,
            GraphAccess.WriteAll,
            LoadType.Clear,
            new Vector4(0, 0, 0, 1));
        builder.UseTexture(
            CreateSampledTextureView(
                graph,
                postScene,
                Format.R16G16B16A16Float,
                null));
        builder.SetRenderFunc<ClusterFullscreenPassData>(
            ClusterFullscreenPassData.Execute);
    }

    private IComputeRenderGraphBuilder AddComputePass<TPassData>(
        ref global::SomeEngine.RenderGraph.RenderGraph graph,
        string name,
        ClusterComputeShader shader,
        out TPassData passData,
        bool asyncCompute = false)
        where TPassData : class, new()
    {
        IComputeRenderGraphBuilder builder =
            graph.AddComputePass(name, out passData);
        builder.SetPipeline(shader.Pipeline);
        builder.SetParameterBlock(shader.Program.ParameterLayout);
        if (passData is ClusterIndirectPassData indirect)
            indirect.Layout = RequireDispatchIndirectLayout();
        builder.EnableAsyncCompute(
            _options.EnableAsyncCompute && asyncCompute);
        return builder;
    }

    private static TextureViewHandle CreateSampledTextureView(
        global::SomeEngine.RenderGraph.RenderGraph graph,
        TextureHandle texture,
        Format? format,
        TextureSubresourceRange? range,
        TextureViewDimension? dimension = null) =>
        graph.CreateSharedTextureView(
            texture,
            range,
            GraphTextureViewUsage.ShaderResource,
            format,
            dimension: dimension);

    private static TextureViewHandle CreateStorageTextureView(
        global::SomeEngine.RenderGraph.RenderGraph graph,
        TextureHandle texture,
        Format format,
        TextureSubresourceRange? range,
        TextureViewDimension? dimension = null) =>
        graph.CreateSharedTextureView(
            texture,
            range,
            GraphTextureViewUsage.Storage,
            format,
            dimension: dimension);

    private static BufferViewHandle CreateConstantBufferView(
        global::SomeEngine.RenderGraph.RenderGraph graph,
        BufferHandle buffer,
        BufferRange? range = null) =>
        graph.CreateSharedBufferView(
            buffer,
            range,
            GraphBindingType.ConstantBuffer);

    private static BufferViewHandle CreateReadOnlyBufferView(
        global::SomeEngine.RenderGraph.RenderGraph graph,
        BufferHandle buffer,
        uint stride = 0,
        BufferRange? range = null) =>
        graph.CreateSharedBufferView(
            buffer,
            range,
            GraphBindingType.ReadOnlyBuffer,
            stride: stride);

    private static BufferViewHandle CreateStorageBufferView(
        global::SomeEngine.RenderGraph.RenderGraph graph,
        BufferHandle buffer,
        uint stride = 0,
        BufferRange? range = null) =>
        graph.CreateSharedBufferView(
            buffer,
            range,
            GraphBindingType.StorageBuffer,
            stride: stride);

    private static TextureSubresourceRange Mip(int mip)
        => new(checked((uint)mip), 1, 0, 1, TextureAspects.Color);

    private static int MipExtent(int extent, int mip)
        => Math.Max(1, extent >> mip);

    private static uint Groups(int extent, int groupSize)
        => checked((uint)((extent + groupSize - 1) / groupSize));

}
