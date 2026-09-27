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
    private readonly Dictionary<SomeEngine.Graphics.Buffer, GraphBufferId> _materialBuffers = [];

    private void ReconcileMaterialBuffers(ClusterMaterialAssignmentSet assignments)
    {
        var resources = new HashSet<SomeEngine.Graphics.Buffer>();
        foreach (Material material in assignments.DistinctMaterials)
            for (uint slot = 0; slot < material.PropertyCount; slot++)
                switch (material.GetDeviceResource(slot))
                {
                    case BufferCbv view: resources.Add(view.Description.Buffer); break;
                    case BufferSrv view: resources.Add(view.Description.Buffer); break;
                    case BufferUav view:
                        resources.Add(view.Description.Buffer);
                        if (view.Description.CounterBuffer is { } counter) resources.Add(counter);
                        break;
                    case AccelerationStructureSrv view: resources.Add(view.Resource.Info.Storage); break;
                }
        var added = new Dictionary<SomeEngine.Graphics.Buffer, GraphBufferId>();
        var removed = new List<SomeEngine.Graphics.Buffer>();
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            foreach (SomeEngine.Graphics.Buffer buffer in resources)
            {
                if (_graph.TryGetBufferId(buffer, out _)) continue;
                added.Add(buffer, edit.RegisterExternalBuffer(buffer,
                    [new BufferBoundaryState(new BufferRange(0, buffer.Info.Size), buffer.InitialSync,
                        buffer.InitialAccess, ResourceContentState.Defined)]));
            }
            foreach ((SomeEngine.Graphics.Buffer buffer, GraphBufferId id) in _materialBuffers)
                if (!resources.Contains(buffer))
                {
                    edit.Remove(id);
                    removed.Add(buffer);
                }
            edit.Commit();
        }
        finally { edit.Dispose(); }
        foreach (SomeEngine.Graphics.Buffer buffer in removed) _materialBuffers.Remove(buffer);
        foreach (var value in added) _materialBuffers.Add(value.Key, value.Value);
    }

    private readonly Dictionary<SamplerDesc, Sampler> _materialSamplers = [];
    private readonly HashSet<Material> _observedMaterials = [];
    private bool _materialsChanged = true;

    private void MaterialChanged(Material material) => _materialsChanged = true;

    private void ObserveMaterials(ClusterMaterialAssignmentSet assignments)
    {
        foreach (Material material in _observedMaterials) material.Changed -= MaterialChanged;
        _observedMaterials.Clear();
        foreach (Material material in assignments.DistinctMaterials)
            if (_observedMaterials.Add(material)) material.Changed += MaterialChanged;
        _materialsChanged = false;
    }

    private void BindRasterStages(
        ref RenderGraphEdit graph,
        ClusterRasterStageSet rasterStages)
    {
        _materialInputsAreShade = false;
        for (int index = 0; index < _softwareRasterPassBindings.Count; index++)
        {
            ClusterSoftwareRasterPassBinding passBinding = _softwareRasterPassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            _materialInputIndex = checked((int)passBinding.Invocation);
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            BindSoftwareRasterResources(
                ref graph,
                ref pass,
                invocation,
                passBinding.Uniforms,
                passBinding.PolicyTarget);
        }

        for (int index = 0; index < _hardwareRasterPassBindings.Count; index++)
        {
            ClusterHardwareRasterPassBinding passBinding = _hardwareRasterPassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            _materialInputIndex = checked((int)passBinding.Invocation);
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            BindHardwareRasterResources(
                ref graph,
                ref pass,
                invocation,
                passBinding.Uniforms,
                passBinding.DispatchUniforms,
                passBinding.PolicyTarget);
        }

        for (int index = 0; index < _tessellationSeedPassBindings.Count; index++)
        {
            ClusterTessellationPassBinding passBinding = _tessellationSeedPassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            ClusterTessellationInvocation tessellation = invocation.Tessellation ??
                throw new InvalidOperationException("A recorded tessellation seed pass lost its material stages.");
            _materialInputIndex = checked((int)passBinding.Invocation);
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            BindTessellationResources(
                ref graph,
                ref pass,
                invocation,
                tessellation.SeedResources,
                passBinding.Uniforms,
                passBinding.PhaseOne,
                passBinding.NextLevelArgument,
                PipelineSync.ComputeShading,
                passBinding.PolicyTarget);
        }

        for (int index = 0; index < _tessellationSplitPassBindings.Count; index++)
        {
            ClusterTessellationPassBinding passBinding = _tessellationSplitPassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            ClusterTessellationInvocation tessellation = invocation.Tessellation ??
                throw new InvalidOperationException("A recorded tessellation split pass lost its material stages.");
            _materialInputIndex = checked((int)passBinding.Invocation);
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            BindTessellationResources(
                ref graph,
                ref pass,
                invocation,
                tessellation.SplitResources,
                passBinding.Uniforms,
                passBinding.PhaseOne,
                passBinding.NextLevelArgument,
                PipelineSync.ComputeShading,
                passBinding.PolicyTarget);
        }

        for (int index = 0; index < _tessellationDicePassBindings.Count; index++)
        {
            ClusterTessellationPassBinding passBinding = _tessellationDicePassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            ClusterTessellationInvocation tessellation = invocation.Tessellation ??
                throw new InvalidOperationException("A recorded tessellation dice pass lost its material stages.");
            ClusterRasterStageResources<ClusterTessellationCoreResource> diceResources =
                tessellation.DiceResources ?? throw new InvalidOperationException(
                    "A recorded tessellation dice pass has no compute dicing resources.");
            _materialInputIndex = checked((int)passBinding.Invocation);
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            BindTessellationResources(
                ref graph,
                ref pass,
                invocation,
                diceResources,
                passBinding.Uniforms,
                passBinding.PhaseOne,
                passBinding.NextLevelArgument,
                PipelineSync.ComputeShading,
                passBinding.PolicyTarget);
        }

        for (int index = 0; index < _tessellationHardwarePassBindings.Count; index++)
        {
            ClusterTessellationHardwarePassBinding passBinding =
                _tessellationHardwarePassBindings[index];
            ClusterRasterInvocation invocation =
                rasterStages.Invocations[checked((int)passBinding.Invocation)];
            ClusterTessellationInvocation tessellation = invocation.Tessellation ??
                throw new InvalidOperationException(
                    "A recorded tessellation hardware pass lost its material stages.");
            ClusterRasterStageResources<ClusterTessellationCoreResource> hardwareResources =
                tessellation.HardwareResources ?? throw new InvalidOperationException(
                    "A recorded tessellation hardware pass has no raster resources.");
            _materialInputIndex = checked((int)passBinding.Invocation);
            PassDefinition pass = graph.GetPass(passBinding.Pass);
            BindTessellationResources(
                ref graph,
                ref pass,
                invocation,
                hardwareResources,
                passBinding.Uniforms,
                passBinding.PhaseOne,
                0u,
                PipelineSync.AllShading,
                passBinding.PolicyTarget);
        }
    }

    private static bool BindInactiveStageResource(
        ref PassDefinition pass, in ClusterLinkedStageResource resource)
    {
        if (resource.ParameterLayout != SlangShaderSharp.ShaderVariableLayout.Null)
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
        GraphBufferId uniformBuffer,
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
                resource,
                materialBinding,
                sync);
        }
    }

    private void BindTessellationCoreResource(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterTessellationCoreResource resource,
        GraphBufferId uniformBuffer,
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
                    BufferContentScope.CountBoundedPrefix);
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
        GraphBufferId uniformBuffer,
        bool policyTarget)
    {
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource> stage =
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
                    uniformBuffer);
                continue;
            }
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                if (!policyTarget && BindNeutralRasterTargetResource(
                        ref graph,
                        ref pass,
                        resource))
                {
                    continue;
                }
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
                resource,
                materialBinding,
                PipelineSync.ComputeShading);
        }
    }

    private void BindHardwareRasterResources(
        ref RenderGraphEdit graph,
        ref PassDefinition pass,
        ClusterRasterInvocation invocation,
        GraphBufferId uniformBuffer,
        GraphBufferId dispatchUniformBuffer,
        bool policyTarget)
    {
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> stage =
            invocation.HardwareResources;
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
                    dispatchUniformBuffer);
                continue;
            }
            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                if (!policyTarget && BindNeutralRasterTargetResource(
                        ref graph,
                        ref pass,
                        resource))
                {
                    continue;
                }
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
            case ("RasterTargetInvocations", ResourceBindingType.BufferSrv):
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        _frameResources.NeutralPolicyView,
                        structureStride: 48,
                        label: "Cluster neutral raster target invocation"),
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
            case ClusterSoftwareRasterCoreResource.BinnedClusters:
                _ = pass.Bind(
                    graph.CreateBufferSrv(
                        frame.BinnedClusters,
                        structureStride: BinnedClusterStride,
                        label: "Cluster software raster binned clusters"),
                    PipelineSync.ComputeShading,
                    BufferContentScope.CountBoundedPrefix);
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
                    BufferContentScope.CountBoundedPrefix);
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
        if (!_pageHeapReaderPasses.Contains(pass.Id)) _pageHeapReaderPasses.Add(pass.Id);
    }

    private void BindShadeStages(
        ref RenderGraphEdit graph,
        ClusterShadeStageSet shadeStages)
    {
        if (_shadePasses.Length != shadeStages.Invocations.Count)
            throw new InvalidOperationException("The retained shade passes do not match the installed invocations.");

        _materialInputsAreShade = true;
        _shadeScalarOffsetsInput ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        _shadeScalarsInput ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        _shadeUniformInputs = new GraphBufferInput[shadeStages.Invocations.Count];

        for (int index = 0; index < shadeStages.Invocations.Count; index++)
        {
            ClusterShadeInvocation invocation = shadeStages.Invocations[index];
            GraphPassId passId = _shadePasses[index];
            PassDefinition pass = graph.GetPass(passId);
            pass.SetParameterBlock(invocation.Pipeline.ParameterLayout);

            _materialInputIndex = index;
            GraphBufferInput uniformBuffer = graph.CreateBufferInput(BufferUsages.Constant);
            _shadeUniformInputs[index] = uniformBuffer;

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
                        _shadeScalarsInput,
                        _shadeScalarOffsetsInput);
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
                    resource,
                    materialBinding,
                    PipelineSync.ComputeShading);
            }
        }
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
                    BufferContentScope.CountBoundedPrefix);
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
                if (!_pageHeapReaderPasses.Contains(pass.Id)) _pageHeapReaderPasses.Add(pass.Id);
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
        _materialInputs.Add(new MaterialInput(pass.Id, input, binding.MaterialSlot, resource.Name,
            _materialInputIndex, _materialInputsAreShade));
    }

    private ResourceBinding GetMaterialTextureSrv(
        ref RenderGraphFrame graph,
        GraphPassId pass,
        Material material,
        uint materialSlot,
        string name)
    {
        EngineTexture? asset = material.GetTexture(materialSlot);

        TextureSrv textureView;
        if (asset is null)
        {
            _ = _renderDevice.PrepareMissingTexture(ref graph, pass);
            textureView = _renderDevice.MissingTextureSrv;
        }
        else
        {
            _ = _renderDevice.PrepareTexture(ref graph, pass, asset);
            textureView = _renderDevice.GetTextureSrv(asset);
        }

        return ResourceBinding.SampledTexture(textureView);
    }

    private ResourceBinding GetMaterialTextureUav(
        ref RenderGraphFrame graph,
        GraphPassId pass,
        Material material,
        uint materialSlot,
        string name)
    {
        EngineTexture asset = material.GetTexture(materialSlot) ?? throw new InvalidDataException(
            $"Material '{material.Name}' slot '{name}' must contain a Texture for writable access.");
        RhiTexture textureResource = _renderDevice.PrepareTexture(ref graph, pass, asset);
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
        where T : DeviceResource
    {
        DeviceResource? value = material.GetDeviceResource(slot);
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


    private readonly List<MaterialInput> _materialInputs = [];
    private int _materialInputIndex;
    private bool _materialInputsAreShade;
    private GraphBufferInput _shadeScalarOffsetsInput = null!;
    private GraphBufferInput _shadeScalarsInput = null!;
    private GraphBufferInput[] _shadeUniformInputs = [];
    private uint[] _shadeScalarOffsets = [];
    private byte[] _shadeScalars = [];

    private readonly record struct MaterialInput(
        GraphPassId Pass, GraphParameterInput Input, uint MaterialSlot, string Name,
        int StageIndex, bool Shade);

    private void UpdateShadeInputs(ref RenderGraphFrame graph, ClusterShadeStageSet stages)
    {
        graph.SetBufferInput(_shadeScalarOffsetsInput, graph.Upload<uint>(
            _shadeScalarOffsets, BufferUsages.ShaderRead, "Cluster material scalar offsets"));
        graph.SetBufferInput(_shadeScalarsInput, graph.Upload<byte>(
            _shadeScalars, BufferUsages.ShaderRead, "Cluster material scalars"));
        Span<ClusterShadeUniforms> uniforms = stackalloc ClusterShadeUniforms[1];
        for (int index = 0; index < stages.Invocations.Count; index++)
        {
            uniforms[0] = new ClusterShadeUniforms
            {
                ScreenWidth = checked((uint)_frameResources.Width),
                ScreenHeight = checked((uint)_frameResources.Height),
                ShadingBin = stages.Invocations[index].BinIndex,
                ShadeBinCount = stages.BinCount,
                MaterialSlotCapacity = checked((uint)stages.Slots.Length),
            };
            graph.SetBufferInput(_shadeUniformInputs[index], graph.Upload<ClusterShadeUniforms>(
                uniforms, BufferUsages.Constant, "Cluster shade parameters"));
        }
    }

    private void UpdateMaterialInputs(ref RenderGraphFrame graph)
    {
        foreach (MaterialInput input in _materialInputs)
        {
            Material material = input.Shade
                ? _installedShadeStages!.Invocations[input.StageIndex].Material
                : _installedRasterStages!.Invocations[input.StageIndex].Material;
            DevelopmentCheck.Require(input.Input.Type != ResourceBindingType.None && Enum.IsDefined(input.Input.Type),
                "A material input requires a supported resource binding type.");
            ResourceBinding value = input.Input.Type switch
            {
                ResourceBindingType.TextureSrv => GetMaterialTextureSrv(ref graph, input.Pass, material, input.MaterialSlot, input.Name),
                ResourceBindingType.TextureUav => GetMaterialTextureUav(ref graph, input.Pass, material, input.MaterialSlot, input.Name),
                ResourceBindingType.BufferSrv => GetMaterialBufferSrv(material, input.MaterialSlot, input.Name),
                ResourceBindingType.BufferUav => GetMaterialBufferUav(material, input.MaterialSlot, input.Name),
                ResourceBindingType.ConstantBuffer => GetMaterialConstantBuffer(material, input.MaterialSlot, input.Name),
                ResourceBindingType.AccelerationStructure => GetMaterialAccelerationStructure(material, input.MaterialSlot, input.Name),
                _ => ResourceBinding.SampledWith(ResolveMaterialSampler(material, input.MaterialSlot, input.Name)),
            };
            graph.SetParameterInput(input.Input, value);
        }
    }
}
