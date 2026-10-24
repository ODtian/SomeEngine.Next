using System.Collections.Immutable;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct MaterialBuffer(GraphBufferId Id, int References, bool RegisteredHere);
    private ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer> _materialBuffers =
        ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer>.Empty;

    private (ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer> Buffers,
        ImmutableDictionary<EngineTexture, MaterialTexture> Textures) UpdateMaterialResources(
        ref RenderGraphEdit edit, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        // The installed shader bindings already retain the graph's resource references.
        // Count only changed bins; no copy of material properties is needed for removals.
        var changes = new Dictionary<SomeEngine.Graphics.Buffer, int>();
        var resources = new HashSet<SomeEngine.Graphics.Buffer>();
        foreach (uint bin in rasterStages.ChangedBins)
        {
            if (_installedRasterStages is not null && _installedRasterStages.TryGetBin(bin, out var before))
                CountBuffers(before.CommandBindings, -1);
            if (rasterStages.TryGetBin(bin, out var after)) CountBuffers(after.CommandBindings, 1);
        }
        foreach (uint bin in shadeStages.ChangedBins)
        {
            if (_installedShadeStages is not null && _installedShadeStages.TryGetBin(bin, out var before))
                CountBuffers(before.CommandBindings, -1);
            if (shadeStages.TryGetBin(bin, out var after)) CountBuffers(after.CommandBindings, 1);
        }
        void CountBuffers(ClusterCommandBindingKey bindings, int change)
        {
            resources.Clear();
            bindings.CollectBuffers(resources);
            foreach (SomeEngine.Graphics.Buffer buffer in resources)
                changes[buffer] = changes.GetValueOrDefault(buffer) + change;
        }
        var buffers = _materialBuffers;
        foreach ((SomeEngine.Graphics.Buffer buffer, int change) in changes)
        {
            if (change == 0) continue;
            if (buffers.TryGetValue(buffer, out MaterialBuffer current))
            {
                int references = checked(current.References + change);
                if (references != 0) buffers = buffers.SetItem(buffer, current with { References = references });
                else
                {
                    if (current.RegisteredHere) edit.Remove(current.Id);
                    buffers = buffers.Remove(buffer);
                }
            }
            else
            {
                bool registeredHere = !_graph.TryGetBufferId(buffer, out GraphBufferId id);
                if (registeredHere)
                    id = edit.RegisterExternalBuffer(buffer,
                        [new BufferBoundaryState(new BufferRange(0, buffer.Info.Size), buffer.InitialSync,
                            buffer.InitialAccess, ResourceContentState.Defined)]);
                buffers = buffers.Add(buffer, new MaterialBuffer(id, change, registeredHere));
            }
        }
        return (buffers, UpdateMaterialTextures(ref edit, rasterStages, shadeStages));
    }

    private readonly Dictionary<SamplerDesc, Sampler> _materialSamplers = [];
    // Only GPU binding dependencies are remembered; scalar values stay on the Material.
    private readonly Dictionary<long, MaterialBindingVersions> _materialBindingVersions = [];
    private sealed record MaterialBindingVersions(
        Shader? Shader, ulong[] Properties)
    {
        internal bool Matches(Material material, ClusterRasterStages raster, ClusterShadeStages shade)
        {
            if (!ReferenceEquals(Shader, material.Shader))
                return false;
            for (uint property = 0; property < Properties.Length; property++)
                if (material.GetPropertyRevision(property) != Properties[property] &&
                    (raster.UsesResourceProperty(material.MaterialId, property) ||
                     shade.UsesResourceProperty(material.MaterialId, property))) return false;
            return true;
        }
    }

    private bool CollectMaterialChanges(ClusterMaterialAssignmentSet assignments)
    {
        foreach (Material material in assignments.ChangedMaterials)
            if (!_materialBindingVersions.TryGetValue(material.MaterialId, out var versions) ||
                !versions.Matches(material, _rasterStages!, _shadeStages!)) return true;
        return false;
    }

    private void AcceptMaterialChanges(ClusterMaterialAssignmentSet assignments)
    {
        foreach (long id in assignments.ChangedMaterialIds)
        {
            if (!assignments.TryGetMaterial(id, out Material material))
            {
                _materialBindingVersions.Remove(id);
                continue;
            }
            if (_materialBindingVersions.TryGetValue(id, out var versions) && versions.Matches(material, _rasterStages!, _shadeStages!))
                continue;
            var properties = new ulong[material.PropertyCount];
            for (uint property = 0; property < material.PropertyCount; property++)
                properties[property] = material.GetPropertyRevision(property);
            _materialBindingVersions[id] = new(material.Shader, properties);
        }
        _materialAssignments.AcceptMaterialChanges(assignments);
    }

    private static bool BindInactiveStageResource(
        ref PassDefinition pass, in ClusterLinkedStageResource resource)
    {
        if (resource.ParameterLayout != SlangShaderSharp.VariableLayoutReflection.Null)
        {
            pass.BindParameterBlock(resource.ParameterLayout);
            return true;
        }
        if (resource.IsUsed)
            return false;
        pass.Bind(GraphParameterResourceBinding.Null(resource.Type));
        return true;
    }

    private void BindTessellationResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        ClusterRasterStageResources<ClusterTessellationCoreResource> stage,
        GraphBufferInput uniformBuffer,
        bool phaseOne,
        uint nextLevelArgument,
        PipelineSync sync,
        bool policyTarget)
    {
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Cluster tessellation resource arrays are not supported: " +
                    $"'{resource.Name}[{resource.Element}]'.");
            }
            ClusterTessellationCoreResource core = stage.CoreResources[resourceIndex];
            if (BindDeformRead(ref graph, ref pass, resource, sync)) continue;
            if (core == ClusterTessellationCoreResource.PolicyViews)
            {
                BindPolicyClusterViews(ref graph, ref pass, sync, policyTarget);
                continue;
            }
            if (core != ClusterTessellationCoreResource.None)
            {
                BindTessellationCoreResource(
                    ref graph,
                    ref pass,
                    core,
                    uniformBuffer,
                    phaseOne,
                    nextLevelArgument,
                    sync);
                continue;
            }
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                if (!policyTarget && BindNeutralRasterTargetResource(
                        ref graph, ref pass, resource))
                {
                    continue;
                }
                featureBinder(ref graph, ref pass, sync);
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
                resource,
                materialBinding,
                sync);
        }
    }

    private void BindTessellationCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterTessellationCoreResource resource,
        GraphBufferInput uniformBuffer,
        bool phaseOne,
        uint nextLevelArgument,
        PipelineSync sync)
    {
        FrameResources frame = _frameResources;
        switch (resource)
        {
            case ClusterTessellationCoreResource.InstanceData:
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.InstanceData, label: "Cluster tessellation instance data"),
                    sync);
                return;
            case ClusterTessellationCoreResource.InstanceProperties:
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange,
                        "Cluster tessellation instance properties"),
                    sync);
                return;
            case ClusterTessellationCoreResource.Uniforms:
                _ = pass.Bind(graph.CreateBufferCbv(uniformBuffer),
                    sync);
                return;
            case ClusterTessellationCoreResource.Views:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.PackedViews,
                        structureStride: ClusterViewUniforms.SizeInBytes,
                        label: "Cluster tessellation views"),
                    sync);
                return;
            case ClusterTessellationCoreResource.PolicyViews:
                BindPolicyClusterViews(ref graph, ref pass, sync);
                return;
            case ClusterTessellationCoreResource.PageHeap:
                BindPageHeapDescriptor(
                    ref graph,
                    ref pass,
                    "Cluster tessellation PageHeap",
                    sync);
                return;
            case ClusterTessellationCoreResource.VisibleClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusters,
                        structureStride: VisibleClusterStride,
                        label: "Cluster tessellation visible clusters"),
                    sync,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.RasterWindows:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusterRasterWindows,
                        structureStride: VisibleClusterRasterWindowStride,
                        label: "Cluster tessellation raster windows"),
                    sync,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.BinnedClusters:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.BinnedClusters,
                        structureStride: BinnedClusterStride,
                        label: "Cluster tessellation binned clusters"),
                    sync,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial,
                    ResourceContentState.Defined,
                    BufferContentScope.CountBoundedRanges);
                return;
            case ClusterTessellationCoreResource.BinMetadata:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.RasterBinMeta,
                        structureStride: RasterBinStride,
                        label: "Cluster tessellation bin metadata"),
                    sync);
                return;
            case ClusterTessellationCoreResource.RasterSlots:
                _ = pass.Bind(graph.CreateBufferSrv(frame.RasterSlots, structureStride: sizeof(uint),
                    label: "Cluster tessellation raster material slots"), sync);
                return;
            case ClusterTessellationCoreResource.HiZ:
                _ = pass.Bind(
                    graph.CreateTextureSrv(
                        phaseOne ? frame.PreviousHiZ : frame.CurrentHiZ,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster tessellation HiZ"),
                    sync);
                return;
            case ClusterTessellationCoreResource.Depth:
                _ = pass.Bind(
                    graph.CreateTextureSrv(
                        frame.Depth,
                        new TextureSubresourceRange(
                            0,
                            1,
                            0,
                            frame.ViewCount,
                            TextureAspects.Depth),
                        Format.D32Float,
                        SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        "Cluster tessellation depth"),
                    sync);
                return;
            case ClusterTessellationCoreResource.Visibility:
                _ = pass.Bind(
                    graph.CreateTextureUav(
                        frame.VisBuffer,
                        format: Format.R32UInt,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster tessellation visibility"),
                    sync,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.WinnerImage:
                BindVisibilityWinnerImage(ref graph, ref pass, sync);
                return;
            case ClusterTessellationCoreResource.WinnerBuffer:
                BindVisibilityWinnerBuffer(ref graph, ref pass, sync);
                return;
            case ClusterTessellationCoreResource.Counters:
                _ = pass.Bind(
                    graph.CreateBufferUav(frame.TessellationCounters, label: "Cluster tessellation counters"),
                    sync,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.WorkPatches0:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationWorkPatches,
                        structureStride: TessellationWorkPatchStride,
                        label: "Cluster tessellation even-level work patches"),
                    sync,
                    (nextLevelArgument & 1u) == 0u ? GraphAccessMode.Write : GraphAccessMode.Read,
                    WriteCoverage.Partial,
                    ResourceContentState.Defined,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.WorkPatches1:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationWorkPatchesAlternate,
                        structureStride: TessellationWorkPatchStride,
                        label: "Cluster tessellation odd-level work patches"),
                    sync,
                    (nextLevelArgument & 1u) == 0u ? GraphAccessMode.Read : GraphAccessMode.Write,
                    WriteCoverage.Partial,
                    ResourceContentState.Defined,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.LevelCounts:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationLevelCounts,
                        structureStride: sizeof(uint),
                        label: "Cluster tessellation level counts"),
                    sync,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.NextLevelArgs:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationLevelDispatchArgs[checked((int)nextLevelArgument)],
                        label: $"Cluster tessellation level {nextLevelArgument} dispatch"),
                    sync,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.FinalPatches:
                _ = pass.Bind(
                    graph.CreateBufferUav(
                        frame.TessellationFinalPatches,
                        structureStride: TessellationFinalPatchStride,
                        label: "Cluster tessellation final patches"),
                    sync,
                    GraphAccessMode.Write,
                    WriteCoverage.Partial,
                    ResourceContentState.Defined,
                    contentScope: BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.RasterPatches:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.TessellationFinalPatches,
                        structureStride: TessellationFinalPatchStride,
                        label: "Cluster tessellation raster patches"),
                    sync,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.GroupedPatchIndices:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.TessellationWorkPatches,
                        structureStride: sizeof(uint),
                        label: "Cluster tessellation grouped patch indices"),
                    sync,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.BinCounts:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.TessellationBinCounts,
                        structureStride: sizeof(uint),
                        label: "Cluster tessellation bin counts"),
                    sync);
                return;
            case ClusterTessellationCoreResource.BinOffsets:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.TessellationBinOffsets,
                        structureStride: sizeof(uint),
                        label: "Cluster tessellation bin offsets"),
                    sync);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown Cluster tessellation core resource {resource}.");
        }
    }

    private void BindSoftwareRasterResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        GraphBufferInput uniformBuffer,
        bool policyTarget)
    {
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource> stage =
            (policyTarget ? invocation.TargetStages?.SoftwareResources : null) ??
            invocation.SoftwareResources ?? throw new InvalidOperationException(
                "A recorded software-raster pass has no software stage resources.");
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Cluster software-raster resource arrays are not supported: " +
                    $"'{resource.Name}[{resource.Element}]'.");
            }
            ClusterSoftwareRasterCoreResource core = stage.CoreResources[resourceIndex];
            if (BindDeformRead(ref graph, ref pass, resource, PipelineSync.ComputeShading)) continue;
            if (core == ClusterSoftwareRasterCoreResource.PolicyViews)
            {
                BindPolicyClusterViews(ref graph, ref pass, PipelineSync.ComputeShading, policyTarget);
                continue;
            }
            if (core != ClusterSoftwareRasterCoreResource.None)
            {
                BindSoftwareRasterCoreResource(
                    ref graph,
                    ref pass,
                    core,
                    uniformBuffer);
                continue;
            }
            if (!policyTarget && BindNeutralRasterTargetResource(ref graph, ref pass, resource))
                continue;
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                featureBinder(ref graph, ref pass, PipelineSync.ComputeShading);
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
                resource,
                materialBinding,
                PipelineSync.ComputeShading);
        }
    }

    private void BindHardwareRasterResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        GraphBufferInput uniformBuffer,
        GraphBufferInput dispatchUniformBuffer,
        bool policyTarget)
    {
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> stage =
            (policyTarget ? invocation.TargetStages?.HardwareResources : null) ?? invocation.HardwareResources;
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Cluster hardware-raster resource arrays are not supported: " +
                    $"'{resource.Name}[{resource.Element}]'.");
            }
            ClusterHardwareRasterCoreResource core = stage.CoreResources[resourceIndex];
            if (BindDeformRead(ref graph, ref pass, resource, PipelineSync.AllShading)) continue;
            if (core == ClusterHardwareRasterCoreResource.PolicyViews)
            {
                BindPolicyClusterViews(ref graph, ref pass, PipelineSync.AllShading, policyTarget);
                continue;
            }
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
            if (!policyTarget && BindNeutralRasterTargetResource(ref graph, ref pass, resource))
                continue;
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                featureBinder(ref graph, ref pass, PipelineSync.AllShading);
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
                resource,
                materialBinding,
                PipelineSync.AllShading);
        }
    }

    private bool BindNeutralRasterTargetResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        in ClusterLinkedStageResource resource)
    {
        switch (resource.Name, resource.Type)
        {
            case ("RasterTargetViews", ResourceBindingType.BufferSrv):
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        _frameResources.NeutralPolicyView,
                        structureStride: ClusterRenderContext.TargetViewSizeInBytes,
                        label: "Cluster neutral raster target view"),
                    PipelineSync.AllShading);
                return true;
            case ("RasterTargetPageHierarchy", ResourceBindingType.BufferSrv):
            case ("RasterTargetPageTable", ResourceBindingType.BufferSrv):
            case ("RasterTargetReceiverMasks", ResourceBindingType.BufferSrv):
                uint stride = resource.Name switch
                {
                    "RasterTargetPageHierarchy" => sizeof(uint),
                    "RasterTargetPageTable" => 4u * sizeof(uint),
                    _ => 2u * sizeof(uint),
                };
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        _frameResources.NeutralPolicyView,
                        structureStride: stride,
                        label: $"Cluster neutral {resource.Name}"),
                    PipelineSync.AllShading);
                return true;
            case ("RasterTargetOutput", ResourceBindingType.TextureUav):
                _ = pass.Bind(
                    graph.CreateTextureUav(
                        _frameResources.NeutralPolicyOutput,
                        new TextureSubresourceRange(
                            0,
                            1,
                            0,
                            1,
                            TextureAspects.Color),
                        format: Format.R32UInt,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                        label: "Cluster neutral raster target output"),
                    PipelineSync.AllShading,
                    GraphAccessMode.ReadWrite,
                    WriteCoverage.Partial,
                    unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
                return true;
            default:
                return false;
        }
    }

    private void BindSoftwareRasterCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterSoftwareRasterCoreResource resource,
        GraphBufferInput uniformBuffer)
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
                _ = pass.Bind(graph.CreateBufferCbv(uniformBuffer),
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
            case ClusterSoftwareRasterCoreResource.PolicyViews:
                BindPolicyClusterViews(ref graph, ref pass, PipelineSync.ComputeShading);
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
                    PipelineSync.ComputeShading,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterSoftwareRasterCoreResource.RasterWindows:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusterRasterWindows,
                        structureStride: VisibleClusterRasterWindowStride,
                        label: "Cluster software raster windows"),
                    PipelineSync.ComputeShading,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterSoftwareRasterCoreResource.BinnedClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.BinnedClusters,
                        structureStride: BinnedClusterStride,
                        label: "Cluster software raster binned clusters"),
                    PipelineSync.ComputeShading,
                    BufferContentScope.CountBoundedRanges);
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
                            frame.ViewCount,
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
            case ClusterSoftwareRasterCoreResource.WinnerImage:
                BindVisibilityWinnerImage(ref graph, ref pass, PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.WinnerBuffer:
                BindVisibilityWinnerBuffer(ref graph, ref pass, PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Diagnostics:
                _ = pass.Bind(
                    graph.CreateBufferUav(frame.SoftwareDebug, label: "Cluster software raster diagnostics"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.Write,
                    WriteCoverage.Partial);
                _ = pass.Read(
                    frame.SoftwareDebug,
                    new BufferRange(0, sizeof(uint)),
                    PipelineSync.ComputeShading,
                    ResourceAccess.ShaderResource);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown Cluster software-raster core resource {resource}.");
        }
    }

    private void BindHardwareRasterCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterHardwareRasterCoreResource resource,
        GraphBufferInput uniformBuffer,
        GraphBufferInput dispatchUniformBuffer)
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
                _ = pass.Bind(graph.CreateBufferCbv(uniformBuffer),
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
            case ClusterHardwareRasterCoreResource.PolicyViews:
                BindPolicyClusterViews(ref graph, ref pass, PipelineSync.AllShading);
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
                    PipelineSync.AllShading,
                    BufferContentScope.CountBoundedRanges);
                return;
            case ClusterHardwareRasterCoreResource.VisibleClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusters,
                        structureStride: VisibleClusterStride,
                        label: "Cluster hardware raster visible clusters"),
                    PipelineSync.AllShading,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterHardwareRasterCoreResource.RasterWindows:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.VisibleClusterRasterWindows,
                        structureStride: VisibleClusterRasterWindowStride,
                        label: "Cluster hardware raster windows"),
                    PipelineSync.AllShading,
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterHardwareRasterCoreResource.DrawMetadata:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.HardwareIndirectArgs,
                        label: "Cluster hardware raster draw metadata"),
                    PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.WinnerImage:
                BindVisibilityWinnerImage(ref graph, ref pass, PipelineSync.PixelShading);
                return;
            case ClusterHardwareRasterCoreResource.WinnerBuffer:
                BindVisibilityWinnerBuffer(ref graph, ref pass, PipelineSync.PixelShading);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unknown Cluster hardware-raster core resource {resource}.");
        }
    }

    private void BindVisibilityWinnerImage(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync)
    {
        if (_visibilityMode != ClusterVisibilityMode.ImageAtomic64 ||
            _frameResources.VisibilityWinnerImage == default)
        {
            throw new InvalidOperationException(
                "An image-A64 material entry was selected without an image winner resource.");
        }
        _ = pass.Bind(
            graph.CreateTextureUav(
                _frameResources.VisibilityWinnerImage,
                format: Format.R64UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Cluster visibility winner image"),
            sync,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
    }

    private void BindVisibilityWinnerBuffer(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        PipelineSync sync)
    {
        if (_visibilityMode != ClusterVisibilityMode.BufferAtomic64 ||
            _frameResources.VisibilityWinnerBuffer == default)
        {
            throw new InvalidOperationException(
                "A buffer-A64 material entry was selected without a buffer winner resource.");
        }
        _ = pass.Bind(
            graph.CreateBufferUav(
                _frameResources.VisibilityWinnerBuffer,
                label: "Cluster visibility winner buffer"),
            sync,
            GraphAccessMode.ReadWrite,
            WriteCoverage.Partial,
            unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
    }

    private void BindPageHeapDescriptor(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        string label,
        PipelineSync sync)
    {
        FrameResources frame = _frameResources;
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(frame.PageHeap, label: label)));
        _pageHeapReaderPasses = _pageHeapReaderPasses.Add(pass.Id);
    }

    private GraphBufferInput BindShadeResources(
        ref RenderGraphEdit graph, GraphPassId passId, ClusterShadeInvocation invocation,
        ClusterShadeStageSet stages)
    {
        _materialInputsAreShade = true;
        _materialInputBin = invocation.BinIndex;
        _shadeScalarOffsetsInput ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        _shadeScalarsInput ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        PassDefinition pass = graph.GetPass(passId);
        pass.SetParameterBlock(invocation.Pipeline.ParameterLayout);
        GraphBufferInput uniformBuffer = UploadUniform(ref graph,
            ShadeUniforms(invocation.BinIndex, stages), "Cluster shade parameters");
        ReadOnlySpan<ClusterLinkedStageResource> resources = invocation.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException(
                    $"Cluster material resource arrays are not supported: '{resource.Name}[{resource.Element}]'.");
            }
            ClusterShadeCoreResource coreResource = invocation.CoreResources[resourceIndex];
            if (BindDeformRead(ref graph, ref pass, resource, PipelineSync.ComputeShading)) continue;
            if (coreResource != ClusterShadeCoreResource.None)
            {
                BindShadeCoreResource(
                    ref graph,
                    ref pass,
                    coreResource,
                    uniformBuffer,
                    _shadeScalarsInput,
                    _shadeScalarOffsetsInput);
                continue;
            }
            if (invocation.FeatureResourceBinders[resourceIndex] is { } featureBinder)
            {
                featureBinder(ref graph, ref pass, PipelineSync.ComputeShading);
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
                resource,
                materialBinding,
                PipelineSync.ComputeShading);
        }
        return uniformBuffer;
    }

    private void BindShadeCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterShadeCoreResource resource,
        GraphBufferInput uniformBuffer,
        GraphBufferInput scalarBuffer,
        GraphBufferInput scalarOffsetBuffer)
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
                    uniformBuffer,
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
                    "PixelCoordBuffer",
                    BufferContentScope.CountBoundedRanges);
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
                            frame.ViewCount,
                            TextureAspects.Depth),
                        Format.D32Float,
                        SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster shade depth"),
                    PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.VisibleClusters:
                BindReadOnly(
                    ref graph,
                    ref pass,
                    frame.VisibleClusters,
                    VisibleClusterStride,
                    "VisibleClusters",
                    BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterShadeCoreResource.PageHeap:
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
                    graph.CreateBufferSrv(
                        frame.PageHeap,
                        label: "Cluster shade PageHeap")));
                _pageHeapReaderPasses = _pageHeapReaderPasses.Add(pass.Id);
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
            case ClusterShadeCoreResource.OutputMotionVectors:
                _ = pass.Bind(
                    graph.CreateTextureUav(
                        frame.MotionVectors,
                        format: Format.R16G16Float,
                        dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                        label: "Cluster material motion output"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.Write,
                    WriteCoverage.Partial);
                return;
            case ClusterShadeCoreResource.MaterialScalars:
                _ = pass.Bind(graph.CreateBufferSrv(scalarBuffer), PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.MaterialScalarOffsets:
                _ = pass.Bind(graph.CreateBufferSrv(scalarOffsetBuffer, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
                return;
            default:
                throw new InvalidOperationException($"Unknown Cluster shade core resource {resource}.");
        }

        void BindReadOnly(
            ref RenderGraphEdit activeGraph,
            ref PassDefinition activePass,
            GraphBufferId buffer,
            uint stride,
            string name,
            BufferContentScope contentScope = BufferContentScope.FullRange)
        {
            _ = activePass.Bind(
                activeGraph.CreateBufferSrv(
                    buffer,
                    structureStride: stride,
                    label: $"Cluster shade {name}"),
                PipelineSync.ComputeShading,
                contentScope);
        }
    }

    private void BindStageMaterialResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        in ClusterLinkedStageResource resource,
        in ClusterMaterialStageBinding binding,
        PipelineSync sync)
    {
        GraphParameterInput input = graph.CreateParameterInput(binding.Type);
        pass.Bind(input, sync);
        ImmutableList<MaterialInput> inputs = _materialInputs.TryGetValue(pass.Id, out var existing)
            ? existing : [];
        _materialInputs = _materialInputs.SetItem(pass.Id, inputs.Add(new MaterialInput(
            pass.Id, input, binding.MaterialSlot, resource.Name, _materialInputBin, _materialInputsAreShade)));
    }

    private ResourceBinding GetMaterialTextureSrv(
        ref RenderGraphFrame graph,
        scoped ReadOnlySpan<GraphPassId> consumers,
        Material material,
        uint materialSlot,
        string name)
    {
        EngineTexture? asset = material.GetTexture(materialSlot);

        TextureSrv textureView;
        if (asset is null)
        {
            _ = _renderDevice.PrepareMissingTexture(ref graph, consumers);
            textureView = _renderDevice.MissingTextureSrv;
        }
        else
        {
            _ = _renderDevice.PrepareTexture(ref graph, consumers, asset);
            textureView = _renderDevice.GetTextureSrv(asset);
        }

        return ResourceBinding.SampledTexture(textureView);
    }

    private ResourceBinding GetMaterialTextureUav(
        ref RenderGraphFrame graph,
        scoped ReadOnlySpan<GraphPassId> consumers,
        Material material,
        uint materialSlot,
        string name)
    {
        EngineTexture asset = material.GetTexture(materialSlot) ?? throw new InvalidDataException(
            $"Material '{material.Name}' slot '{name}' must contain a Texture for writable access.");
        RhiTexture textureResource = _renderDevice.PrepareTexture(ref graph, consumers, asset);
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
        return ResourceBinding.StorageTexture(textureView);
    }

    private ResourceBinding GetMaterialBufferSrv(
        Material material,
        uint materialSlot,
        string name)
    {
        BufferSrv view = RequireMaterialValue<BufferSrv>(material, materialSlot, name);
        return ResourceBinding.ReadOnlyBuffer(view);
    }

    private ResourceBinding GetMaterialBufferUav(
        Material material,
        uint materialSlot,
        string name)
    {
        BufferUav view = RequireMaterialValue<BufferUav>(material, materialSlot, name);
        return ResourceBinding.WritableBuffer(view);
    }

    private ResourceBinding GetMaterialConstantBuffer(
        Material material,
        uint materialSlot,
        string name)
    {
        BufferCbv view = RequireMaterialValue<BufferCbv>(material, materialSlot, name);
        return ResourceBinding.ConstantBuffer(view);
    }

    private ResourceBinding GetMaterialAccelerationStructure(
        Material material,
        uint materialSlot,
        string name)
    {
        AccelerationStructureSrv view = RequireMaterialValue<AccelerationStructureSrv>(
            material,
            materialSlot,
            name);
        return ResourceBinding.AccelerationStructure(view);
    }

    private static T RequireMaterialValue<T>(Material material, uint slot, string name)
        where T : class
    {
        object? value = material.GetDeviceResource(slot);
        DevelopmentCheck.Require(value is T,
            $"Material '{material.Name}' slot '{name}' must contain a {typeof(T).Name}.");
        return (T)value!;
    }

    private Sampler ResolveMaterialSampler(Material material, uint materialSlot, string name)
    {
        SamplerDesc description = material.GetSampler(materialSlot);
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


    private ImmutableDictionary<GraphPassId, ImmutableList<MaterialInput>> _materialInputs =
        ImmutableDictionary<GraphPassId, ImmutableList<MaterialInput>>.Empty;
    private uint _materialInputBin;
    private bool _materialInputsAreShade;
    private GraphBufferInput _shadeScalarOffsetsInput = null!;
    private GraphBufferInput _shadeScalarsInput = null!;

    private ClusterMaterialScalars _materialScalars = ClusterMaterialScalars.Empty;
    private GraphBufferId _scalarBytes;
    private GraphBufferId _scalarOffsets;

    private void UpdateMaterialIndexBuffers(ref RenderGraphEdit edit,
        ClusterRasterStageSet raster, ClusterShadeStageSet shade)
    {
        Update(ref edit, _frameResources.RasterSlots, raster.BinIndices, raster.ChangedOffsets);
        Update(ref edit, _frameResources.ShadeSlots, shade.BinIndices, shade.ChangedOffsets);

        void Update(ref RenderGraphEdit graph, GraphBufferId buffer, ClusterUploadData values,
            IReadOnlyList<int> addresses)
        {
            BufferRange[] changes = addresses.Select(address => new BufferRange(
                checked((ulong)address * sizeof(uint)), sizeof(uint))).ToArray();
            _uploads!.SetChangedValue(ref graph, buffer,
                values, changes);
        }
    }

    private void UpdateScalarBuffers(ref RenderGraphEdit edit, ClusterMaterialScalars scalars)
    {
        _uploads!.SetChangedValue(ref edit, _scalarBytes, scalars.Bytes, scalars.ByteChanges);
        _uploads.SetChangedValue(ref edit, _scalarOffsets,
            scalars.Offsets, scalars.OffsetChanges);
    }

    private readonly record struct MaterialInput(
        GraphPassId Pass, GraphParameterInput Input, uint MaterialSlot, string Name,
        uint Bin, bool Shade);

    private void UpdateShadeInputs(ref RenderGraphFrame graph, ClusterShadeStageSet stages)
    {
        ClusterDispatchParameters binDispatch = new(new DispatchArguments(
            Math.Max(1u, (stages.BinCount + 127u) / 128u), 1, 1));
        graph.SetPassData(_shadeClearPass, binDispatch);
        graph.SetPassData(_shadeReservePass, binDispatch);
        graph.SetBufferInput(_shadeScalarOffsetsInput, new GraphBufferSlice(_scalarOffsets,
            new BufferRange(0, checked((ulong)_materialScalars.Offsets.Length))));
        graph.SetBufferInput(_shadeScalarsInput, new GraphBufferSlice(_scalarBytes,
            new BufferRange(0, checked((ulong)_materialScalars.Bytes.Length))));
    }

    private MaterialInputGroup[] _materialInputGroups = [];
    private ImmutableDictionary<GraphPassId, ImmutableList<MaterialInput>>? _materialInputSource;
    private ClusterRasterStageSet? _materialInputRaster;
    private ClusterShadeStageSet? _materialInputShade;

    private sealed record MaterialInputGroup(Material Material, uint Property, string Name,
        ResourceBindingType Type, GraphPassId[] Consumers, GraphParameterInput[] Inputs);

    private void RefreshMaterialInputGroups()
    {
        if (ReferenceEquals(_materialInputSource, _materialInputs) &&
            ReferenceEquals(_materialInputRaster, _installedRasterStages) &&
            ReferenceEquals(_materialInputShade, _installedShadeStages)) return;
        var groups = new Dictionary<(Material Material, uint Property, ResourceBindingType Type), List<MaterialInput>>();
        foreach (ImmutableList<MaterialInput> inputs in _materialInputs.Values)
        foreach (MaterialInput input in inputs)
        {
            Material material = input.Shade
                ? _installedShadeStages!.GetBin(input.Bin).Material
                : _installedRasterStages!.GetBin(input.Bin).Material;
            var key = (material, input.MaterialSlot, input.Input.Type);
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
            group.Add(input);
        }
        _materialInputGroups = groups.Select(pair => new MaterialInputGroup(
            pair.Key.Material, pair.Key.Property, pair.Value[0].Name, pair.Key.Type,
            pair.Value.Select(input => input.Pass).Distinct().ToArray(),
            pair.Value.Select(input => input.Input).ToArray())).ToArray();
        _materialInputSource = _materialInputs;
        _materialInputRaster = _installedRasterStages;
        _materialInputShade = _installedShadeStages;
    }

    private void UpdateMaterialInputs(ref RenderGraphFrame graph)
    {
        RefreshMaterialInputGroups();
        foreach (MaterialInputGroup group in _materialInputGroups)
        {
            Material material = group.Material;
            ResourceBinding value = group.Type switch
            {
                ResourceBindingType.TextureSrv => GetMaterialTextureSrv(ref graph, group.Consumers, material, group.Property, group.Name),
                ResourceBindingType.TextureUav => GetMaterialTextureUav(ref graph, group.Consumers, material, group.Property, group.Name),
                ResourceBindingType.BufferSrv => GetMaterialBufferSrv(material, group.Property, group.Name),
                ResourceBindingType.BufferUav => GetMaterialBufferUav(material, group.Property, group.Name),
                ResourceBindingType.ConstantBuffer => GetMaterialConstantBuffer(material, group.Property, group.Name),
                ResourceBindingType.AccelerationStructure => GetMaterialAccelerationStructure(material, group.Property, group.Name),
                _ => ResourceBinding.SampledWith(ResolveMaterialSampler(material, group.Property, group.Name)),
            };
            foreach (GraphParameterInput input in group.Inputs) graph.SetParameterInput(input, value);
        }
    }
}
