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
        ulong binnedCapacity = checked(candidates * MaxBinnedEntriesPerCluster);
        int width = output.Width;
        int height = output.Height;
        uint viewCount = output.ViewCount;
        int pageFaultCapacity = _resources.PageFaultCapacity;
        ClusterResidencyBinding residency = _resources.Residency.GetBinding();
        RenderInstanceResources instanceResources = _resources.InstanceResources;
        ulong instanceDataBytes = checked((ulong)instanceResources.PropertyDataBytes);
        ulong instanceMetadataStride = checked((ulong)instanceResources.BatchMetadataStrideBytes);
        ulong instanceMetadataBytes = checked(
            instanceMetadataStride * (ulong)instanceResources.Options.BatchCapacity);
        ClusterCullingHistory history = _history ?? throw new InvalidOperationException(
            "The Cluster culling history was not created.");
        history.EnsureDimensions(
            width,
            height);
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

        var frame = new FrameResources()
        {
            Width = width,
            Height = height,
            ViewCount = viewCount,
            RasterBinCount = rasterBinCount,
            ShadeBinCount = shadeBinCount,
            SlotCapacity = assignments.SlotCapacity,
            PageFaultCapacity = pageFaultCapacity,
            PageHeap = graph.RegisterExternalBuffer(residency.PageHeap, []),
            Bvh = graph.RegisterExternalBuffer(residency.Bvh, []),
            InstanceData = _instanceDataTransfer?.Data ?? DeclareExternalBuffer(ref graph, instanceDataBytes, BufferUsages.ShaderRead,
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
        frame.DeformCache = frame.AddBuffer(ref graph, _options.MaxDeformCacheBytes,
            BufferUsages.None, "Cluster user deformation bytes");
        frame.DeformRegions = frame.AddBuffer(ref graph, candidates * 8,
            BufferUsages.None, "Cluster user deformation regions");
        frame.DeformRequests = frame.AddBuffer(ref graph, candidates * 8,
            BufferUsages.None, "Cluster deformation byte requests");
        frame.DeformPrefixOffsets = frame.AddBuffer(ref graph, candidates * 4,
            BufferUsages.None, "Cluster deformation prefix offsets");
        frame.DeformPrefixGroups = frame.AddBuffer(ref graph, ((candidates + 255) / 256) * 8,
            BufferUsages.None, "Cluster deformation prefix groups");
        frame.DeformAllocation = frame.AddBuffer(ref graph, 4,
            BufferUsages.None, "Cluster deformation allocated bytes");
        frame.DeformArguments = frame.AddBuffer(ref graph, 24,
            BufferUsages.Indirect, "Cluster deformation dispatch arguments");
        if (_options.EnableFrameMetricsReadback)
        {
            frame.FrameMetricsReadback = DeclareExternalBuffer(
                ref graph,
                checked((ulong)FrameMetricsReadbackByteSize),
                BufferUsages.CopyDestination,
                "Cluster frame-metrics readback");
        }

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
            // Shared by the neutral target-view (80 B), page-table (16 B),
            // receiver-mask (8 B) and uint views. Keep the byte size aligned to
            // every structured interpretation.
            stackalloc byte[checked((int)ClusterRenderContext.TargetViewSizeInBytes)],
            BufferUsages.ShaderRead,
            "Cluster neutral target view");
        frame.TraversalNodes = frame.AddBuffer(
            ref graph,
            checked(candidates * TraversalNodeStride),
            BufferUsages.None,
            "Cluster breadth-first hierarchy nodes");
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
            checked(sizeof(uint) + (ulong)pageFaultCapacity * sizeof(uint) +
                ((residency.Bvh.Info.Size / 64u + 31u) / 32u) * sizeof(uint)),
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
            ClusterRasterArguments.HiZPairOffset(_history!.HiZMipCount),
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
            _winnerClearPass = default;
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
            _winnerClearPass = AddComputePass(
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
        _winnerClearPass = AddComputePass(
            ref graph,
            "Clear Cluster visibility winner buffer",
            pipeline,
            new ClusterDispatchParameters(new DispatchArguments(groupsX, groupsY, 1)),
            bindingCount,
            ClusterDispatchParameters.Record);
    }

    private void ReconcileStageResources(
        ref RenderGraphEdit graph,
        ClusterMaterialAssignmentSet assignments,
        ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages)
    {
        FrameResources frame = _frameResources;
        uint rasterBinCount = rasterStages.BinCount;
        uint tessellationBinCount = checked(rasterBinCount * 8u);
        uint shadeBinCount = shadeStages.BinCount;

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
        _frameResources = frame;
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
        FrameResources frame = _frameResources;
        if (_instanceDataTransfer is null)
            graph.BindExternalBuffer(frame.InstanceData, binding.PropertyData, binding.PropertyDataEndpoints);
        graph.BindExternalBuffer(
            frame.InstanceProperties,
            binding.InstancePropertyMetadata,
            binding.InstancePropertyMetadataEndpoints);
        graph.SetBufferRange(
            frame.InstanceProperties,
            binding.InstancePropertyMetadataRange);

        int generation = RequireReadbackWriteGeneration();
        BindReadbackBuffer(ref graph, frame.PageFaultReadback,
            _pageFaultReadbacks[generation] ?? throw new InvalidOperationException("Cluster page-fault readback was not created."),
            ref _pageFaultReadbackStates[generation]);
        if (_options.EnableFrameMetricsReadback)
            BindReadbackBuffer(ref graph, frame.FrameMetricsReadback,
                _frameMetricReadbacks[generation] ?? throw new InvalidOperationException("Cluster frame-metrics readback was not created."),
                ref _frameMetricReadbackStates[generation]);

        graph.BindExternalTexture(
            frame.PreviousHiZ,
            history.PreviousHiZ,
            history.PreviousHiZEndpoints);
        graph.BindExternalTexture(
            frame.CurrentHiZ,
            history.CurrentHiZ,
            history.CurrentHiZEndpoints);
    }

    private void RecordResidencyUploads(
        ref RenderGraphFrame graph,
        in ClusterRenderBinding binding)
    {
        if (binding.Publication is not { } publication)
            return;

        ClusterMeshPublication meshes = publication.Meshes;
        // Streaming bytes are a frame-local workload; the geometry readers stay
        // in the retained graph. One copy pass per destination holds this frame's
        // ranges instead of creating and ordering a separate pass for every page.
        GraphPassId pagePass = AddUploads(ref graph, meshes.PageUploads,
            _frameResources.PageHeap, "Upload Cluster pages");
        GraphPassId previousBvhPass = AddUploads(ref graph, meshes.Bvh.Uploads,
            _frameResources.Bvh, "Patch Cluster BVH");
        if (pagePass != default) _residencyUploadPasses.Add(pagePass);
        if (previousBvhPass != default)
        {
            _residencyUploadPasses.Add(previousBvhPass);
            if (pagePass != default) graph.OrderAfter(previousBvhPass, pagePass);
        }
        if (previousBvhPass != default)
        {
            graph.OrderAfter(_sceneBoundsPass, previousBvhPass);
            if (_traversalSeedPass != default)
                graph.OrderAfter(_traversalSeedPass, previousBvhPass);
            if (_continuationTraversalSeedPass != default)
                graph.OrderAfter(_continuationTraversalSeedPass, previousBvhPass);
            foreach (GraphPassId traversalLevel in _traversalLevelPasses)
                graph.OrderAfter(traversalLevel, previousBvhPass);
            foreach (GraphPassId traversalLevel in _primaryPostTraversalLevelPasses)
                graph.OrderAfter(traversalLevel, previousBvhPass);
            foreach (GraphPassId traversalLevel in _continuationTraversalLevelPasses)
                graph.OrderAfter(traversalLevel, previousBvhPass);
            foreach (GraphPassId traversalLevel in _continuationPostTraversalLevelPasses)
                graph.OrderAfter(traversalLevel, previousBvhPass);
        }

        static GraphPassId AddUploads(
            ref RenderGraphFrame activeGraph,
            ReadOnlySpan<ClusterStagedUpload> uploads,
            GraphBufferId destination,
            string label)
        {
            if (uploads.IsEmpty) return default;
            const ulong copyBatchBytes = 4UL * 1024 * 1024;
            var copies = new ClusterBufferCopyParameters[uploads.Length];
            var segments = new ReadOnlyMemory<byte>[uploads.Length];
            int copyCount = 0;
            for (int first = 0; first < uploads.Length;)
            {
                ulong offset = uploads[first].Offset;
                ulong byteCount = 0;
                int end = first;
                do
                {
                    ClusterStagedUpload upload = uploads[end];
                    segments[end - first] = upload.Bytes;
                    byteCount = checked(byteCount + (ulong)upload.Bytes.Length);
                    end++;
                } while (end < uploads.Length && uploads[end].Offset == offset + byteCount &&
                    byteCount + (ulong)uploads[end].Bytes.Length <= copyBatchBytes);
                GraphBufferSlice source = activeGraph.Upload(segments.AsSpan(0, end - first), BufferUsages.CopySource, label);
                copies[copyCount++] = new(source.Buffer, source.Range.Offset, destination, offset, byteCount);
                first = end;
            }
            Array.Resize(ref copies, copyCount);
            return activeGraph.AddCopyPass(
                label,
                PassQueueSelection.AnyOfType(QueueType.Copy),
                copies,
                default,
                static (ref PassDefinition access, ref ClusterBufferCopyParameters[] values) =>
                {
                    foreach (ClusterBufferCopyParameters copy in values)
                    {
                        access.Read(copy.Source, new BufferRange(copy.SourceOffset, copy.ByteCount),
                            PipelineSync.Copy, ResourceAccess.CopySource);
                        access.Write(copy.Destination, new BufferRange(copy.DestinationOffset, copy.ByteCount),
                            PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    }
                },
                static (ref CopyPassCommandScope commands, in ClusterBufferCopyParameters[] values) =>
                {
                    foreach (ClusterBufferCopyParameters copy in values)
                        ClusterBufferCopyParameters.Record(ref commands, copy);
                });
        }
    }

    private void DeclareResidencyReads(
        ref RenderGraphFrame graph,
        in ClusterRenderBinding binding)
    {
        foreach (GraphPassId passId in _pageHeapReaderPasses)
        {
            PassDefinition pass = graph.GetPass(passId);
            pass.ReadRanges(_frameResources.PageHeap, _residentPageReads,
                PipelineSync.AllShading, ResourceAccess.ShaderResource);
        }

        BufferRange bvhRange = binding.BvhRange;
        if (!bvhRange.IsWhole && bvhRange.Size != 0)
        {
            PassDefinition sceneBounds = graph.GetPass(_sceneBoundsPass);
            _ = sceneBounds.Read(_frameResources.Bvh, bvhRange, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
            if (_traversalSeedPass != default)
            {
                PassDefinition seed = graph.GetPass(_traversalSeedPass);
                _ = seed.Read(
                    _frameResources.Bvh,
                    bvhRange,
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource);
            }
            foreach (GraphPassId traversalLevel in _traversalLevelPasses)
            {
                PassDefinition traversal = graph.GetPass(traversalLevel);
                _ = traversal.Read(
                    _frameResources.Bvh,
                    bvhRange,
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource);
            }
            foreach (GraphPassId traversalLevel in _primaryPostTraversalLevelPasses)
            {
                PassDefinition traversal = graph.GetPass(traversalLevel);
                _ = traversal.Read(
                    _frameResources.Bvh,
                    bvhRange,
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource);
            }
            if (_continuationTraversalSeedPass != default)
            {
                PassDefinition seed = graph.GetPass(_continuationTraversalSeedPass);
                _ = seed.Read(
                    _frameResources.Bvh,
                    bvhRange,
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource);
            }
            foreach (GraphPassId traversalLevel in _continuationTraversalLevelPasses)
            {
                PassDefinition traversal = graph.GetPass(traversalLevel);
                _ = traversal.Read(
                    _frameResources.Bvh,
                    bvhRange,
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource);
            }
            foreach (GraphPassId traversalLevel in _continuationPostTraversalLevelPasses)
            {
                PassDefinition traversal = graph.GetPass(traversalLevel);
                _ = traversal.Read(
                    _frameResources.Bvh,
                    bvhRange,
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource);
            }
        }
    }

    private QueueCompletion[][] CaptureRetirementCompletions(
        ClusterResidencyPublication publication)
    {
        PageRetirement[] retirements = publication.Meshes.Retirements;
        var result = new QueueCompletion[retirements.Length][];
        Buffer pageHeap = _resources.Residency.GetBinding().PageHeap;
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

    private GraphBufferInput _primaryTraversalSeedUniforms = null!;
    private GraphBufferInput _continuationTraversalSeedUniforms = null!;

    private void WriteFrameUploads(
        ref RenderGraphFrame graph, int generation, ClusterRasterStageSet rasterStages,
        ClusterShadeStageSet shadeStages, scoped ReadOnlySpan<ClusterViewUniforms> packedViews,
        int primaryViewCount)
    {
        ClusterFrameUploads uploads = _uploads ?? throw new InvalidOperationException("Cluster uploads are unavailable.");
        uploads.BeginFrame(generation);
        try
        {
            graph.SetBufferData(_frameResources.PackedViews, MemoryMarshal.AsBytes(packedViews));
            WriteUniform(ref graph, uploads, generation, _sceneValues, _sceneUniforms);
            WriteUniform(ref graph, uploads, generation, new ClusterTraversalSeedUniforms
            {
                ViewCount = checked((uint)primaryViewCount), ViewIndexMode = 0u,
                MaxNodes = _options.MaxCandidates,
                UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
            }, _primaryTraversalSeedUniforms);
            if (_renderContext.IsValid)
                WriteUniform(ref graph, uploads, generation, new ClusterTraversalSeedUniforms
                {
                    ViewCount = _renderContext.MaximumInvocationCount, ViewIndexMode = 1u,
                    MaxNodes = _options.MaxCandidates,
                    UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
                }, _continuationTraversalSeedUniforms);
            uploads.CompleteFrame(ref graph);
        }
        catch
        {
            uploads.CancelFrame();
            throw;
        }
    }

    private static void WriteUniform<T>(
        ref RenderGraphFrame graph,
        ClusterFrameUploads uploads,
        int generation,
        in T value,
        GraphBufferInput id)
        where T : unmanaged
    {
        int valueSize = Unsafe.SizeOf<T>();
        Span<byte> bytes = stackalloc byte[valueSize];
        MemoryMarshal.Write(bytes, in value);
        uploads.WriteUniformAndBind(ref graph, generation, bytes, id);
    }

    private static void BindReadbackBuffer(ref RenderGraphFrame graph, GraphBufferId id,
        Buffer buffer, ref BufferBoundaryState[] states)
    {
        if (states.Length == 0)
            states = [new BufferBoundaryState(new BufferRange(0, buffer.Info.Size),
                buffer.InitialSync, buffer.InitialAccess, ResourceContentState.Undefined)];
        graph.BindExternalBuffer(id, buffer, states);
    }

    private static void RecordBufferInitialization(
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
            ClusterBufferCopyParameters.Record);

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
            ClusterBufferCopyParameters.Record);

        static void AddSmallClear(
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
                ClusterBufferRangeClearParameters.Record);
        }
    }

    private static void RecordTargetInitialization(
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
            ClusterTargetClearParameters.Record);

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
                ClusterColorAttachmentClearParameters.Record);
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
                ClusterPolicyTargetClearParameters.Record);
        }
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
                    frame.ViewCount,
                    TextureAspects.Color),
                dimension: TextureViewDimension.Texture2DArray,
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

    private GraphBufferInput UploadUniform<T>(
        ref RenderGraphEdit graph,
        T value,
        string name)
        where T : unmanaged
    {
        int valueSize = Unsafe.SizeOf<T>();
        Span<byte> bytes = stackalloc byte[valueSize];
        MemoryMarshal.Write(bytes, in value);
        GraphBufferInput input = _uploads!.DeclareUniform(ref graph, bytes, name);
        _rasterGroup?.Uniforms.Add(input);
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

    private GraphBufferId UploadBytes(ref RenderGraphEdit graph, ClusterUploadData bytes,
        BufferUsages usages, string label)
    {
        GraphBufferId buffer = _uploads!.Declare(ref graph, bytes, usages, label);
        _rasterGroup?.Uploads.Add(buffer);
        return buffer;
    }

    private GraphBufferId UploadBytes(
        ref RenderGraphEdit graph,
        scoped ReadOnlySpan<byte> bytes,
        BufferUsages usage,
        string name)
    {
        GraphBufferId buffer = (_uploads ?? throw new InvalidOperationException(
            "Cluster retained upload storage was not created."))
            .Declare(ref graph, bytes, usage, name);
        _rasterGroup?.Uploads.Add(buffer);
        return buffer;
    }

    private static GraphBufferId DeclareExternalBuffer(
        ref RenderGraphEdit graph,
        ulong size,
        BufferUsages usages,
        string label) =>
        graph.DeclareExternalBuffer(new BufferDesc(size, usages, label));

    private struct FrameResources
    {
        public FrameResources() { }
        internal int Width;
        internal int Height;
        internal uint ViewCount;
        internal uint RasterBinCount;
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
        internal GraphBufferId PackedViews;
        internal GraphBufferId RasterSlots;
        internal GraphBufferId ShadeSlots;
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
        internal GraphBufferId TraversalNodes;
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
