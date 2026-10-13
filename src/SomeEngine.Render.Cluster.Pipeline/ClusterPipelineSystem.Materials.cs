using SomeEngine.Render.Lighting;
using System.Collections.Immutable;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct MaterialBuffer(GraphBufferId Id, int References, bool RegisteredHere);

    // Vertex/domain/visibility and shading bind the same material scalar data.
    // The existing scalar holder supplies every stage, and user shaders
    // interpret their own fields.
    private sealed class RasterMaterialResources(ClusterPipelineSystem pipeline, IGraphResourceProvider? features) : IGraphResourceProvider
    {
        public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
        {
            if (type == ResourceBindingType.BufferSrv)
            {
                if (name == "MaterialScalarRegion") { binder = pipeline.BindRasterMaterialScalars; return true; }
                if (name == "MaterialScalarOffsets") { binder = pipeline.BindRasterMaterialOffsets; return true; }
            }
            if (features is not null) return features.TryResolve(name, type, out binder);
            binder = null!;
            return false;
        }
    }

    private void BindRasterMaterialScalars(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.ShadeScalars), sync);
        ReadIndexedMaterialTextures(ref graph, ref pass, _rasterInputs!.TextureReads, sync);
    }

    private void BindRasterMaterialOffsets(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.ShadeScalarOffsets, structureStride: sizeof(uint)), sync);

    private (ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer> Buffers,
        ImmutableDictionary<EngineTexture, MaterialTexture> Textures) UpdateMaterialResources(
        ref RenderGraphEdit edit, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        HashSet<GraphParameterInput> inputs = edit.GetParameterInputs().ToHashSet();
        GraphData.MaterialInputs = GraphData.MaterialInputs.RemoveRange(GraphData.MaterialInputs
            .Where(pair => !inputs.Contains(pair.Value.Input)).Select(static pair => pair.Key));
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
            {
                CountBuffers(before.CommandBindings, -1);
                if (before.SurfaceShader is { } receiver) CountBuffers(receiver.CommandBindings, -1);
            }
            if (shadeStages.TryGetBin(bin, out var after))
            {
                CountBuffers(after.CommandBindings, 1);
                if (after.SurfaceShader is { } receiver) CountBuffers(receiver.CommandBindings, 1);
            }
        }
        void CountBuffers(ClusterCommandBindingKey bindings, int change)
        {
            resources.Clear();
            bindings.CollectBuffers(resources);
            foreach (SomeEngine.Graphics.Buffer buffer in resources)
                changes[buffer] = changes.GetValueOrDefault(buffer) + change;
        }
        var buffers = GraphData.MaterialBuffers;
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
        return (buffers, UpdateMaterialTextures(ref edit));
    }

    private readonly Dictionary<SamplerDesc, Sampler> _materialSamplers = [];
    // Observed property revisions serve GPU bindings and declared depth inputs; values stay on Material.
    private readonly Dictionary<long, MaterialBindingVersions> _materialBindingVersions = [];
    private sealed record MaterialBindingVersions(
        Shader? Shader, ulong[] Properties)
    {
        internal bool Matches(Material material, ClusterRasterStages raster, ClusterShadeStages shade)
        {
            if (!ReferenceEquals(Shader, material.Shader) || Properties.Length != material.PropertyCount)
                return false;
            for (uint property = 0; property < Properties.Length; property++)
                if (material.GetPropertyRevision(property) != Properties[property] &&
                    (raster.UsesResourceProperty(material.MaterialId, property) ||
                     shade.UsesResourceProperty(material.MaterialId, property))) return false;
            return true;
        }
        internal bool MatchesDepthInputs(Material material, HashSet<uint> properties)
        {
            foreach (uint property in properties)
                if (material.GetPropertyRevision(property) != Properties[property]) return false;
            return true;
        }
    }

    private ClusterMaterialAssignmentSet? _consumedMaterialAssignments;

    private bool CollectMaterialChanges(ClusterMaterialAssignmentSet assignments, bool topologyChanged)
    {
        _rasterChangedMaterials.Clear();
        bool resourcesChanged = false;
        foreach (long id in assignments.ChangedMaterialIds)
        {
            if (!assignments.TryGetMaterial(id, out Material material))
            {
                _rasterChangedMaterials.Add(id);
                continue;
            }
            bool resourcesMatch = _materialBindingVersions.TryGetValue(id, out var versions) &&
                versions.Matches(material, _rasterStages!, _shadeStages!);
            resourcesChanged |= !resourcesMatch;
            if (topologyChanged || !resourcesMatch ||
                !_rasterStages!.TryGetMaterialDepthInputs(id, out var properties) ||
                !versions!.MatchesDepthInputs(material, properties))
                _rasterChangedMaterials.Add(id);
        }
        return resourcesChanged;
    }

    private void AcceptMaterialChanges(ClusterMaterialAssignmentSet assignments, ClusterMaterialAssignmentSet source)
    {
        foreach (long id in assignments.ChangedMaterialIds)
        {
            if (!assignments.TryGetMaterial(id, out Material material))
            {
                _materialBindingVersions.Remove(id);
                continue;
            }
            if (_materialBindingVersions.TryGetValue(id, out var versions) &&
                versions.Matches(material, _rasterStages!, _shadeStages!) &&
                (!_rasterStages!.TryGetMaterialDepthInputs(id, out var propertiesRead) ||
                 versions.MatchesDepthInputs(material, propertiesRead)))
                continue;
            var properties = new ulong[material.PropertyCount];
            for (uint property = 0; property < material.PropertyCount; property++)
                properties[property] = material.GetPropertyRevision(property);
            _materialBindingVersions[id] = new(material.Shader, properties);
        }
        _consumedMaterialAssignments = assignments;
        _materialAssignments.AcceptMaterialChanges(source);
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
        GraphBufferSlice uniformBuffer,
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
                    sync, policyTarget);
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
                invocation.Material,
                sync);
        }
    }

    private void BindTessellationCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterTessellationCoreResource resource,
        GraphBufferSlice uniformBuffer,
        bool phaseOne,
        uint nextLevelArgument,
        PipelineSync sync,
        bool policyTarget)
    {
        FrameResources frame = GetRasterFrameResources(GraphData.Resources, policyTarget);
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
                _ = pass.Bind(uniformBuffer,
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
                        format: Format.R32Float,
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
            case ClusterTessellationCoreResource.WorkStatus:
                BindWorkStatus(ref graph, ref pass);
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
        GraphBufferSlice uniformBuffer,
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
                    uniformBuffer, policyTarget);
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
                invocation.Material,
                PipelineSync.ComputeShading);
        }
    }

    private void BindHardwareRasterResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        GraphBufferSlice uniformBuffer,
        GraphBufferSlice dispatchUniformBuffer,
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
                    dispatchUniformBuffer, policyTarget);
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
                invocation.Material,
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
                        GraphData.Resources.NeutralPolicyView,
                        structureStride: ClusterRenderContext.TargetViewSizeInBytes,
                        label: "Cluster neutral raster target view"),
                    PipelineSync.AllShading);
                return true;
            case ("RasterTargetDepthCacheClasses", ResourceBindingType.BufferSrv):
            case ("RasterTargetPageHierarchy", ResourceBindingType.BufferSrv):
            case ("RasterTargetPageTable", ResourceBindingType.BufferSrv):
            case ("RasterTargetPageMetadata", ResourceBindingType.BufferSrv):
                uint stride = resource.Name switch
                {
                    "RasterTargetPageHierarchy" => 2u * sizeof(uint),
                    "RasterTargetDepthCacheClasses" => sizeof(uint),
                    "RasterTargetPageTable" or "RasterTargetPageMetadata" => 4u * sizeof(uint),
                    _ => 2u * sizeof(uint),
                };
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        GraphData.Resources.NeutralPolicyView,
                        structureStride: stride,
                        label: $"Cluster neutral {resource.Name}"),
                    PipelineSync.AllShading);
                return true;
            case ("RasterTargetReceiverMasks", ResourceBindingType.TextureSrv):
                _ = pass.Bind(graph.CreateTextureSrv(GraphData.Resources.NeutralPolicyOutput,
                    format: Format.R32UInt, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D), PipelineSync.AllShading);
                return true;
            case ("RasterTargetCurrentHiZ", ResourceBindingType.TextureSrv):
            case ("RasterTargetPreviousHiZ", ResourceBindingType.TextureSrv):
                _ = pass.Bind(graph.CreateTextureSrv(GraphData.Resources.PreviousHiZ,
                    format: Format.R32Float,
                    dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                    label: $"Cluster neutral {resource.Name}"), PipelineSync.AllShading);
                return true;
            case ("RasterTargetOutput", ResourceBindingType.TextureUav):
            case ("RasterTargetStaticOutput", ResourceBindingType.TextureUav):
                pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.TextureUav));
                return true;
            default:
                return false;
        }
    }

    private void BindSoftwareRasterCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterSoftwareRasterCoreResource resource,
        GraphBufferSlice uniformBuffer,
        bool policyTarget)
    {
        FrameResources frame = GetRasterFrameResources(GraphData.Resources, policyTarget);
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
                _ = pass.Bind(uniformBuffer,
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
        GraphBufferSlice uniformBuffer,
        GraphBufferSlice dispatchUniformBuffer,
        bool policyTarget)
    {
        FrameResources frame = GetRasterFrameResources(GraphData.Resources, policyTarget);
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
                _ = pass.Bind(uniformBuffer,
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
                        dispatchUniformBuffer.Buffer,
                        dispatchUniformBuffer.Range,
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
            GraphData.Resources.VisibilityWinnerImage == default)
        {
            throw new InvalidOperationException(
                "An image-A64 material entry was selected without an image winner resource.");
        }
        _ = pass.Bind(
            graph.CreateTextureUav(
                GraphData.Resources.VisibilityWinnerImage,
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
            GraphData.Resources.VisibilityWinnerBuffer == default)
        {
            throw new InvalidOperationException(
                "A buffer-A64 material entry was selected without a buffer winner resource.");
        }
        _ = pass.Bind(
            graph.CreateBufferUav(
                GraphData.Resources.VisibilityWinnerBuffer,
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
        FrameResources frame = GraphData.Resources;
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(frame.PageHeap, label: label)));
    }

    private GraphBufferSlice BindShadeResources(
        ref RenderGraphEdit graph, GraphPassId passId, ClusterShadeInvocation invocation,
        ClusterShadeStageSet stages, bool receivers)
    {
        GraphData.ShadeScalarOffsets ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        GraphData.ShadeScalars ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        PassDefinition pass = graph.GetPass(passId);
        pass.SetParameterBlock(invocation.Pipeline.ParameterLayout);
        GraphBufferSlice uniformBuffer = UploadUniform(ref graph,
            ShadeUniforms(invocation.BinIndex, stages), "Cluster shade parameters");
        // A final export overwrites the early receiver; only writes within one stage are disjoint.
        UnorderedAccessHazardMode receiverHazards =
            !receivers && stages.ShadingPath == ClusterShadingPath.Forward && stages.ExportSurfaceReceivers
                ? UnorderedAccessHazardMode.Ordered : UnorderedAccessHazardMode.DisjointWrites;
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
            if (coreResource != ClusterShadeCoreResource.None)
            {
                BindShadeCoreResource(
                    ref graph,
                    ref pass,
                    coreResource,
                    uniformBuffer,
                    GraphData.ShadeScalars,
                    GraphData.ShadeScalarOffsets, receiverHazards);
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
                invocation.Material,
                PipelineSync.ComputeShading);
        }
        return uniformBuffer;
    }

    private void BindShadeCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterShadeCoreResource resource,
        GraphBufferSlice uniformBuffer,
        GraphBufferInput scalarBuffer,
        GraphBufferInput scalarOffsetBuffer,
        UnorderedAccessHazardMode receiverHazards)
    {
        FrameResources frame = GraphData.Resources;
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
                    3 * sizeof(uint),
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
                    WriteCoverage.Partial,
                    unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
                return;
            case ClusterShadeCoreResource.MaterialSurfaceOffsets:
                BindReadOnly(ref graph, ref pass, frame.SurfaceDataOffsets, sizeof(uint), "MaterialSurfaceOffsets");
                return;
            case ClusterShadeCoreResource.MaterialSurfaceOutputs:
                _ = pass.Bind(graph.CreateBufferUav(frame.MaterialSurfaces), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined,
                    BufferContentScope.CountBoundedRanges, UnorderedAccessHazardMode.DisjointWrites);
                return;
            case ClusterShadeCoreResource.MaterialSurfaceInputs:
                _ = pass.Bind(graph.CreateBufferSrv(frame.MaterialSurfaces), PipelineSync.ComputeShading,
                    BufferContentScope.CountBoundedRanges);
                return;
            case ClusterShadeCoreResource.SurfaceReceiverOutput:
                _ = pass.Bind(graph.CreateBufferUav(frame.SurfaceReceivers, structureStride: SurfaceReceiver.SizeInBytes), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Partial,
                    unorderedAccessHazards: receiverHazards);
                return;
            case ClusterShadeCoreResource.AdditionalSurfaceReceiverOutput:
                _ = pass.Bind(graph.CreateBufferUav(frame.AdditionalSurfaceReceivers, structureStride: SurfaceReceiver.SizeInBytes), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Partial);
                return;
            case ClusterShadeCoreResource.SurfaceReceiverInputs:
                _ = pass.Bind(graph.CreateBufferSrv(frame.SurfaceReceivers, structureStride: SurfaceReceiver.SizeInBytes), PipelineSync.ComputeShading);
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
                    WriteCoverage.Partial,
                    unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
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
        Material material,
        PipelineSync sync)
    {
        MaterialInput input = GetMaterialInput(ref graph, material,
            binding.MaterialSlot, resource.Name, binding.Type);
        pass.Bind(input.Input, sync);
    }

    private MaterialInput GetMaterialInput(ref RenderGraphEdit graph, Material material,
        uint property, string name, ResourceBindingType type)
    {
        var key = (material, property);
        GraphData.MaterialInputChanges = GraphData.MaterialInputChanges.Add(key);
        if (GraphData.MaterialInputs.TryGetValue(key, out MaterialInput input))
        {
            DevelopmentCheck.Require(input.Input.Type == type,
                "A material property must keep the same resource binding type.");
            return input;
        }
        input = new MaterialInput(graph.CreateParameterInput(type), name);
        GraphData.MaterialInputs = GraphData.MaterialInputs.Add(key, input);
        return input;
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



    private ClusterMaterialScalars _materialScalars = ClusterMaterialScalars.Empty;

    private void UpdateMaterialIndexBuffers(ref RenderGraphEdit edit,
        ClusterRasterStageSet raster, ClusterShadeStageSet shade)
    {
        if (!ReferenceEquals(raster, _installedRasterStages))
            Update(ref edit, GraphData.Resources.RasterSlots, raster.BinIndices, raster.ChangedOffsets);
        if (!ReferenceEquals(shade, _installedShadeStages))
            Update(ref edit, GraphData.Resources.ShadeSlots, shade.BinIndices, shade.ChangedOffsets);

        void Update(ref RenderGraphEdit graph, GraphBufferId buffer, ClusterBytes values,
            IReadOnlyList<int> addresses)
        {
            BufferRange[] changes = addresses.Select(address => new BufferRange(
                checked((ulong)address * sizeof(uint)), sizeof(uint))).ToArray();
            UpdateBufferData(ref graph, buffer,
                values, changes);
        }
    }

    private void UpdateScalarBuffers(ref RenderGraphEdit edit, ClusterMaterialScalars scalars)
    {
        UpdateBufferData(ref edit, GraphData.ScalarBytes, scalars.Bytes, scalars.ByteChanges);
        UpdateBufferData(ref edit, GraphData.ScalarOffsets,
            scalars.Offsets, scalars.OffsetChanges);
    }

    private readonly record struct MaterialInput(GraphParameterInput Input, string Name);

    private void UpdateShadeInputs(ref RenderGraphFrame graph, ClusterShadeStageSet stages)
    {
        ClusterDispatchParameters binDispatch = new(new DispatchArguments(
            Math.Max(1u, (stages.BinCount + 127u) / 128u), 1, 1));
        foreach (ClusterLayerDispatch dispatch in GraphData.ShadeBinDispatches) graph.SetPassData(dispatch.Pass, binDispatch);
        graph.SetBufferInput(GraphData.ShadeScalarOffsets, new GraphBufferSlice(GraphData.ScalarOffsets,
            new BufferRange(0, checked((ulong)_materialScalars.Offsets.Length))));
        graph.SetBufferInput(GraphData.ShadeScalars, new GraphBufferSlice(GraphData.ScalarBytes,
            new BufferRange(0, checked((ulong)_materialScalars.Bytes.Length))));
    }

    private void UpdateMaterialInputs(ref RenderGraphFrame graph)
    {
        foreach ((Material material, uint property) in GraphData.MaterialInputChanges)
        {
            if (!GraphData.MaterialInputs.TryGetValue((material, property), out MaterialInput input) ||
                input.Input.Type is ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav) continue;
            ResourceBinding value = input.Input.Type switch
            {
                ResourceBindingType.BufferSrv => GetMaterialBufferSrv(material, property, input.Name),
                ResourceBindingType.BufferUav => GetMaterialBufferUav(material, property, input.Name),
                ResourceBindingType.ConstantBuffer => GetMaterialConstantBuffer(material, property, input.Name),
                ResourceBindingType.AccelerationStructure => GetMaterialAccelerationStructure(material, property, input.Name),
                _ => ResourceBinding.SampledWith(ResolveMaterialSampler(material, property, input.Name)),
            };
            graph.SetPersistentParameterInput(input.Input, value);
        }
        GraphData.MaterialInputChanges = GraphData.MaterialInputChanges.Clear();
        UpdateMaterialTextureInputs(ref graph);
        if (_textureScalarChanges.Count != 0)
        {
            ClusterMaterialScalars values = _materialScalars.Update(_installedShadeStages!,
                _materialAssignments.Current, _renderDevice.GetTextureDescriptorIndex, _textureScalarChanges);
            QueueBufferWrites(GraphData.ScalarBytes, values.Bytes, values.ByteChanges);
            _materialScalars = values;
            _textureScalarChanges.Clear();
        }
    }
}
