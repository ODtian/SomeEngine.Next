using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{

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
    internal const int FrameMetricsReadbackByteSize =
        VisibilityProbeReadbackOffset + VisibilityProbeRowPitch;
    private const uint MaxBinnedEntriesPerCluster = 9;
    private const uint ClusterVertexCapacity = 64;
    private const int RasterBinStride = 32;
    private const int VisibleClusterStride = 12;
    private const int VisibleClusterRasterWindowStride = 2 * sizeof(uint);
    private const int CandidateStride = 16;
    private const int BinnedClusterStride = 4;
    private const int TraversalNodeStride = 16;
    private const int TraversalArgumentStride = 32;
    private const int TessellationWorkPatchStride = 16;
    private const int TessellationFinalPatchStride = 20;
    private const int TessellationSplitLevelCount = 6;
    private FrameResources CreateFrameResources(
        ref RenderGraphEdit graph,
        in ClusterPipelineOutputDescription output,
        ClusterMaterialAssignmentSet assignments,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages)
    {
        uint rasterBinCount = rasterStages.BinCount;
        uint tessellationBinCount = checked(rasterBinCount * 8u);
        uint shadeBinCount = shadeStages.BinCount;
        ulong candidates = _options.MaxCandidates;
        uint entriesPerCluster = Math.Max(MaxBinnedEntriesPerCluster, _resources.MaximumRasterBatchesPerCluster);
        ulong binnedCapacity = checked(candidates * entriesPerCluster);
        int width = output.Width;
        int height = output.Height;
        uint viewCount = output.ViewCount;
        int pageFaultCapacity = _resources.PageFaultCapacity;
        ClusterResidencyBinding residency = _resources.Residency.GetBinding();
        ClusterGeometryBuffers geometryBuffers = _resources.Residency.GeometryBuffers;
        RenderInstanceResources instanceResources = _resources.InstanceResources;
        ulong instanceMetadataStride = checked((ulong)instanceResources.BatchMetadataStrideBytes);
        int mipCount = ClusterCullingHistory.CalculateMipCount(width, height);
        var historyDescription = new TextureDesc(TextureDimension.Texture2D,
            checked((uint)width), checked((uint)height), 1, checked((uint)mipCount), viewCount, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource,
            label: "Cluster HiZ history");
        var frame = new FrameResources()
        {
            BinnedEntriesPerCluster = entriesPerCluster,
            Width = width,
            Height = height,
            ViewCount = viewCount,
            RasterBinCount = rasterBinCount,
            ShadeBinCount = shadeBinCount,
            SlotCapacity = assignments.SlotCapacity,
            PageFaultCapacity = pageFaultCapacity,
            PageHeap = geometryBuffers.PageHeap,
            Bvh = geometryBuffers.Bvh,
            InstanceData = instanceResources.PropertyData,
            InstanceProperties = instanceResources.BatchMetadata,
            InstancePropertiesRange = new BufferRange(0, instanceMetadataStride),
            HiZ = [graph.CreatePersistentTexture(historyDescription), graph.CreatePersistentTexture(historyDescription)],
            PreviousHiZ = graph.CreateTextureInput(),
            CurrentHiZ = graph.CreateTextureInput(),
            RasterSlots = UploadBytes(
                ref graph,
                rasterStages.BinIndices,
                BufferUsages.ShaderRead,
                "Cluster raster stage slots"),
            ReadOffsetZero = UploadBytes(
                ref graph,
                stackalloc byte[16],
                BufferUsages.ShaderRead,
                "Cluster zero read offset"),
        };
        frame.ShadeSlots = UploadBytes(
            ref graph,
            shadeStages.BinIndices,
            BufferUsages.ShaderRead,
            "Cluster shade stage slots");
        if (_options.EnableFrameMetricsReadback)
        {
            frame.FrameMetricsBuffer = CreateBufferData(ref graph, ClusterBytes.Zero(FrameMetricsReadbackByteSize),
                BufferUsages.CopySource, "Cluster frame metric values");
        }

        GraphData.PageUpload = geometryBuffers.PageUpload;
        GraphData.BvhUpload = geometryBuffers.BvhUpload;
        frame.WorkStatus = frame.AddBuffer(ref graph,
            checked((ulong)(_options.EnableFrameMetricsReadback ? WorkMetricsByteSize : sizeof(uint))), BufferUsages.CopySource,
            "Cluster work overflow status");
        frame.CandidateArgs = frame.AddBuffer(
            ref graph,
            16,
            BufferUsages.Indirect | BufferUsages.CopySource,
            "Cluster candidate dispatch");
        frame.NeutralPolicyValue = UploadWords(
            ref graph,
            [0xFFFFFFFFu, 0xFFFFFFFFu, 0u, 0u],
            BufferUsages.ShaderRead,
            "Cluster neutral policy value");
        frame.NeutralPolicyCounter = frame.AddBuffer(
            ref graph,
            sizeof(uint),
            BufferUsages.None,
            "Cluster neutral policy counter");
        frame.NeutralPolicyView = UploadBytes(
            ref graph,
            // Shared by the neutral target-view (96 B), page-table (16 B),
            // receiver-mask (8 B) and uint views. Keep the byte size aligned to
            // every structured interpretation.
            stackalloc byte[checked((int)ClusterRenderContext.TargetViewSizeInBytes)],
            BufferUsages.ShaderRead,
            "Cluster neutral target view");
        frame.TraversalNodes0 = frame.AddBuffer(
            ref graph,
            checked(candidates * TraversalNodeStride),
            BufferUsages.None,
            "Cluster hierarchy nodes for even levels");
        frame.TraversalNodes1 = frame.AddBuffer(
            ref graph,
            checked(candidates * TraversalNodeStride),
            BufferUsages.None,
            "Cluster hierarchy nodes for odd levels");
        ulong traversalArgumentBytes = checked(
            ((ulong)_resources.HierarchyLevels + 1u) * TraversalArgumentStride);
        frame.TraversalArguments0 = frame.AddBuffer(
            ref graph,
            traversalArgumentBytes,
            BufferUsages.Indirect,
            "Cluster hierarchy indirect arguments 0");
        frame.TraversalArguments1 = frame.AddBuffer(
            ref graph,
            traversalArgumentBytes,
            BufferUsages.Indirect,
            "Cluster hierarchy indirect arguments 1");
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
            checked(sizeof(uint) + (ulong)pageFaultCapacity * 16u +
                (residency.BvhDescription.Size / 64u) * sizeof(uint)),
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
        if (_renderContext.IsValid)
        {
            frame.ContinuationDrawArgs = frame.AddBuffer(
                ref graph,
                16,
                BufferUsages.CopySource,
                "Cluster continuation counts");
            frame.ContinuationPostDrawArgs = frame.AddBuffer(
                ref graph,
                16,
                BufferUsages.CopySource,
                "Cluster continuation post counts");
        }
        frame.VisibleClusters = frame.AddBuffer(
            ref graph,
            checked(candidates * VisibleClusterStride),
            BufferUsages.None,
            "Cluster visible clusters");
        frame.VisibleClusterRasterWindows = frame.AddBuffer(
            ref graph,
            checked(candidates * VisibleClusterRasterWindowStride),
            BufferUsages.None,
            "Cluster visible raster windows");
        if (_renderContext.IsValid)
        {
            frame.TargetVisibleClusters = frame.AddBuffer(ref graph,
                checked(candidates * VisibleClusterStride), BufferUsages.None,
                "Cluster target visible clusters");
            frame.TargetRasterWindows = frame.AddBuffer(ref graph,
                checked(candidates * VisibleClusterRasterWindowStride), BufferUsages.None,
                "Cluster target raster windows");
            // The target main pass appends once. Neither primary nor target scratch resets
            // may overwrite this list before the target post-HiZ pass consumes it.
            frame.TargetDeferredWork = frame.AddBuffer(ref graph,
                checked(candidates * CandidateStride), BufferUsages.None, "Cluster target deferred work");
            frame.TargetDeferredCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.None,
                "Cluster target deferred count");
            frame.TargetDeferredArgs = frame.AddBuffer(ref graph, 16, BufferUsages.Indirect,
                "Cluster target deferred dispatch");
        }
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
        frame.HardwareIndirectArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * ClusterIndirectAbi.HardwareRasterStride),
            BufferUsages.Indirect,
            "Cluster hardware indirect arguments");
        frame.RasterWorkArguments = frame.AddBuffer(
            ref graph,
            ClusterRasterArguments.HiZPairOffset(mipCount),
            BufferUsages.Indirect,
            "Cluster raster and screen arguments");
        frame.SoftwareDispatchArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster software raster dispatches");
        frame.RasterReserveCounters = frame.AddBuffer(ref graph, 4 * sizeof(uint), BufferUsages.CopySource, "Cluster raster reserve counters");
        frame.TessellationCounters = frame.AddBuffer(
            ref graph,
            8 * sizeof(uint),
            BufferUsages.CopySource,
            "Cluster tessellation counters");
        frame.PhaseOneTessellationCounters = frame.AddBuffer(
            ref graph,
            8 * sizeof(uint),
            BufferUsages.CopySource,
            "Cluster phase one tessellation counters");
        frame.TessellationWorkPatches = frame.AddBuffer(
            ref graph,
            checked((ulong)_options.MaxCandidates * TessellationWorkPatchStride),
            BufferUsages.Vertex,
            "Cluster tessellation work patches");
        frame.TessellationWorkPatchesAlternate = frame.AddBuffer(
            ref graph,
            checked((ulong)_options.MaxCandidates * TessellationWorkPatchStride),
            BufferUsages.None,
            "Cluster tessellation alternate work patches");
        frame.TessellationFinalPatches = frame.AddBuffer(
            ref graph,
            checked((ulong)_options.MaxCandidates * TessellationFinalPatchStride),
            BufferUsages.None,
            "Cluster tessellation final patches");
        frame.TessellationLevelCounts = frame.AddBuffer(
            ref graph,
            TessellationSplitLevelCount * sizeof(uint),
            BufferUsages.None,
            "Cluster tessellation level counts");
        frame.TessellationLevelDispatchArgs = new GraphBufferId[TessellationSplitLevelCount + 1];
        for (int level = 0; level <= TessellationSplitLevelCount; level++)
        {
            frame.TessellationLevelDispatchArgs[level] = frame.AddBuffer(
                ref graph,
                ClusterIndirectAbi.DispatchBytes,
                BufferUsages.Indirect,
                $"Cluster tessellation level {level} dispatch");
        }
        frame.TessellationSourceDispatchArgs = frame.AddBuffer(
            ref graph,
            ClusterIndirectAbi.DispatchBytes,
            BufferUsages.Indirect,
            "Cluster tessellation source dispatch");
        frame.TessellationSeedDispatchArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)rasterBinCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster tessellation seed dispatches");
        frame.TessellationBinCounts = frame.AddBuffer(
            ref graph,
            checked((ulong)tessellationBinCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster tessellation bin counts");
        frame.TessellationBinOffsets = frame.AddBuffer(
            ref graph,
            checked((ulong)tessellationBinCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster tessellation bin offsets");
        frame.TessellationBinScatterCounts = frame.AddBuffer(
            ref graph,
            checked((ulong)tessellationBinCount * sizeof(uint)),
            BufferUsages.None,
            "Cluster tessellation bin scatter counts");
        frame.TessellationBinReserveCounter = frame.AddBuffer(
            ref graph,
            sizeof(uint),
            BufferUsages.None,
            "Cluster tessellation bin reserve counter");
        frame.TessellationBinDispatchArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)tessellationBinCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster tessellation split or dice dispatches");
        frame.TessellationHardwareDrawArgs = frame.AddBuffer(
            ref graph,
            checked((ulong)tessellationBinCount * ClusterIndirectAbi.DrawIndexedBytes +
                (ulong)rasterBinCount * ClusterIndirectAbi.DispatchBytes),
            BufferUsages.Indirect,
            "Cluster tessellation hardware draw arguments");
        if (_visibilityMode == ClusterVisibilityMode.HardwareOnly && !ClusterRasterPipeline.SupportsMesh(_backend, _device))
            frame.TessellationPatternIndices = UploadStructs<ushort>(ref graph,
                CreateTessellationPatternIndices(), BufferUsages.Index | BufferUsages.ShaderRead,
                "Cluster tessellation pattern indices");
        ulong softwareDebugBytes =
            _visibilityMode != ClusterVisibilityMode.HardwareOnly &&
            _options.EnableFrameMetricsReadback
                ? 4ul + 8192ul * 24ul
                : (ulong)sizeof(uint);
        frame.SoftwareDebug = frame.AddBuffer(
            ref graph,
            softwareDebugBytes,
            BufferUsages.CopySource,
            "Cluster software raster debug output");

        frame.ShadeBinCounts = frame.AddBuffer(
                ref graph,
                checked((ulong)shadeBinCount * 2ul * sizeof(uint)),
                BufferUsages.None,
                "Cluster shade bin counts");
            frame.ShadeBinOffsets = frame.AddBuffer(
                ref graph,
                checked((ulong)shadeBinCount * 2ul * sizeof(uint)),
                BufferUsages.None,
                "Cluster shade bin offsets");
            frame.ShadeIndirectArgs = frame.AddBuffer(
                ref graph,
                checked((ulong)shadeBinCount * ClusterIndirectAbi.DispatchBytes),
                BufferUsages.Indirect,
                "Cluster shade dispatches");
            frame.ShadeScatterCounts = frame.AddBuffer(
                ref graph,
                checked((ulong)shadeBinCount * 2ul * sizeof(uint)),
                BufferUsages.None,
                "Cluster shade scatter counts");
            frame.ShadeReserveCounters = frame.AddBuffer(
                ref graph,
                sizeof(uint),
                BufferUsages.CopySource,
                "Cluster shade reserve counter");
        frame.PixelCoordinates = frame.AddBuffer(
                ref graph,
                checked((ulong)width * (ulong)height * viewCount * 2ul * sizeof(uint)),
                BufferUsages.None,
                "Cluster shade tiles and quads");

        frame.VisBuffer = graph.CreateTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1,
                1,
                viewCount,
                1,
                Format.R32UInt,
                TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment |
                TextureUsages.CopySource,
                label: "Cluster visibility buffer"));
            if (_visibilityMode == ClusterVisibilityMode.ImageAtomic64)
            {
                frame.VisibilityWinnerImage = graph.CreateTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    checked((uint)width),
                    checked((uint)height),
                    1,
                    1,
                    viewCount,
                    1,
                    Format.R64UInt,
                    TextureUsages.Sampled | TextureUsages.Storage,
                    label: "Cluster visibility winner image"));
            }
            else if (_visibilityMode == ClusterVisibilityMode.BufferAtomic64)
            {
                frame.VisibilityWinnerBuffer = frame.AddBuffer(
                    ref graph,
                    checked((ulong)width * (ulong)height * viewCount * 8u),
                    BufferUsages.None,
                    "Cluster visibility winner buffer");
            }
            frame.Depth = graph.CreateTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1,
                1,
                viewCount,
                1,
                Format.D32Float,
                TextureUsages.Sampled | TextureUsages.DepthStencilAttachment | TextureUsages.CopySource,
                label: "Cluster scene depth"));
            {
                frame.NeutralPolicyOutput = graph.CreateTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    1,
                    1,
                    1,
                    1,
                    1,
                    1,
                    Format.R32UInt,
                    TextureUsages.Storage | TextureUsages.ColorAttachment,
                    label: "Cluster neutral target output"));
                if (_visibilityMode == ClusterVisibilityMode.HardwareOnly && _renderContext.IsValid)
                {
                    frame.PolicyVisibilityScratch = graph.CreateTexture(new TextureDesc(
                        TextureDimension.Texture2D,
                        _renderContext.RasterWidth,
                        _renderContext.RasterHeight,
                        1,
                        1,
                        1,
                        1,
                        Format.R32UInt,
                        TextureUsages.ColorAttachment,
                        label: "Cluster policy hardware visibility scratch"));
                    frame.PolicyDepthScratch = graph.CreateTexture(new TextureDesc(
                        TextureDimension.Texture2D,
                        _renderContext.RasterWidth,
                        _renderContext.RasterHeight,
                        1,
                        1,
                        1,
                        1,
                        Format.D32Float,
                        TextureUsages.DepthStencilAttachment,
                        label: "Cluster policy hardware depth scratch"));
                }
            }
        frame.SurfaceReceivers = graph.CreateBuffer(new BufferDesc(checked((ulong)width * (ulong)height * viewCount * 32u),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Cluster surface receivers"));
        frame.AdditionalReceiverLayerCount = shadeStages.HasAdditionalReceivers ? 3u : 0u;
        frame.AdditionalSurfaceReceivers = graph.CreateBuffer(new BufferDesc(
            Math.Max(32u, checked((ulong)width * (ulong)height * viewCount * 32u * frame.AdditionalReceiverLayerCount)),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination, "Cluster additional surface receivers"));
        frame.SceneColor = graph.CreateTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1,
                1,
                viewCount,
                1,
                Format.R16G16B16A16Float,
                TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopySource,
                label: "Cluster scene color"));
        frame.MotionVectors = graph.CreateTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1,
                1,
                viewCount,
                1,
                Format.R16G16Float,
                TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource,
                label: "Cluster motion vectors"));
        RecordBufferInitialization(ref graph, in frame);
        RecordTargetInitialization(ref graph, in frame);
        RecordVisibilityWinnerClear(ref graph, in frame);
        return frame;
    }

    private void RecordVisibilityWinnerClear(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        if (_visibilityMode == ClusterVisibilityMode.HardwareOnly)
        {
            return;
        }

        ClusterComputePipeline pipeline = _pipelines?.WinnerClear
            ?? throw new InvalidOperationException("The selected A64 visibility mode has no clear pipeline.");
        GraphBufferInput uniforms = UploadUniform(
            ref graph,
            new ClusterVisibilityWinnerUniforms
            {
                Width = checked((uint)frame.Width),
                Height = checked((uint)frame.Height),
                ViewCount = frame.ViewCount,
            },
            "Cluster visibility winner clear uniforms");
        int bindingCount = 0;
        AddBinding(ref bindingCount, GraphParameterResourceBinding.ConstantBuffer(
            CreateConstantBufferView(graph, uniforms), PipelineSync.ComputeShading));
        if (_visibilityMode == ClusterVisibilityMode.ImageAtomic64)
        {
            AddBinding(ref bindingCount, GraphParameterResourceBinding.StorageTexture(
                CreateStorageTextureView(
                    graph,
                    frame.VisibilityWinnerImage,
                    Format.R64UInt,
                    null,
                    TextureViewDimension.Texture2DArray),
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Complete));
            _ = AddComputePass(
                ref graph,
                "Clear Cluster visibility winner image",
                pipeline,
                new ClusterDispatchParameters(new DispatchArguments(
                    Groups(frame.Width, 8),
                    Groups(frame.Height, 8),
                    frame.ViewCount)),
                bindingCount,
                ClusterDispatchParameters.Record);
            return;
        }

        AddBinding(ref bindingCount, GraphParameterResourceBinding.WritableBuffer(
            CreateStorageBufferView(graph, frame.VisibilityWinnerBuffer),
            PipelineSync.ComputeShading,
            GraphAccessMode.Write,
            WriteCoverage.Complete));
        ulong pixels = checked((ulong)frame.Width * (ulong)frame.Height * frame.ViewCount);
        ulong groups = (pixels + 63u) / 64u;
        uint groupsX = checked((uint)Math.Min(groups, 65_535u));
        uint groupsY = checked((uint)((groups + 65_534u) / 65_535u));
        _ = AddComputePass(
            ref graph,
            "Clear Cluster visibility winner buffer",
            pipeline,
            new ClusterDispatchParameters(new DispatchArguments(groupsX, groupsY, 1)),
            bindingCount,
            ClusterDispatchParameters.Record);
    }

    private void GrowRasterWork(ref RenderGraphEdit graph)
    {
        FrameResources frame = GraphData.Resources;
        frame.BinnedEntriesPerCluster = _resources.MaximumRasterBatchesPerCluster;
        SetStageBuffer(ref graph, frame.BinnedClusters,
            checked((ulong)_options.MaxCandidates * frame.BinnedEntriesPerCluster * BinnedClusterStride),
            BufferUsages.None, "Cluster raster bin indices");
        GraphData.Resources = frame;
        UpdateRasterBinning(ref graph, GraphData.RasterPhases);
    }

    private void ReconcileStageResources(
        ref RenderGraphEdit graph,
        ClusterMaterialAssignmentSet assignments,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages)
    {
        FrameResources frame = GraphData.Resources;
        uint rasterBinCount = rasterStages.BinCount;
        uint tessellationBinCount = checked(rasterBinCount * 8u);
        uint shadeBinCount = shadeStages.BinCount;

        uint additionalLayers = shadeStages.HasAdditionalReceivers ? 3u : 0u;
        if (additionalLayers != frame.AdditionalReceiverLayerCount)
        {
            frame.AdditionalReceiverLayerCount = additionalLayers;
            SetStageBuffer(ref graph, frame.AdditionalSurfaceReceivers,
                Math.Max(32u, checked((ulong)frame.Width * (ulong)frame.Height * frame.ViewCount * 32u * additionalLayers)),
                BufferUsages.None, "Cluster additional surface receivers");
            GraphData.Outputs = GraphData.Outputs with { AdditionalReceiverLayerCount = additionalLayers };
        }

        bool rasterCapacityChanged = frame.RasterBinCount != rasterBinCount;
        bool shadeCapacityChanged = frame.ShadeBinCount != shadeBinCount;
        frame.RasterBinCount = rasterBinCount;
        frame.ShadeBinCount = shadeBinCount;
        frame.SlotCapacity = assignments.SlotCapacity;
        UpdateMaterialIndexBuffers(ref graph, rasterStages, shadeStages);

        if (rasterCapacityChanged)
        {
            SetStageBuffer(
                ref graph,
                frame.RasterBinMeta,
                checked((ulong)rasterBinCount * RasterBinStride),
                BufferUsages.None,
                "Cluster raster bins");
            SetStageBuffer(
                ref graph,
                frame.HardwareIndirectArgs,
                checked((ulong)rasterBinCount * ClusterIndirectAbi.HardwareRasterStride),
                BufferUsages.Indirect,
                "Cluster hardware indirect arguments");
            SetStageBuffer(
                ref graph,
                frame.SoftwareDispatchArgs,
                checked((ulong)rasterBinCount * ClusterIndirectAbi.DispatchBytes),
                BufferUsages.Indirect,
                "Cluster software raster dispatches");
            SetStageBuffer(ref graph, frame.TessellationSeedDispatchArgs,
                checked((ulong)rasterBinCount * ClusterIndirectAbi.DispatchBytes),
                BufferUsages.Indirect, "Cluster tessellation seed dispatches");
            SetStageBuffer(ref graph, frame.TessellationBinCounts, checked((ulong)tessellationBinCount * sizeof(uint)),
                BufferUsages.None, "Cluster tessellation bin counts");
            SetStageBuffer(ref graph, frame.TessellationBinOffsets, checked((ulong)tessellationBinCount * sizeof(uint)),
                BufferUsages.None, "Cluster tessellation bin offsets");
            SetStageBuffer(ref graph, frame.TessellationBinScatterCounts, checked((ulong)tessellationBinCount * sizeof(uint)),
                BufferUsages.None, "Cluster tessellation bin scatter counts");
            SetStageBuffer(ref graph, frame.TessellationBinDispatchArgs,
                checked((ulong)tessellationBinCount * ClusterIndirectAbi.DispatchBytes),
                BufferUsages.Indirect, "Cluster tessellation split or dice dispatches");
            SetStageBuffer(ref graph, frame.TessellationHardwareDrawArgs,
                checked((ulong)tessellationBinCount * ClusterIndirectAbi.DrawIndexedBytes +
                    (ulong)rasterBinCount * ClusterIndirectAbi.DispatchBytes),
                BufferUsages.Indirect, "Cluster tessellation hardware draw arguments");
        }
        if (shadeCapacityChanged)
        {
            SetStageBuffer(ref graph, frame.ShadeBinCounts,
                checked((ulong)shadeBinCount * 2ul * sizeof(uint)),
                BufferUsages.None, "Cluster shade bin counts");
            SetStageBuffer(ref graph, frame.ShadeBinOffsets,
                checked((ulong)shadeBinCount * 2ul * sizeof(uint)),
                BufferUsages.None, "Cluster shade bin offsets");
            SetStageBuffer(ref graph, frame.ShadeIndirectArgs,
                checked((ulong)shadeBinCount * ClusterIndirectAbi.DispatchBytes),
                BufferUsages.Indirect, "Cluster shade dispatches");
            SetStageBuffer(ref graph, frame.ShadeScatterCounts,
                checked((ulong)shadeBinCount * 2ul * sizeof(uint)),
                BufferUsages.None, "Cluster shade scatter counts");
        }
        GraphData.Resources = frame;
    }

    private static void SetStageBuffer(
        ref RenderGraphEdit graph,
        GraphBufferId buffer,
        ulong size,
        BufferUsages additional,
        string label)
    {
        graph.SetBufferDescription(
            buffer,
            new BufferDesc(
                Math.Max(size, 4),
                BufferUsages.ShaderRead |
                BufferUsages.ShaderWrite |
                BufferUsages.CopyDestination |
                additional,
                label));
    }

    private void BindFrameResources(
        ref RenderGraphFrame graph,
        in ClusterRenderBinding binding,
        ClusterCullingHistory history)
    {
        FrameResources frame = GraphData.Resources;
        graph.SetBufferRange(
            frame.InstanceProperties,
            binding.InstancePropertyMetadataRange);

        foreach ((GraphBufferId buffer, var writes) in GraphData.BufferWrites)
            foreach (BufferWrite write in writes)
                write.Bytes.CopyTo(graph.GetWriteSpan(buffer, write.Range.Offset, checked((int)write.Range.Size)),
                    checked((int)write.Range.Offset));
        int current = history.WriteIndex;
        graph.SetTextureInput(frame.PreviousHiZ, frame.HiZ[1 - current]);
        graph.SetTextureInput(frame.CurrentHiZ, frame.HiZ[current]);
        GraphData.Outputs = GraphData.Outputs with { CurrentHiZ = frame.HiZ[current] };
    }

    private void RecordResidencyUploads(ref RenderGraphFrame graph, in ClusterRenderBinding binding)
    {
        if (binding.Publication is not { } publication) return;
        foreach (ClusterStagedUpload upload in publication.Meshes.PageUploads)
            graph.WriteBuffer(GraphData.Resources.PageHeap, upload.Offset, upload.Bytes.Span);
        foreach (ClusterStagedUpload upload in publication.Meshes.Bvh.Uploads)
            graph.WriteBuffer(GraphData.Resources.Bvh, upload.Offset, upload.Bytes.Span);
    }

    private void DeclareResidencyReads(
        ref RenderGraphFrame graph,
        in ClusterRenderBinding binding)
    {
        graph.SetBufferRanges(GraphData.Resources.PageHeap, _residentPageReads);

        graph.SetBufferRanges(GraphData.Resources.Bvh, GraphData.Outputs.Geometry.BvhReadRanges);
    }

    private QueueCompletion[][] CaptureRetirementCompletions(
        ClusterResidencyPublication publication)
    {
        PageRetirement[] retirements = publication.Meshes.Retirements;
        var result = new QueueCompletion[retirements.Length][];
        GraphBufferId pageHeap = GraphData.Resources.PageHeap;
        for (int index = 0; index < retirements.Length; index++)
        {
            PageRetirement retirement = retirements[index];
            var range = new BufferRange(retirement.Offset, retirement.Size);
            int count = _graph.GetRangeCompletionCount(pageHeap, range);
            if (count == 0)
            {
                result[index] = [];
                continue;
            }
            var completions = new QueueCompletion[count];
            int copied = _graph.CopyRangeCompletions(
                pageHeap,
                range,
                completions);
            if (copied != count)
                throw new InvalidOperationException("Cluster page retirement completions changed during capture.");
            result[index] = completions;
        }
        return result;
    }


    private void WriteFrameUploads(
        ref RenderGraphFrame graph, ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages, scoped ReadOnlySpan<ClusterViewUniforms> packedViews,
        int primaryViewCount)
    {
        graph.SetBufferData(GraphData.Resources.PackedViews, MemoryMarshal.AsBytes(packedViews));
        Span<byte> parametersBytes = graph.GetBufferDataWriteSpan(GraphData.ShaderParameterBytes);
        parametersBytes.Clear();
        foreach ((GraphBufferInput input, ShaderParameters parameters) in GraphData.Parameters)
        {
            graph.SetBufferInput(input, new GraphBufferSlice(GraphData.ShaderParameterBytes,
                new BufferRange(checked((ulong)parameters.Offset), checked((ulong)parameters.Capacity))));
            if (input == GraphData.SceneUniforms || input == GraphData.PrimarySeedUniforms || input == GraphData.TargetSeedUniforms) continue;
            parameters.Bytes.CopyTo(parametersBytes.Slice(parameters.Offset));
        }
        if (_options.EnableFrameMetricsReadback)
        {
            ulong frameIndex = checked(++_metricFrameIndex);
            graph.WriteBuffer(GraphData.Resources.FrameMetricsBuffer, MetricsFrameIndexOffset,
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref frameIndex, 1)));
        }
        WriteUniform(parametersBytes, _sceneValues, GraphData.SceneUniforms);
        WriteUniform(parametersBytes, new ClusterTraversalSeedUniforms
        {
            ViewCount = checked((uint)primaryViewCount), ViewIndexMode = 0u,
            MaxNodes = _options.MaxCandidates, UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
        }, GraphData.PrimarySeedUniforms);
        if (_renderContext.IsValid)
            WriteUniform(parametersBytes, new ClusterTraversalSeedUniforms
            {
                ViewCount = _renderContext.MaximumInvocationCount, ViewIndexMode = 1u,
                MaxNodes = _options.MaxCandidates, UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
            }, GraphData.TargetSeedUniforms);
    }

    private void WriteUniform<T>(Span<byte> bytes, in T value, GraphBufferInput input)
        where T : unmanaged => MemoryMarshal.Write(bytes.Slice(GraphData.Parameters[input].Offset), in value);

    private void RecordBufferInitialization(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        AddSmallClear(ref graph, frame.CandidateCount, new BufferRange(0, sizeof(uint)),
            "Initialize Cluster candidate count");
        AddSmallClear(ref graph, frame.Phase2CandidateCount, new BufferRange(0, sizeof(uint)),
            "Initialize Cluster deferred work count");
        AddSmallClear(
            ref graph,
            frame.TraversalArguments0,
            BufferRange.Whole,
            "Initialize Cluster hierarchy arguments 0");
        AddSmallClear(
            ref graph,
            frame.TraversalArguments1,
            BufferRange.Whole,
            "Initialize Cluster hierarchy arguments 1");
        AddSmallClear(
            ref graph,
            frame.PageFaults,
            BufferRange.Whole,
            "Initialize Cluster page-fault count");
        AddSmallClear(ref graph, frame.SoftwareDebug, new BufferRange(0, sizeof(uint)),
            "Initialize Cluster software debug count");
        AddSmallClear(ref graph, frame.NeutralPolicyCounter, new BufferRange(0, sizeof(uint)),
            "Initialize Cluster neutral policy counter");

        ClusterBufferCopyParameters copy = new(
            frame.CandidateArgsInitialization,
            0,
            frame.CandidateArgs,
            0,
            16);
        _ = graph.AddCopyFramePass(
            "Initialize Cluster candidate dispatch arguments",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            copy,
            new PassOptions(RepeatCommands: true),
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
            ClusterBufferCopyParameters.Record, region: _regions.PrimaryGeometry);

        ClusterBufferCopyParameters phaseTwoCopy = new(
            frame.CandidateArgsInitialization,
            0,
            frame.Phase2CandidateArgs,
            0,
            16);
        _ = graph.AddCopyFramePass(
            "Initialize Cluster deferred dispatch arguments",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            phaseTwoCopy,
            new PassOptions(RepeatCommands: true),
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
            ClusterBufferCopyParameters.Record, region: _regions.PrimaryGeometry);

        void AddSmallClear(
            ref RenderGraphEdit activeGraph,
            GraphBufferId buffer,
            in BufferRange range,
            string label)
        {
            var clear = new ClusterBufferRangeClearParameters(buffer, range);
            _ = activeGraph.AddCopyFramePass(
                label,
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                clear,
                new PassOptions(RepeatCommands: true),
                static (ref PassDefinition access, ref ClusterBufferRangeClearParameters data) =>
                    _ = access.Write(
                        data.Buffer,
                        data.Range,
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                ClusterBufferRangeClearParameters.Record, region: _regions.PrimaryGeometry);
        }
    }

    private void RecordTargetInitialization(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        TextureSubresourceRange colors = new(
            0,
            1,
            0,
            frame.ViewCount,
            TextureAspects.Color);
        GraphColorAttachmentViewId vis = graph.CreateColorAttachmentView(
            frame.VisBuffer,
            colors,
            dimension: TextureViewDimension.Texture2DArray,
            label: "Cluster visibility clear view");
        GraphColorAttachmentViewId scene = graph.CreateColorAttachmentView(
            frame.SceneColor,
            colors,
            dimension: TextureViewDimension.Texture2DArray,
            label: "Cluster scene clear view");
        GraphDepthStencilViewId depth = graph.CreateDepthStencilView(
            frame.Depth,
            new TextureSubresourceRange(
                0,
                1,
                0,
                frame.ViewCount,
                TextureAspects.Depth),
            dimension: TextureViewDimension.Texture2DArray,
            label: "Cluster depth clear view");
        ClusterTargetClearParameters data = new(
            vis,
            scene,
            depth);
        _ = graph.AddRasterFramePass(
            "Clear Cluster frame targets",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            data,
            new PassOptions(RepeatCommands: true),
            ClusterTargetClearParameters.Declare,
            ClusterTargetClearParameters.Record, region: _regions.PrimaryGeometry);

        {
            GraphColorAttachmentViewId neutralPolicyOutput = graph.CreateColorAttachmentView(
                frame.NeutralPolicyOutput,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                dimension: TextureViewDimension.Texture2D,
                label: "Cluster neutral target output clear view");
            ClusterColorAttachmentClearParameters neutralData = new(
                neutralPolicyOutput,
                Vector4.Zero);
            _ = graph.AddRasterFramePass(
                "Clear Cluster neutral target output",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                neutralData,
                new PassOptions(RepeatCommands: true),
                ClusterColorAttachmentClearParameters.Declare,
                ClusterColorAttachmentClearParameters.Record, region: _regions.PrimaryGeometry);
        }
        if (frame.PolicyVisibilityScratch != default && frame.PolicyDepthScratch != default)
        {
            GraphColorAttachmentViewId policyVisibility = graph.CreateColorAttachmentView(
                frame.PolicyVisibilityScratch,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                format: Format.R32UInt,
                dimension: TextureViewDimension.Texture2DArray,
                label: "Cluster policy hardware visibility scratch clear view");
            GraphDepthStencilViewId policyDepth = graph.CreateDepthStencilView(
                frame.PolicyDepthScratch,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
                Format.D32Float,
                TextureViewDimension.Texture2DArray,
                label: "Cluster policy hardware depth scratch clear view");
            ClusterPolicyTargetClearParameters policyData = new(
                policyVisibility,
                policyDepth);
            _ = graph.AddRasterFramePass(
                "Clear Cluster policy hardware scratch",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                policyData,
                new PassOptions(RepeatCommands: true),
                ClusterPolicyTargetClearParameters.Declare,
                ClusterPolicyTargetClearParameters.Record, region: _regions.PrimaryGeometry);
        }
    }

    private void AuthorHistoryInitialization(
        ref RenderGraphEdit graph,
        in FrameResources frame)
    {
        GraphData.HistoryInitialization = graph.AddRegion("Cluster history initialization", _regions.PrimaryGeometry);
        foreach (GraphTextureId texture in frame.HiZ)
        for (int mip = 0; mip < ClusterCullingHistory.CalculateMipCount(frame.Width, frame.Height); mip++)
        {
            GraphColorAttachmentViewId view = graph.CreateColorAttachmentView(texture,
                new TextureSubresourceRange(checked((uint)mip), 1, 0, frame.ViewCount, TextureAspects.Color),
                dimension: TextureViewDimension.Texture2DArray, label: $"Cluster HiZ initialization mip {mip}");
            var data = new ClusterColorAttachmentClearParameters(view, Vector4.Zero);
            graph.AddRasterFramePass($"Initialize Cluster HiZ mip {mip}", PassQueueSelection.AnyOfType(QueueType.Graphics),
                data, default, ClusterColorAttachmentClearParameters.Declare, ClusterColorAttachmentClearParameters.Record,
                region: GraphData.HistoryInitialization);
        }
    }

    private GraphBufferInput UploadUniform<T>(
        ref RenderGraphEdit graph,
        T value,
        string name)
        where T : unmanaged
    {
        int valueSize = Unsafe.SizeOf<T>();
        Span<byte> bytes = stackalloc byte[valueSize];
        MemoryMarshal.Write(bytes, in value);
        GraphBufferInput input = CreateParameters(ref graph, bytes, name);
        _rasterInputs?.Uniforms.Add(input);
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

    private GraphBufferId UploadBytes(ref RenderGraphEdit graph, ClusterBytes bytes,
        BufferUsages usages, string label)
    {
        GraphBufferId buffer = CreateBufferData(ref graph, bytes, usages, label);
        _rasterInputs?.Buffers.Add(buffer);
        return buffer;
    }

    private GraphBufferId UploadBytes(
        ref RenderGraphEdit graph,
        scoped ReadOnlySpan<byte> bytes,
        BufferUsages usage,
        string name)
    {
        GraphBufferId buffer = CreateBufferData(ref graph, bytes, usage, name);
        _rasterInputs?.Buffers.Add(buffer);
        return buffer;
    }

    private static FrameResources GetRasterFrameResources(in FrameResources frame, bool target)
    {
        FrameResources resources = frame;
        if (target)
        {
            resources.VisibleClusters = frame.TargetVisibleClusters;
            resources.VisibleClusterRasterWindows = frame.TargetRasterWindows;
            resources.Phase2CandidateClusters = frame.TargetDeferredWork;
            resources.Phase2CandidateCount = frame.TargetDeferredCount;
            resources.Phase2CandidateArgs = frame.TargetDeferredArgs;
        }
        return resources;
    }

    private struct FrameResources
    {
        public FrameResources() { }
        internal int Width;
        internal int Height;
        internal uint ViewCount;
        internal uint RasterBinCount;
        internal uint ShadeBinCount;
        internal uint SlotCapacity;
        internal uint BinnedEntriesPerCluster;
        internal int PageFaultCapacity;
        internal GraphBufferId PageHeap;
        internal GraphBufferId Bvh;
        internal GraphBufferId WorkStatus;
        internal GraphBufferId InstanceData;
        internal GraphBufferId InstanceProperties;
        internal BufferRange InstancePropertiesRange;
        internal GraphBufferId FrameMetricsBuffer;
        internal GraphTextureId[] HiZ = [];
        internal GraphTextureInput PreviousHiZ = null!;
        internal GraphTextureInput CurrentHiZ = null!;
        internal GraphBufferId PackedViews;
        internal GraphBufferId RasterSlots;
        internal GraphBufferId ShadeSlots;
        internal GraphBufferId ReadOffsetZero;
        internal GraphBufferId CandidateArgs;
        internal GraphBufferId NeutralPolicyValue;
        internal GraphBufferId NeutralPolicyCounter;
        internal GraphBufferId NeutralPolicyView;
        internal GraphTextureId NeutralPolicyOutput;
        internal GraphBufferId TraversalNodes0;
        internal GraphBufferId TraversalNodes1;
        internal GraphBufferId TraversalArguments0;
        internal GraphBufferId TraversalArguments1;
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
        internal GraphBufferId TargetRasterWindows;
        internal GraphBufferId VisibleClusterRasterWindows;
        internal GraphBufferId RasterBinMeta;
        internal GraphBufferId BinnedClusters;
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
        internal GraphBufferId SurfaceReceivers;
        internal GraphBufferId AdditionalSurfaceReceivers;
        internal uint AdditionalReceiverLayerCount;
        internal GraphTextureId MotionVectors;
        internal GraphBufferId AddBuffer(
            ref RenderGraphEdit graph,
            ulong size,
            BufferUsages additional,
            string name)
        {
            return graph.CreateTransientBuffer(new BufferDesc(
                Math.Max(size, 4),
                BufferUsages.ShaderRead |
                BufferUsages.ShaderWrite |
                BufferUsages.CopyDestination |
                additional,
                name));
        }

    }

}
