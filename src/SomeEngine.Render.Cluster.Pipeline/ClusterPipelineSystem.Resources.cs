using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly FrameResourceScratch _frameResourceScratch = new();

    internal const int CandidateCountReadbackOffset = 0;
    internal const int CandidateArgsReadbackOffset = 4;
    internal const int DrawArgsReadbackOffset = 16;
    internal const int Phase2CandidateCountReadbackOffset = 32;
    internal const int Phase2CandidateArgsReadbackOffset = 36;
    internal const int Phase2DrawArgsReadbackOffset = 48;
    internal const int RasterReserveReadbackOffset = 64;
    internal const int ShadeReserveReadbackOffset = 72;
    internal const int DeformReserveReadbackOffset = 76;
    internal const int CacheAllocationReadbackOffset = 80;
    internal const int CachedDeformClustersReadbackOffset = 84;
    internal const int SoftwareDebugReadbackOffset = 88;
    internal const int ShadeBinCountReadbackOffset = 92;
    internal const int VisibilityProbeReadbackOffset = 512;
    internal const int VisibilityProbePixelCount = 64;
    internal const int VisibilityProbeRowPitch = VisibilityProbePixelCount * sizeof(uint);
    internal const int FrameMetricsReadbackByteSize =
        VisibilityProbeReadbackOffset + VisibilityProbeRowPitch;
    private const uint MaxBinnedEntriesPerCluster = 9;
    private const uint ClusterVertexCapacity = 64;
    private const int RasterBinStride = 32;
    private const int DeformBinStride = 16;
    private const int VisibleClusterStride = 16;
    private const int CandidateStride = 12;
    private const int BinnedClusterStride = 8;
    private FrameResources CreateFrameResources(
        ref RenderGraphEdit graph,
        in ClusterPipelineOutputDescription output,
        ClusterMaterialAssignmentSet assignments,
        ClusterDeformStageSet deformStages,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages)
    {
        uint rasterBinCount = rasterStages.BinCount;
        uint deformBinCount = deformStages.BinCount;
        uint shadeBinCount = shadeStages.BinCount;
        ulong candidates = _options.MaxCandidates;
        ulong binnedCapacity = checked(candidates * MaxBinnedEntriesPerCluster);
        int width = output.Width;
        int height = output.Height;
        int pageFaultCapacity = _resources.PageFaultCapacity;
        ClusterResidencyBinding residency = _resources.Residency.GetBinding();
        RenderInstanceResources instanceResources = _resources.InstanceResources;
        ulong instanceDataBytes = checked((ulong)instanceResources.PropertyDataBytes);
        ulong instanceMetadataStride = checked((ulong)instanceResources.BatchMetadataStrideBytes);
        ulong instanceMetadataBytes = checked(
            instanceMetadataStride * (ulong)instanceResources.Options.BatchCapacity);
        ClusterCullingHistory history = _history ?? throw new InvalidOperationException(
            "The Cluster culling history was not created.");
        history.EnsureDimensions(width, height);
        TextureInfo historyInfo = history.PreviousHiZ.Info;
        TextureDesc previousHistoryDescription = new(
            historyInfo.Dimension,
            historyInfo.Width,
            historyInfo.Height,
            historyInfo.Depth,
            historyInfo.MipLevelCount,
            historyInfo.ArrayLayerCount,
            historyInfo.SampleCount,
            historyInfo.Format,
            historyInfo.Usages,
            historyInfo.PermittedViewFormats.ToArray(),
            "Cluster previous HiZ",
            new ResourceNodePlacement(
                historyInfo.CreationNodeMask,
                historyInfo.VisibleNodeMask));
        TextureDesc currentHistoryDescription = new(
            historyInfo.Dimension,
            historyInfo.Width,
            historyInfo.Height,
            historyInfo.Depth,
            historyInfo.MipLevelCount,
            historyInfo.ArrayLayerCount,
            historyInfo.SampleCount,
            historyInfo.Format,
            historyInfo.Usages,
            historyInfo.PermittedViewFormats.ToArray(),
            "Cluster current HiZ",
            new ResourceNodePlacement(
                historyInfo.CreationNodeMask,
                historyInfo.VisibleNodeMask));

        var frame = new FrameResources(_frameResourceScratch)
        {
            Width = width,
            Height = height,
            RasterBinCount = rasterBinCount,
            DeformBinCount = deformBinCount,
            ShadeBinCount = shadeBinCount,
            SlotCapacity = assignments.SlotCapacity,
            PageFaultCapacity = pageFaultCapacity,
            PageHeap = DeclareExternalBuffer(ref graph, residency.PageHeap.Info.Size, BufferUsages.ShaderRead,
                "Cluster page heap"),
            Bvh = DeclareExternalBuffer(ref graph, residency.Bvh.Info.Size, BufferUsages.ShaderRead,
                "Cluster BVH"),
            InstanceData = DeclareExternalBuffer(ref graph, instanceDataBytes, BufferUsages.ShaderRead,
                "Cluster instance data"),
            InstanceProperties = DeclareExternalBuffer(ref graph, instanceMetadataBytes, BufferUsages.Constant,
                "Cluster instance metadata"),
            InstancePropertiesRange = new BufferRange(0, instanceMetadataStride),
            PageFaultReadback = DeclareExternalBuffer(
                ref graph,
                checked(sizeof(uint) + (ulong)pageFaultCapacity * sizeof(uint)),
                BufferUsages.CopyDestination,
                "Cluster page-fault readback"),
            PreviousHiZ = graph.DeclareExternalTexture(previousHistoryDescription),
            CurrentHiZ = graph.DeclareExternalTexture(currentHistoryDescription),
            RasterSlots = UploadWords(
                ref graph,
                rasterStages.Slots.Span,
                BufferUsages.ShaderRead,
                "Cluster raster stage slots"),
            DeformSlots = UploadWords(
                ref graph,
                deformStages.Slots.Span,
                BufferUsages.ShaderRead,
                "Cluster deform stage slots"),
            ShadeSlots = UploadWords(
                ref graph,
                shadeStages.Slots.Span,
                BufferUsages.ShaderRead,
                "Cluster shade stage slots"),
            ReadOffsetZero = UploadBytes(
                ref graph,
                stackalloc byte[16],
                BufferUsages.ShaderRead,
                "Cluster zero read offset"),
        };
        if (_options.EnableFrameMetricsReadback)
        {
            frame.FrameMetricsReadback = DeclareExternalBuffer(
                ref graph,
                FrameMetricsReadbackByteSize,
                BufferUsages.CopyDestination,
                "Cluster frame-metrics readback");
        }

        frame.CandidateArgs = frame.AddBuffer(
            ref graph,
            16,
            BufferUsages.Indirect | BufferUsages.CopySource,
            "Cluster candidate dispatch");
        frame.CandidateArgsInitialization = UploadWords(
            ref graph,
            [0u, 1u, 1u, 0u],
            BufferUsages.CopySource,
            "Cluster candidate dispatch initialization");
        frame.CandidateClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * CandidateStride),
            BufferUsages.None,
            "Cluster candidates");
        frame.CandidateCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster candidate count");
        frame.DrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster phase-one counts");
        frame.PageFaults = frame.AddBuffer(
            ref graph,
            checked(sizeof(uint) + (ulong)pageFaultCapacity * sizeof(uint)),
            BufferUsages.CopySource,
            "Cluster page faults");
        frame.Phase2CandidateArgs = frame.AddBuffer(
            ref graph,
            16,
            BufferUsages.Indirect | BufferUsages.CopySource,
            "Cluster phase-two dispatch");
        frame.Phase2CandidateClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * CandidateStride),
            BufferUsages.None,
            "Cluster phase-two candidates");
        frame.Phase2CandidateCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster phase-two count");
        frame.Phase2DrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster phase-two counts");
        frame.VisibleClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * VisibleClusterStride),
            BufferUsages.None,
            "Cluster visible clusters");
        frame.RasterBinMeta = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * RasterBinStride),
            BufferUsages.None,
            "Cluster raster bins");
        frame.BinnedClusters = frame.AddBuffer(
            ref graph,
            checked(binnedCapacity * BinnedClusterStride),
            BufferUsages.None,
            "Cluster raster bin indices");
        frame.BinnedDrawArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * ClusterIndirectAbi.DrawBytes),
            BufferUsages.None,
            "Cluster raster draw metadata");
        frame.BinnedHardwareDrawArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * ClusterIndirectAbi.DrawBytes),
            BufferUsages.CopySource,
            "Cluster hardware draw metadata");
        frame.HardwareIndirectArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * ClusterIndirectAbi.DrawBytes),
            BufferUsages.Indirect | BufferUsages.CopyDestination,
            "Cluster hardware indirect arguments");
        frame.BinningDispatchArgs = frame.AddBuffer(
            ref graph,
            16,
            BufferUsages.Indirect,
            "Cluster binning dispatch");
        frame.SoftwareDispatchArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * 2 * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster software raster dispatches");
        frame.RasterReserveCounters = frame.AddBuffer(ref graph, 4 * sizeof(uint), BufferUsages.CopySource, "Cluster raster reserve counters");
        frame.DeformBinMeta = frame.AddBuffer(
            ref graph,
            checked((ulong)deformBinCount * DeformBinStride),
            BufferUsages.None,
            "Cluster deform bins");
        frame.DeformBinnedClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * BinnedClusterStride),
            BufferUsages.None,
            "Cluster deform bin indices");
        frame.DeformDispatchArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)deformBinCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster deform dispatches");
        frame.DeformReserveCounters = frame.AddBuffer(ref graph, 2 * sizeof(uint), BufferUsages.CopySource, "Cluster deform reserve counters");
        frame.CacheOffsets = frame.AddBuffer(
            ref graph,
            checked(candidates * sizeof(uint)),
            BufferUsages.None,
            "Cluster deform cache offsets");
        frame.CacheAllocationCounter = frame.AddBuffer(ref graph, 2 * sizeof(uint), BufferUsages.CopySource, "Cluster deform cache statistics");
        frame.DeformCache = frame.AddBuffer(
            ref graph,
            _options.DeformCacheBytes,
            BufferUsages.None,
            "Cluster deform cache");
        frame.SoftwareDebug = frame.AddBuffer(
            ref graph,
            4 + 8192ul * 24,
            BufferUsages.CopySource,
            "Cluster software raster debug output");

        frame.ShadeBinCounts = frame.AddBuffer(
            ref graph,
            checked((ulong)shadeBinCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade bin counts");
        frame.ShadeBinOffsets = frame.AddBuffer(
            ref graph,
            checked((ulong)shadeBinCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade bin offsets");
        frame.ShadeIndirectArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)shadeBinCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster shade dispatches");
        frame.ShadeScatterCounts = frame.AddBuffer(
            ref graph,
            checked((ulong)shadeBinCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade scatter counts");
        frame.ShadeReserveCounters = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.CopySource, "Cluster shade reserve counter");
        frame.PixelCoordinates = frame.AddBuffer(
            ref graph,
            checked((ulong)width * (ulong)height * sizeof(uint)),
            BufferUsages.None,
            "Cluster shade pixel coordinates");

        frame.VisBuffer = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment |
            TextureUsages.CopySource,
            label: "Cluster visibility buffer"));
        frame.Depth = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.D32Float,
            TextureUsages.Sampled | TextureUsages.DepthStencilAttachment | TextureUsages.CopySource,
            label: "Cluster scene depth"));
        frame.SoftwareDepth = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment,
            label: "Cluster software depth"));
        frame.SceneColor = graph.CreateTexture(new TextureDesc(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R16G16B16A16Float,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource,
            label: "Cluster scene color"));
        RecordBufferInitialization(ref graph, in frame);
        RecordTargetInitialization(ref graph, in frame);
        return frame;
    }

    private void BindFrameResources(
        ref RenderGraphFrame graph,
        in ClusterRenderBinding binding,
        ClusterCullingHistory history)
    {
        FrameResources frame = _frameResources;
        BindBuffer(ref graph, frame.PageHeap, binding.PageHeap,
            PipelineSync.AllShading, ResourceAccess.ShaderResource,
            ResourceContentState.Defined);
        BindBuffer(ref graph, frame.Bvh, binding.Bvh,
            PipelineSync.AllShading, ResourceAccess.ShaderResource,
            ResourceContentState.Defined);
        BindBuffer(ref graph, frame.InstanceData, binding.PropertyData,
            PipelineSync.AllShading, ResourceAccess.ShaderResource,
            ResourceContentState.Defined);
        BindBuffer(ref graph, frame.InstanceProperties, binding.InstancePropertyMetadata,
            PipelineSync.AllShading, ResourceAccess.ConstantBuffer,
            ResourceContentState.Defined);
        graph.SetBufferRange(
            frame.InstanceProperties,
            binding.InstancePropertyMetadataRange);

        int generation = RequireReadbackWriteGeneration();
        BindBuffer(
            ref graph,
            frame.PageFaultReadback,
            _pageFaultReadbacks[generation]
                ?? throw new InvalidOperationException("Cluster page-fault readback was not created."),
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            ResourceContentState.Undefined,
            _readbackFences[generation]);
        if (_options.EnableFrameMetricsReadback)
        {
            BindBuffer(
                ref graph,
                frame.FrameMetricsReadback,
                _frameMetricReadbacks[generation]
                    ?? throw new InvalidOperationException("Cluster frame-metrics readback was not created."),
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                ResourceContentState.Undefined,
                _readbackFences[generation]);
        }

        graph.BindExternalTexture(
            frame.PreviousHiZ,
            history.PreviousHiZ,
            history.PreviousHiZEndpoints);
        graph.BindExternalTexture(
            frame.CurrentHiZ,
            history.CurrentHiZ,
            history.CurrentHiZEndpoints);
    }

    private void WriteFrameUploads(
        ref RenderGraphFrame graph,
        int generation,
        ClusterDeformStageSet deformStages,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages,
        in RenderView view,
        in ClusterViewUniforms viewUniforms)
    {
        ClusterFrameUploads uploads = _uploads ?? throw new InvalidOperationException(
            "Cluster retained upload storage was not created.");
        FrameResources frame = _frameResources;
        uploads.BeginFrame();
        try
        {
            WriteWords(ref graph, uploads, generation, rasterStages.Slots.Span,
                "Cluster raster stage slots");
            WriteWords(ref graph, uploads, generation, deformStages.Slots.Span,
                "Cluster deform stage slots");
            WriteWords(ref graph, uploads, generation, shadeStages.Slots.Span,
                "Cluster shade stage slots");
            WriteBytes(ref graph, uploads, generation, stackalloc byte[16],
                "Cluster zero read offset");
            WriteWords(ref graph, uploads, generation, [0u, 1u, 1u, 0u],
                "Cluster candidate dispatch initialization");
            WriteUniform(ref graph, uploads, generation, viewUniforms,
                "Cluster view uniforms");

            WriteRasterPhaseUploads(ref graph, uploads, generation, frame, in view, phaseOne: true);
            WriteRasterPhaseUploads(ref graph, uploads, generation, frame, in view, phaseOne: false);

            WriteUniform(
                ref graph,
                uploads,
                generation,
                new ClusterShadeBinUniforms
                {
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                    ShadeBinCount = frame.ShadeBinCount,
                    SlotCapacity = frame.SlotCapacity,
                },
                "Cluster shade bin uniforms");
            WriteUniform(
                ref graph,
                uploads,
                generation,
                new ClusterResolveUniforms
                {
                    ViewProj = view.View * view.Projection,
                    View = view.View,
                    ScreenWidth = checked((uint)frame.Width),
                    ScreenHeight = checked((uint)frame.Height),
                },
                "Cluster resolve uniforms");
            uploads.CompleteFrame();
        }
        catch
        {
            uploads.CancelFrame();
            throw;
        }
    }

    private void WriteRasterPhaseUploads(
        ref RenderGraphFrame graph,
        ClusterFrameUploads uploads,
        int generation,
        in FrameResources frame,
        in RenderView view,
        bool phaseOne)
    {
        WriteUniform(
            ref graph,
            uploads,
            generation,
            new ClusterRasterDeformBinningUniforms
            {
                RasterMaxBins = frame.RasterBinCount,
                DeformMaxBins = frame.DeformBinCount,
                SlotCapacity = frame.SlotCapacity,
                MaxVisibleClusters = _options.MaxCandidates,
                ResetCacheAllocationState = phaseOne ? 1u : 0u,
            },
            phaseOne
                ? "Cluster phase one raster/deform binning uniforms"
                : "Cluster phase two raster/deform binning uniforms");

        for (uint bin = 0; bin < frame.DeformBinCount; bin++)
        {
            WriteUniform(
                ref graph,
                uploads,
                generation,
                new ClusterDeformUniforms
                {
                    MaxDeformCacheBytes = checked((uint)_options.DeformCacheBytes),
                    MaxClusterVertices = ClusterVertexCapacity,
                    CurrentBin = bin,
                },
                phaseOne
                    ? "Cluster phase one deform bin uniforms"
                    : "Cluster phase two deform bin uniforms");
        }

        for (uint bin = 0; bin < frame.RasterBinCount; bin++)
        {
            WriteUniform(
                ref graph,
                uploads,
                generation,
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
        }

        WriteUniform(
            ref graph,
            uploads,
            generation,
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
            WriteUniform(
                ref graph,
                uploads,
                generation,
                new ClusterDrawDispatchUniforms
                {
                    DrawArgsByteOffset = checked(bin * ClusterIndirectAbi.DrawStride),
                },
                phaseOne
                    ? "Cluster phase one hardware bin dispatch uniforms"
                    : "Cluster phase two hardware bin dispatch uniforms");
        }
    }

    private static void WriteUniform<T>(
        ref RenderGraphFrame graph,
        ClusterFrameUploads uploads,
        int generation,
        in T value,
        string label)
        where T : unmanaged
    {
        int valueSize = Unsafe.SizeOf<T>();
        int byteSize = checked((valueSize + 255) & ~255);
        Span<byte> bytes = stackalloc byte[byteSize];
        bytes.Clear();
        MemoryMarshal.Write(bytes, in value);
        _ = uploads.WriteAndBind(ref graph, generation, bytes, label);
    }

    private static void WriteWords(
        ref RenderGraphFrame graph,
        ClusterFrameUploads uploads,
        int generation,
        scoped ReadOnlySpan<uint> values,
        string label) =>
        WriteBytes(
            ref graph,
            uploads,
            generation,
            MemoryMarshal.AsBytes(values),
            label);

    private static void WriteBytes(
        ref RenderGraphFrame graph,
        ClusterFrameUploads uploads,
        int generation,
        scoped ReadOnlySpan<byte> bytes,
        string label) =>
        _ = uploads.WriteAndBind(ref graph, generation, bytes, label);

    private static void BindBuffer(
        ref RenderGraphFrame graph,
        GraphBufferId id,
        Buffer buffer,
        PipelineSync sync,
        ResourceAccess access,
        ResourceContentState contents,
        ReadOnlySpan<QueueCompletion> readiness = default)
    {
        BufferRange range = new(0, buffer.Info.Size);
        if (readiness.IsEmpty)
        {
            graph.BindExternalBuffer(
                id,
                buffer,
                [new BufferBoundaryState(range, sync, access, contents)]);
            return;
        }

        var endpoints = new BufferBoundaryState[readiness.Length];
        for (int index = 0; index < endpoints.Length; index++)
        {
            QueueCompletion completion = readiness[index];
            endpoints[index] = new BufferBoundaryState(
                range,
                sync,
                access,
                contents,
                completion.Queue,
                completion);
        }
        graph.BindExternalBuffer(id, buffer, endpoints);
    }

    private static void RecordBufferInitialization(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        ReadOnlySpan<GraphBufferId> buffers = frame.ClearBuffers;
        for (int index = 0; index < buffers.Length; index++)
        {
            ClusterBufferClearParameters passData = new(buffers[index]);
            _ = graph.AddCopyFramePass(
                $"Initialize Cluster frame buffer {index}",
                PassQueueSelection.AnyOfType(QueueType.Copy),
                passData,
                default,
                static (ref PassDefinition access, ref ClusterBufferClearParameters data) =>
                    _ = access.Write(
                        data.Buffer,
                        BufferRange.Whole,
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                ClusterBufferClearParameters.Record);
        }

        ClusterBufferCopyParameters copy = new(
            frame.CandidateArgsInitialization,
            frame.CandidateArgs,
            16);
        _ = graph.AddCopyFramePass(
            "Initialize Cluster candidate dispatch arguments",
            PassQueueSelection.AnyOfType(QueueType.Copy),
            copy,
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

    private static void RecordTargetInitialization(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        GraphColorAttachmentViewId vis = graph.CreateColorAttachmentView(
            frame.VisBuffer,
            label: "Cluster visibility clear view");
        GraphColorAttachmentViewId softwareDepth = graph.CreateColorAttachmentView(
            frame.SoftwareDepth,
            label: "Cluster software depth clear view");
        GraphColorAttachmentViewId scene = graph.CreateColorAttachmentView(
            frame.SceneColor,
            label: "Cluster scene clear view");
        GraphDepthStencilViewId depth = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
            label: "Cluster depth clear view");
        ClusterTargetClearParameters data = new(
            vis,
            softwareDepth,
            scene,
            depth);
        _ = graph.AddRasterFramePass(
            "Clear Cluster frame targets",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            default,
            ClusterTargetClearParameters.Declare,
            ClusterTargetClearParameters.Record);
    }

    private void AuthorHistoryInitialization(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        for (int mip = 0; mip < _history!.HiZMipCount; mip++)
        {
            GraphColorAttachmentViewId view = graph.CreateColorAttachmentView(
                frame.PreviousHiZ,
                new TextureSubresourceRange(
                    checked((uint)mip),
                    1,
                    0,
                    1,
                    TextureAspects.Color),
                label: $"Cluster HiZ history initialization mip {mip}");
            ClusterColorAttachmentClearParameters data = new(view, Vector4.Zero);
            _historyInitializationPasses.Add(graph.AddRasterFramePass(
                $"Initialize Cluster HiZ history mip {mip}",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                data,
                default,
                ClusterColorAttachmentClearParameters.Declare,
                ClusterColorAttachmentClearParameters.Record));
        }

    }

    private GraphBufferId UploadUniform<T>(
        ref RenderGraphEdit graph,
        T value,
        string name)
        where T : unmanaged
    {
        int valueSize = Unsafe.SizeOf<T>();
        int byteSize = checked((valueSize + 255) & ~255);
        Span<byte> bytes = stackalloc byte[byteSize];
        bytes.Clear();
        MemoryMarshal.Write(bytes, in value);
        return UploadBytes(ref graph, bytes, BufferUsages.Constant, name);
    }

    private GraphBufferId UploadStructs<T>(
        ref RenderGraphEdit graph,
        scoped ReadOnlySpan<T> values,
        BufferUsages usage,
        string name)
        where T : unmanaged
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one value.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    private GraphBufferId UploadWords(
        ref RenderGraphEdit graph,
        scoped ReadOnlySpan<uint> values,
        BufferUsages usage,
        string name)
    {
        if (values.IsEmpty)
            throw new ArgumentException("An upload requires at least one word.", nameof(values));
        return UploadBytes(ref graph, MemoryMarshal.AsBytes(values), usage, name);
    }

    private GraphBufferId UploadBytes(
        ref RenderGraphEdit graph,
        scoped ReadOnlySpan<byte> bytes,
        BufferUsages usage,
        string name)
    {
        return (_uploads ?? throw new InvalidOperationException(
            "Cluster retained upload storage was not created."))
            .Declare(ref graph, bytes, usage, name);
    }

    private static GraphBufferId DeclareExternalBuffer(
        ref RenderGraphEdit graph,
        ulong size,
        BufferUsages usages,
        string label) =>
        graph.DeclareExternalBuffer(new BufferDesc(size, usages, label));

    private struct FrameResources
    {
        private readonly FrameResourceScratch _scratch;

        internal FrameResources(FrameResourceScratch scratch)
            => _scratch = scratch ?? throw new ArgumentNullException(nameof(scratch));

        internal int Width;
        internal int Height;
        internal uint RasterBinCount;
        internal uint DeformBinCount;
        internal uint ShadeBinCount;
        internal uint SlotCapacity;
        internal int PageFaultCapacity;
        internal GraphBufferId PageHeap;
        internal GraphBufferId Bvh;
        internal GraphBufferId InstanceData;
        internal GraphBufferId InstanceProperties;
        internal BufferRange InstancePropertiesRange;
        internal GraphBufferId PageFaultReadback;
        internal GraphBufferId FrameMetricsReadback;
        internal GraphTextureId PreviousHiZ;
        internal GraphTextureId CurrentHiZ;
        internal GraphBufferId RasterSlots;
        internal GraphBufferId DeformSlots;
        internal GraphBufferId ShadeSlots;
        internal GraphBufferId ReadOffsetZero;
        internal GraphBufferId CandidateArgs;
        internal GraphBufferId CandidateArgsInitialization;
        internal GraphBufferId CandidateClusters;
        internal GraphBufferId CandidateCount;
        internal GraphBufferId DrawArgs;
        internal GraphBufferId PageFaults;
        internal GraphBufferId Phase2CandidateArgs;
        internal GraphBufferId Phase2CandidateClusters;
        internal GraphBufferId Phase2CandidateCount;
        internal GraphBufferId Phase2DrawArgs;
        internal GraphBufferId VisibleClusters;
        internal GraphBufferId RasterBinMeta;
        internal GraphBufferId BinnedClusters;
        internal GraphBufferId BinnedDrawArgs;
        internal GraphBufferId BinnedHardwareDrawArgs;
        internal GraphBufferId HardwareIndirectArgs;
        internal GraphBufferId BinningDispatchArgs;
        internal GraphBufferId SoftwareDispatchArgs;
        internal GraphBufferId RasterReserveCounters;
        internal GraphBufferId DeformBinMeta;
        internal GraphBufferId DeformBinnedClusters;
        internal GraphBufferId DeformDispatchArgs;
        internal GraphBufferId DeformReserveCounters;
        internal GraphBufferId CacheOffsets;
        internal GraphBufferId CacheAllocationCounter;
        internal GraphBufferId DeformCache;
        internal GraphBufferId SoftwareDebug;
        internal GraphBufferId ShadeBinCounts;
        internal GraphBufferId ShadeBinOffsets;
        internal GraphBufferId ShadeIndirectArgs;
        internal GraphBufferId ShadeScatterCounts;
        internal GraphBufferId ShadeReserveCounters;
        internal GraphBufferId PixelCoordinates;
        internal GraphTextureId VisBuffer;
        internal GraphTextureId Depth;
        internal GraphTextureId SoftwareDepth;
        internal GraphTextureId SceneColor;
        internal readonly ReadOnlySpan<GraphBufferId> ClearBuffers => _scratch.ClearBuffers;

        internal GraphBufferId AddBuffer(
            ref RenderGraphEdit graph,
            ulong size,
            BufferUsages additional,
            string name)
        {
            _scratch.EnsureCanAddClearBuffer();
            GraphBufferId result = graph.CreateTransientBuffer(new BufferDesc(
                Math.Max(size, 4),
                BufferUsages.ShaderRead |
                BufferUsages.ShaderWrite |
                BufferUsages.CopyDestination |
                additional,
                name));
            _scratch.AddClearBuffer(result);
            return result;
        }

    }

    /// <summary>Reusable owner for the variable-length scratch used while authoring one frame.</summary>
    private sealed class FrameResourceScratch
    {
        private const int ClearBufferCapacity = 64;

        private readonly GraphBufferId[] _clearBuffers = new GraphBufferId[ClearBufferCapacity];
        private int _clearBufferCount;
        private bool _active;

        internal ReadOnlySpan<GraphBufferId> ClearBuffers
        {
            get
            {
                EnsureActive();
                return _clearBuffers.AsSpan(0, _clearBufferCount);
            }
        }

        internal void Begin()
        {
            if (_active)
                throw new InvalidOperationException("Cluster frame-resource scratch is already active.");
            _active = true;
        }

        internal void End()
        {
            EnsureActive();
            _clearBufferCount = 0;
            _active = false;
        }

        internal void EnsureCanAddClearBuffer()
        {
            EnsureActive();
            if ((uint)_clearBufferCount >= (uint)_clearBuffers.Length)
            {
                throw new InvalidOperationException(
                    "The Cluster frame clear-buffer capacity is exhausted.");
            }
        }

        internal void AddClearBuffer(GraphBufferId buffer)
            => _clearBuffers[_clearBufferCount++] = buffer;

        private void EnsureActive()
        {
            if (!_active)
                throw new InvalidOperationException("Cluster frame-resource scratch is not active.");
        }
    }
}
