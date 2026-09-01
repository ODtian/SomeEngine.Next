using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Assets.Schema.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly Dictionary<SamplerDesc, Sampler> _materialSamplers = [];

    private void BindDeformStages(
        ref RenderGraphFrame graph,
        ClusterDeformStageSet deformStages)
    {
        for (int index = 0; index < _deformPassBindings.Count; index++)
        {
            ClusterDeformPassBinding passBinding = _deformPassBindings[index];
            ClusterDeformInvocation invocation =
                deformStages.Invocations[checked((int)passBinding.Invocation)];
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            pass.SetParameterBlock(invocation.Pipeline.ParameterLayout);

            ReadOnlySpan<ClusterLinkedStageResource> resources = invocation.Resources.Resources;
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
                if (resource.Element != 0)
                {
                    throw new NotSupportedException(
                        $"Cluster deform resource arrays are not supported: " +
                        $"'{resource.Name}[{resource.Element}]'.");
                }

                ClusterDeformCoreResource coreResource = invocation.CoreResources[resourceIndex];
                if (coreResource != ClusterDeformCoreResource.None)
                {
                    BindDeformCoreResource(
                        ref graph,
                        ref pass,
                        coreResource,
                        passBinding.Uniforms);
                    continue;
                }

                if (invocation.FeatureResourceBinders[resourceIndex] is { } featureBinder)
                {
                    featureBinder(ref graph, ref pass);
                    continue;
                }

                ClusterMaterialStageBinding materialBinding = invocation.MaterialBindings[resourceIndex];
                if (!materialBinding.IsMaterialResource)
                {
                    throw new InvalidDataException(
                        $"Final deform entry requires unknown linked resource '{resource.Name}'.");
                }
                BindStageMaterialResource(
                    ref graph,
                    ref pass,
                    invocation.Material,
                    resource,
                    materialBinding,
                    PipelineSync.ComputeShading);
            }
        }
    }

    private void BindRasterStages(
        ref RenderGraphFrame graph,
        ClusterRasterStageSet rasterStages)
    {
        for (int index = 0; index < _softwareRasterPassBindings.Count; index++)
        {
            ClusterSoftwareRasterPassBinding passBinding = _softwareRasterPassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            pass.SetParameterBlock(invocation.Software.ParameterLayout);
            BindSoftwareRasterResources(
                ref graph,
                ref pass,
                invocation,
                passBinding.Uniforms);
        }

        for (int index = 0; index < _hardwareRasterPassBindings.Count; index++)
        {
            ClusterHardwareRasterPassBinding passBinding = _hardwareRasterPassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            pass.SetParameterBlock(invocation.Hardware.ParameterLayout);
            BindHardwareRasterResources(
                ref graph,
                ref pass,
                invocation,
                passBinding.Uniforms,
                passBinding.DispatchUniforms);
        }

        for (int index = 0; index < _tessellationFactorPassBindings.Count; index++)
        {
            ClusterTessellationPassBinding passBinding = _tessellationFactorPassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            pass.SetParameterBlock(invocation.TessellationFactor.ParameterLayout);
            BindTessellationResources(
                ref graph,
                ref pass,
                invocation,
                invocation.TessellationFactorResources,
                passBinding.Uniforms,
                dicing: false);
        }

        for (int index = 0; index < _tessellationDicePassBindings.Count; index++)
        {
            ClusterTessellationPassBinding passBinding = _tessellationDicePassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            pass.SetParameterBlock(invocation.TessellationDice.ParameterLayout);
            BindTessellationResources(
                ref graph,
                ref pass,
                invocation,
                invocation.TessellationDiceResources,
                passBinding.Uniforms,
                dicing: true);
        }
    }

    private void BindTessellationResources(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        ClusterRasterStageResources<ClusterTessellationCoreResource> stage,
        GraphBufferId uniformBuffer,
        bool dicing)
    {
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Cluster tessellation resource arrays are not supported: " +
                    $"'{resource.Name}[{resource.Element}]'.");
            }
            ClusterTessellationCoreResource core = stage.CoreResources[resourceIndex];
            if (core != ClusterTessellationCoreResource.None)
            {
                BindTessellationCoreResource(
                    ref graph,
                    ref pass,
                    core,
                    uniformBuffer,
                    dicing);
                continue;
            }
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                featureBinder(ref graph, ref pass);
                continue;
            }
            ClusterMaterialStageBinding materialBinding = stage.MaterialBindings[resourceIndex];
            if (!materialBinding.IsMaterialResource)
            {
                throw new InvalidDataException(
                    $"Final tessellation entry requires unknown linked resource '{resource.Name}'.");
            }
            BindStageMaterialResource(
                ref graph,
                ref pass,
                invocation.Material,
                resource,
                materialBinding,
                PipelineSync.ComputeShading);
        }
    }

    private void BindTessellationCoreResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterTessellationCoreResource resource,
        GraphBufferId uniformBuffer,
        bool dicing)
    {
        FrameResources frame = _frameResources;
        switch (resource)
        {
            case ClusterTessellationCoreResource.InstanceData:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.InstanceData, label: "Cluster tessellation instance data"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.InstanceProperties:
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange,
                        "Cluster tessellation instance properties"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.Uniforms:
                _ = pass.Bind(
                    graph.CreateBufferCbv(uniformBuffer, label: "Cluster tessellation uniforms"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.Views:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.PackedViews,
                        structureStride: ClusterViewUniforms.SizeInBytes,
                        label: "Cluster tessellation views"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.PageHeap:
                BindPageHeapDescriptor(
                    ref graph,
                    ref pass,
                    "Cluster tessellation PageHeap",
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.VisibleClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusters,
                        structureStride: VisibleClusterStride,
                        label: "Cluster tessellation visible clusters"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.BinnedClusters:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.BinnedClusters,
                        structureStride: BinnedClusterStride,
                        label: "Cluster tessellation binned clusters"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.BinMetadata:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.RasterBinMeta,
                        structureStride: RasterBinStride,
                        label: "Cluster tessellation bin metadata"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.DeformCache:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.DeformCache, label: "Cluster tessellation deform cache"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.CacheOffsets:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.CacheOffsets,
                        structureStride: sizeof(uint),
                        label: "Cluster tessellation cache offsets"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterTessellationCoreResource.Counters:
                _ = pass.Bind(
                    graph.CreateBufferUav(frame.TessellationCounters, label: "Cluster tessellation counters"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.SplitWork:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationSplitWork,
                        structureStride: TessellationWorkStride,
                        label: "Cluster tessellation split work"),
                    PipelineSync.ComputeShading,
                    dicing ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.DiceArguments:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationDiceDispatchArgs,
                        label: "Cluster tessellation dice arguments"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.DicedTriangles:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.DicedTriangles,
                        structureStride: DicedTriangleStride,
                        label: "Cluster tessellation diced triangles"),
                    PipelineSync.ComputeShading,
                    dicing ? GraphAccessMode.Write : GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.RasterArguments:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationRasterDispatchArgs,
                        label: "Cluster tessellation raster arguments"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown Cluster tessellation core resource {resource}.");
        }
    }

    private void BindSoftwareRasterResources(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        GraphBufferId uniformBuffer)
    {
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource> stage =
            invocation.SoftwareResources;
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Cluster software-raster resource arrays are not supported: " +
                    $"'{resource.Name}[{resource.Element}]'.");
            }
            ClusterSoftwareRasterCoreResource core = stage.CoreResources[resourceIndex];
            if (core != ClusterSoftwareRasterCoreResource.None)
            {
                BindSoftwareRasterCoreResource(ref graph, ref pass, core, uniformBuffer);
                continue;
            }
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                featureBinder(ref graph, ref pass);
                continue;
            }
            ClusterMaterialStageBinding materialBinding = stage.MaterialBindings[resourceIndex];
            if (!materialBinding.IsMaterialResource)
            {
                throw new InvalidDataException(
                    $"Final software-raster entry requires unknown linked resource '{resource.Name}'.");
            }
            BindStageMaterialResource(
                ref graph,
                ref pass,
                invocation.Material,
                resource,
                materialBinding,
                PipelineSync.ComputeShading);
        }
    }

    private void BindHardwareRasterResources(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        GraphBufferId uniformBuffer,
        GraphBufferId dispatchUniformBuffer)
    {
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> stage =
            invocation.HardwareResources;
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Cluster hardware-raster resource arrays are not supported: " +
                    $"'{resource.Name}[{resource.Element}]'.");
            }
            ClusterHardwareRasterCoreResource core = stage.CoreResources[resourceIndex];
            if (core != ClusterHardwareRasterCoreResource.None)
            {
                BindHardwareRasterCoreResource(
                    ref graph,
                    ref pass,
                    core,
                    uniformBuffer,
                    dispatchUniformBuffer);
                continue;
            }
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                featureBinder(ref graph, ref pass);
                continue;
            }
            ClusterMaterialStageBinding materialBinding = stage.MaterialBindings[resourceIndex];
            if (!materialBinding.IsMaterialResource)
            {
                throw new InvalidDataException(
                    $"Final hardware-raster entry requires unknown linked resource '{resource.Name}'.");
            }
            BindStageMaterialResource(
                ref graph,
                ref pass,
                invocation.Material,
                resource,
                materialBinding,
                PipelineSync.AllShading);
        }
    }

    private void BindSoftwareRasterCoreResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterSoftwareRasterCoreResource resource,
        GraphBufferId uniformBuffer)
    {
        FrameResources frame = _frameResources;
        switch (resource)
        {
            case ClusterSoftwareRasterCoreResource.InstanceData:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.InstanceData, label: "Cluster software raster instance data"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.InstanceProperties:
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange,
                        "Cluster software raster instance properties"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Uniforms:
                _ = pass.Bind(
                    graph.CreateBufferCbv(uniformBuffer, label: "Cluster software raster uniforms"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Views:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.PackedViews,
                        structureStride: ClusterViewUniforms.SizeInBytes,
                        label: "Cluster software raster views"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.PageHeap:
                BindPageHeapDescriptor(ref graph, ref pass, "Cluster software raster PageHeap", PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.VisibleClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusters,
                        structureStride: VisibleClusterStride,
                        label: "Cluster software raster visible clusters"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.BinnedClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.BinnedClusters,
                        structureStride: BinnedClusterStride,
                        label: "Cluster software raster binned clusters"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.BinMetadata:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.RasterBinMeta,
                        structureStride: RasterBinStride,
                        label: "Cluster software raster bin metadata"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Depth:
                _ = pass.Bind(
                    graph.CreateTextureSrv(
                        frame.Depth,
                        new TextureSubresourceRange(
                            0,
                            1,
                            0,
                            frame.ViewCapacity,
                            TextureAspects.Depth),
                        Format.D32Float,
                        SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        "Cluster software raster depth"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Visibility:
                _ = pass.Bind(
                    graph.CreateTextureUav(
                        frame.VisBuffer,
                        format: Format.R32UInt,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster software raster visibility"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterSoftwareRasterCoreResource.SoftwareDepth:
                _ = pass.Bind(
                    graph.CreateTextureUav(
                        frame.SoftwareDepth,
                        format: Format.R32UInt,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster software raster depth output"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterSoftwareRasterCoreResource.DeformCache:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.DeformCache, label: "Cluster software raster deform cache"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.CacheOffsets:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.CacheOffsets,
                        structureStride: sizeof(uint),
                        label: "Cluster software raster cache offsets"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Diagnostics:
                _ = pass.Bind(
                    graph.CreateBufferUav(frame.SoftwareDebug, label: "Cluster software raster diagnostics"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown Cluster software-raster core resource {resource}.");
        }
    }

    private void BindHardwareRasterCoreResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterHardwareRasterCoreResource resource,
        GraphBufferId uniformBuffer,
        GraphBufferId dispatchUniformBuffer)
    {
        FrameResources frame = _frameResources;
        switch (resource)
        {
            case ClusterHardwareRasterCoreResource.InstanceData:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.InstanceData, label: "Cluster hardware raster instance data"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.InstanceProperties:
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange,
                        "Cluster hardware raster instance properties"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.Uniforms:
                _ = pass.Bind(
                    graph.CreateBufferCbv(uniformBuffer, label: "Cluster hardware raster uniforms"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.Views:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.PackedViews,
                        structureStride: ClusterViewUniforms.SizeInBytes,
                        label: "Cluster hardware raster views"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.DispatchUniforms:
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        dispatchUniformBuffer,
                        label: "Cluster hardware raster dispatch uniforms"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.PageHeap:
                BindPageHeapDescriptor(ref graph, ref pass, "Cluster hardware raster PageHeap", PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.BinnedClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.BinnedClusters,
                        structureStride: BinnedClusterStride,
                        label: "Cluster hardware raster binned clusters"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.VisibleClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusters,
                        structureStride: VisibleClusterStride,
                        label: "Cluster hardware raster visible clusters"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.DrawMetadata:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.BinnedHardwareDrawArgs,
                        label: "Cluster hardware raster draw metadata"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.DeformCache:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.DeformCache, label: "Cluster hardware raster deform cache"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.CacheOffsets:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.CacheOffsets,
                        structureStride: sizeof(uint),
                        label: "Cluster hardware raster cache offsets"),
                    PipelineSync.AllShading);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown Cluster hardware-raster core resource {resource}.");
        }
    }

    private void BindPageHeapDescriptor(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        string label,
        PipelineSync sync)
    {
        FrameResources frame = _frameResources;
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(frame.PageHeap, label: label)));
        foreach (ref readonly BufferRange range in _pendingResidentPageRanges.AsSpan())
        {
            _ = pass.Read(
                frame.PageHeap,
                range,
                sync,
                ResourceAccess.ShaderResource);
        }
    }

    private void BindDeformCoreResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterDeformCoreResource resource,
        GraphBufferId uniformBuffer)
    {
        FrameResources frame = _frameResources;
        switch (resource)
        {
            case ClusterDeformCoreResource.InstanceData:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.InstanceData, label: "Cluster deform instance data"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterDeformCoreResource.InstanceProperties:
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange,
                        "Cluster deform instance properties"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterDeformCoreResource.Uniforms:
                _ = pass.Bind(
                    graph.CreateBufferCbv(uniformBuffer, label: "Cluster deform uniforms"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterDeformCoreResource.PageHeap:
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
                    graph.CreateBufferSrv(frame.PageHeap, label: "Cluster deform PageHeap")));
                foreach (ref readonly BufferRange range in _pendingResidentPageRanges.AsSpan())
                {
                    _ = pass.Read(
                        frame.PageHeap,
                        range,
                        PipelineSync.ComputeShading,
                        ResourceAccess.ShaderResource);
                }
                return;
            case ClusterDeformCoreResource.VisibleClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusters,
                        structureStride: VisibleClusterStride,
                        label: "Cluster deform visible clusters"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterDeformCoreResource.BinnedClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.DeformBinnedClusters,
                        structureStride: BinnedClusterStride,
                        label: "Cluster deform binned clusters"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterDeformCoreResource.BinMetadata:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.DeformBinMeta,
                        structureStride: DeformBinStride,
                        label: "Cluster deform bin metadata"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterDeformCoreResource.DeformCache:
                _ = pass.Bind(
                    graph.CreateBufferUav(frame.DeformCache, label: "Cluster deform cache"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterDeformCoreResource.CacheOffsets:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.CacheOffsets,
                        structureStride: sizeof(uint),
                        label: "Cluster deform cache offsets"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterDeformCoreResource.CacheRequests:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.DeformCacheRequests,
                        structureStride: 2 * sizeof(uint),
                        label: "Cluster deform cache requests"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            default:
                throw new InvalidOperationException($"Unknown Cluster deform core resource {resource}.");
        }
    }

    private void BindShadeStages(
        ref RenderGraphFrame graph,
        ClusterShadeStageSet shadeStages)
    {
        if (_shadePasses.Length != shadeStages.Invocations.Count)
            throw new InvalidOperationException("The retained shade passes do not match the installed invocations.");

        (uint[] scalarOffsets, byte[] scalarRegion) = shadeStages.EncodeScalars();
        GraphBufferId scalarOffsetBuffer = graph.Upload(
            MemoryMarshal.AsBytes(scalarOffsets.AsSpan()),
            BufferUsages.ShaderRead,
            "Cluster shade material scalar offsets");
        GraphBufferId scalarBuffer = graph.Upload(
            scalarRegion,
            BufferUsages.ShaderRead,
            "Cluster shade material scalar region");

        for (int index = 0; index < shadeStages.Invocations.Count; index++)
        {
            ClusterShadeInvocation invocation = shadeStages.Invocations[index];
            GraphPassId passId = _shadePasses[index];
            PassDefinition pass = graph.GetPass(passId);
            pass.SetParameterBlock(invocation.Pipeline.ParameterLayout);

            var uniforms = new ClusterShadeUniforms
            {
                ScreenWidth = checked((uint)_frameResources.Width),
                ScreenHeight = checked((uint)_frameResources.Height),
                ShadingBin = invocation.BinIndex,
                ShadeBinCount = shadeStages.BinCount,
                MaterialSlotCapacity = checked((uint)shadeStages.Slots.Length),
            };
            int uniformBytes = checked((Unsafe.SizeOf<ClusterShadeUniforms>() + 255) & ~255);
            var uniformBytesStorage = new byte[uniformBytes];
            Span<byte> uniformPacket = uniformBytesStorage;
            uniformPacket.Clear();
            MemoryMarshal.Write(uniformPacket, in uniforms);
            GraphBufferId uniformBuffer = graph.Upload(
                uniformPacket,
                BufferUsages.Constant,
                $"Cluster shade bin {invocation.BinIndex} uniforms");

            ReadOnlySpan<ClusterLinkedStageResource> resources = invocation.Resources.Resources;
            for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
                if (resource.Element != 0)
                {
                    throw new NotSupportedException(
                        $"Cluster material resource arrays are not supported: '{resource.Name}[{resource.Element}]'.");
                }
                ClusterShadeCoreResource coreResource = invocation.CoreResources[resourceIndex];
                if (coreResource != ClusterShadeCoreResource.None)
                {
                    BindShadeCoreResource(
                        ref graph,
                        ref pass,
                        coreResource,
                        uniformBuffer,
                        scalarBuffer,
                        scalarOffsetBuffer);
                    continue;
                }
                if (invocation.FeatureResourceBinders[resourceIndex] is { } featureBinder)
                {
                    featureBinder(ref graph, ref pass);
                    continue;
                }
                ClusterMaterialStageBinding materialBinding = invocation.MaterialBindings[resourceIndex];
                if (!materialBinding.IsMaterialResource)
                {
                    throw new InvalidDataException(
                        $"Final shade entry requires unknown linked resource '{resource.Name}'.");
                }
                BindStageMaterialResource(
                    ref graph,
                    ref pass,
                    invocation.Material,
                    resource,
                    materialBinding,
                    PipelineSync.ComputeShading);
            }
        }
    }

    private void BindShadeCoreResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterShadeCoreResource resource,
        GraphBufferId uniformBuffer,
        GraphBufferId scalarBuffer,
        GraphBufferId scalarOffsetBuffer)
    {
        FrameResources frame = _frameResources;
        switch (resource)
        {
            case ClusterShadeCoreResource.InstanceData:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.InstanceData, label: "Cluster shade instance data"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.InstanceProperties:
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange,
                        "Cluster shade instance properties"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.Uniforms:
                _ = pass.Bind(
                    graph.CreateBufferCbv(uniformBuffer, label: "Cluster shade uniforms"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.Views:
                BindReadOnly(
                    ref graph,
                    ref pass,
                    frame.PackedViews,
                    ClusterViewUniforms.SizeInBytes,
                    "Views");
                return;
            case ClusterShadeCoreResource.PixelCoordinates:
                BindReadOnly(
                    ref graph,
                    ref pass,
                    frame.PixelCoordinates,
                    2 * sizeof(uint),
                    "PixelCoordBuffer");
                return;
            case ClusterShadeCoreResource.BinOffsets:
                BindReadOnly(ref graph, ref pass, frame.ShadeBinOffsets, sizeof(uint), "BinOffsets");
                return;
            case ClusterShadeCoreResource.BinCounts:
                BindReadOnly(ref graph, ref pass, frame.ShadeBinCounts, sizeof(uint), "BinCounts");
                return;
            case ClusterShadeCoreResource.Visibility:
                _ = pass.Bind(
                    graph.CreateTextureSrv(
                        frame.VisBuffer,
                        format: Format.R32UInt,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster shade visibility"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.Depth:
                _ = pass.Bind(
                    graph.CreateTextureSrv(
                        frame.Depth,
                        new TextureSubresourceRange(
                            0,
                            1,
                            0,
                            frame.ViewCapacity,
                            TextureAspects.Depth),
                        Format.D32Float,
                        SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster shade depth"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.VisibleClusters:
                BindReadOnly(ref graph, ref pass, frame.VisibleClusters, VisibleClusterStride, "VisibleClusters");
                return;
            case ClusterShadeCoreResource.PageHeap:
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
                    graph.CreateBufferSrv(
                        frame.PageHeap,
                        label: "Cluster shade PageHeap")));
                foreach (ref readonly BufferRange range in _pendingResidentPageRanges.AsSpan())
                {
                    _ = pass.Read(
                        frame.PageHeap,
                        range,
                        PipelineSync.ComputeShading,
                        ResourceAccess.ShaderResource);
                }
                return;
            case ClusterShadeCoreResource.OutputColor:
                _ = pass.Bind(
                    graph.CreateTextureUav(
                        frame.SceneColor,
                        format: Format.R16G16B16A16Float,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster material shade output"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.Write,
                    WriteCoverage.Partial);
                return;
            case ClusterShadeCoreResource.DeformCache:
                BindReadOnly(ref graph, ref pass, frame.DeformCache, 0, "DeformCache");
                return;
            case ClusterShadeCoreResource.CacheOffsets:
                BindReadOnly(ref graph, ref pass, frame.CacheOffsets, sizeof(uint), "CacheOffsets");
                return;
            case ClusterShadeCoreResource.MaterialScalars:
                BindReadOnly(ref graph, ref pass, scalarBuffer, 0, "MaterialScalarRegion");
                return;
            case ClusterShadeCoreResource.MaterialScalarOffsets:
                BindReadOnly(ref graph, ref pass, scalarOffsetBuffer, sizeof(uint), "MaterialScalarOffsets");
                return;
            default:
                throw new InvalidOperationException($"Unknown Cluster shade core resource {resource}.");
        }

        void BindReadOnly(
            ref RenderGraphFrame activeGraph,
            ref PassDefinition activePass,
            GraphBufferId buffer,
            uint stride,
            string name)
        {
            _ = activePass.Bind(
                activeGraph.CreateBufferSrv(
                    buffer,
                    structureStride: stride,
                    label: $"Cluster shade {name}"),
                PipelineSync.ComputeShading);
        }
    }

    private void BindStageMaterialResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        in ClusterLinkedStageResource resource,
        in ClusterMaterialStageBinding binding,
        PipelineSync sync)
    {
        switch (binding.Type)
        {
            case ResourceBindingType.TextureSrv:
                BindMaterialTextureSrv(ref graph, ref pass, material, binding.MaterialSlot, resource.Name, sync);
                break;
            case ResourceBindingType.TextureUav:
                BindMaterialTextureUav(ref graph, ref pass, material, binding.MaterialSlot, resource.Name, sync);
                break;
            case ResourceBindingType.Sampler:
                pass.Bind(ResolveMaterialSampler(material, binding.MaterialSlot, resource.Name));
                break;
            case ResourceBindingType.BufferSrv:
                BindMaterialBufferSrv(ref graph, ref pass, material, binding.MaterialSlot, resource.Name, sync);
                break;
            case ResourceBindingType.BufferUav:
                BindMaterialBufferUav(ref graph, ref pass, material, binding.MaterialSlot, resource.Name, sync);
                break;
            case ResourceBindingType.ConstantBuffer:
                BindMaterialConstantBuffer(ref graph, ref pass, material, binding.MaterialSlot, resource.Name, sync);
                break;
            case ResourceBindingType.AccelerationStructure:
                BindMaterialAccelerationStructure(ref graph, ref pass, material, binding.MaterialSlot, resource.Name, sync);
                break;
            default:
                throw new InvalidDataException(
                    $"Cluster material stage resource '{resource.Name}' has unsupported type {binding.Type}.");
        }
    }

    private void BindMaterialTextureSrv(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        uint materialSlot,
        string name,
        PipelineSync sync)
    {
        object? value = material.GetSlotValue(materialSlot);
        EngineTexture? asset = value switch
        {
            null => null,
            EngineTexture assetTexture => assetTexture,
            _ => throw new InvalidDataException(
                $"Material '{material.Name}' slot '{name}' is not a Texture."),
        };

        RhiTexture textureResource;
        TextureSrv textureView;
        if (asset is null)
        {
            textureResource = _renderDevice.PrepareMissingTexture(ref graph, pass.Id);
            textureView = _renderDevice.MissingTextureSrv;
        }
        else
        {
            textureResource = _renderDevice.PrepareTexture(ref graph, pass.Id, asset);
            textureView = _renderDevice.GetTextureSrv(asset);
        }

        TextureSrvDesc description = textureView.Description;
        GraphTextureId graphTexture = graph.GetImported(textureResource);
        GraphTextureSrvId view = graph.CreateTextureSrv(
            graphTexture,
            description.Range,
            description.Format,
            description.Dimension,
            $"Cluster material {name}");
        _ = pass.Bind(view, sync);
    }

    private void BindMaterialTextureUav(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        uint materialSlot,
        string name,
        PipelineSync sync)
    {
        object? value = material.GetSlotValue(materialSlot);
        EngineTexture asset = value as EngineTexture ?? throw new InvalidDataException(
            $"Material '{material.Name}' slot '{name}' must contain a Texture for writable access.");
        RhiTexture textureResource = _renderDevice.PrepareTexture(ref graph, pass.Id, asset);
        TextureInfo info = textureResource.Info;
        var range = new TextureSubresourceRange(
            0,
            1,
            0,
            info.ArrayLayerCount,
            TextureAspects.Color);
        TextureUav textureView = _renderDevice.GetTextureUav(
            asset,
            range,
            label: $"Cluster material {name}");
        TextureUavDesc description = textureView.Description;
        GraphTextureId graphTexture = graph.GetImported(textureResource);
        GraphTextureUavId view = graph.CreateTextureUav(
            graphTexture,
            description.Range,
            description.Format,
            description.Dimension,
            $"Cluster material {name}");
        _ = pass.Bind(
            view,
            sync,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial);
    }

    private void BindMaterialBufferSrv(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        uint materialSlot,
        string name,
        PipelineSync sync)
    {
        BufferSrv view = RequireMaterialValue<BufferSrv>(material, materialSlot, name);
        BufferSrvDesc description = view.Description;
        GraphBufferId buffer = ImportMaterialBuffer(ref graph, description.Buffer);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                buffer,
                description.Range,
                description.Format,
                description.StructureStride,
                $"Cluster material {name}"),
            sync);
    }

    private void BindMaterialBufferUav(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        uint materialSlot,
        string name,
        PipelineSync sync)
    {
        BufferUav view = RequireMaterialValue<BufferUav>(material, materialSlot, name);
        BufferUavDesc description = view.Description;
        GraphBufferId buffer = ImportMaterialBuffer(ref graph, description.Buffer);
        GraphBufferId counter = description.CounterBuffer is { } counterBuffer
            ? ImportMaterialBuffer(ref graph, counterBuffer)
            : default;
        _ = pass.Bind(
            graph.CreateBufferUav(
                buffer,
                description.Range,
                description.Format,
                description.StructureStride,
                counter,
                description.CounterOffset,
                $"Cluster material {name}"),
            sync,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial);
    }

    private void BindMaterialConstantBuffer(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        uint materialSlot,
        string name,
        PipelineSync sync)
    {
        BufferCbv view = RequireMaterialValue<BufferCbv>(material, materialSlot, name);
        BufferCbvDesc description = view.Description;
        GraphBufferId buffer = ImportMaterialBuffer(ref graph, description.Buffer);
        _ = pass.Bind(
            graph.CreateBufferCbv(buffer, description.Range, $"Cluster material {name}"),
            sync);
    }

    private void BindMaterialAccelerationStructure(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        uint materialSlot,
        string name,
        PipelineSync sync)
    {
        AccelerationStructureSrv view = RequireMaterialValue<AccelerationStructureSrv>(
            material,
            materialSlot,
            name);
        AccelerationStructure resource = view.Resource;
        GraphBufferId storage = ImportMaterialBuffer(ref graph, resource.Info.Storage);
        pass.Bind(GraphParameterResourceBinding.AccelerationStructure(
            view,
            storage,
            resource.Info.StorageRange,
            sync));
    }

    private static T RequireMaterialValue<T>(Material material, uint slot, string name)
        where T : class
    {
        object? value = material.GetSlotValue(slot);
        return value as T ?? throw new InvalidDataException(
            $"Material '{material.Name}' slot '{name}' is not a {typeof(T).Name}.");
    }

    private static GraphBufferId ImportMaterialBuffer(
        ref RenderGraphFrame graph,
        SomeEngine.Graphics.Buffer buffer)
    {
        return graph.Import(
            buffer,
            [new BufferBoundaryState(
                new BufferRange(0, buffer.Info.Size),
                buffer.InitialSync,
                buffer.InitialAccess,
                ResourceContentState.Defined)]);
    }

    private Sampler ResolveMaterialSampler(Material material, uint materialSlot, string name)
    {
        object? value = material.GetSlotValue(materialSlot);
        SamplerDesc description = value switch
        {
            SamplerDesc sampler => sampler,
            null => ClusterMaterialBindingDefaults.Sampler,
            _ => throw new InvalidDataException(
                $"Material '{material.Name}' slot '{name}' is not a SamplerDesc."),
        };
        if (_materialSamplers.TryGetValue(description, out Sampler? samplerResource))
            return samplerResource;
        samplerResource = _backend.CreateSampler(_device, description);
        _materialSamplers.Add(description, samplerResource);
        return samplerResource;
    }

    private void DisposeMaterialSamplers(ref List<Exception>? failures)
    {
        foreach (Sampler sampler in _materialSamplers.Values)
        {
            try { sampler.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _materialSamplers.Clear();
    }

}
