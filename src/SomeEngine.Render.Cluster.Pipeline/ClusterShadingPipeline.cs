using SomeEngine.Render;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using System.Collections.Immutable;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;
using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Lighting;
using Buffer = SomeEngine.Graphics.Buffer;
using SomeEngine.Render.Materials;
using EngineTexture = SomeEngine.Resources.Texture;
using SlangShaderSharp;
using System.Runtime.CompilerServices;
using MeshOptimizer;
using SomeEngine.Resources.Geometry;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;
public sealed class ClusterShadingPipeline : IRenderMaterialResources
{
    internal ClusterGeometryPipeline _geometry = null !;
    internal ClusterGraphInstaller _installer = null !;
    public ClusterShadingPipeline(RenderDevice renderDevice, ClusterMaterialAssignments materialAssignments, IGraphResourceProvider? featureResources = null)
    {
        _renderDevice = renderDevice;
        _materialAssignments = materialAssignments;
        materialAssignments.Attach(this);
        _stageResourceProvider = featureResources;
    }

    internal void Attach(ClusterGeometryPipeline geometry, ClusterGraphInstaller installer)
    {
        _geometry = geometry;
        _installer = installer;
    }

    internal readonly RenderDevice _renderDevice;
    internal readonly ClusterMaterialAssignments _materialAssignments;
    internal readonly IGraphResourceProvider? _stageResourceProvider;
    private RenderMaterialEntry[] _materialEntries = [];
    public IReadOnlyList<RenderMaterialEntry> Materials => _materialEntries;
    public IReadOnlyList<GraphPassId> MaterialPasses =>
        _installer.GraphData.ShadeGroups.Values.Select(group => group.Pass).ToArray();
    public uint MaterialCapacity => _materialAssignments.Current.MaterialAddressCapacity;
    public GraphBufferId DispatchArguments => _installer.GraphData.Resources.ShadeIndirectArgs;
    public uint DispatchStride => checked((uint)ClusterIndirectLayout.DispatchBytes);
    public uint DebugMode => _geometry._options.DebugMode;

    public void ReplaceShader(Shader previous, Shader next) => _materialAssignments.MarkShaderChanged(previous, next);

    public GraphBufferSlice GetUniforms(in RenderMaterialEntry material)
    {
        uint bin = material.BinIndex;
        return _installer.GraphData.ShadeGroups.Values.First(group => group.Stage.BinIndex == bin).Uniforms;
    }

    public void BindResource(ref RenderGraphEdit edit, ref PassDefinition pass, in ShaderParameterResource resource, in RenderMaterialEntry material, GraphBufferSlice uniforms)
    {
        if (BindInactiveStageResource(ref pass, resource))
            return;
        if (_geometry.BindDeformRead(ref edit, ref pass, resource, PipelineSync.ComputeShading))
            return;
        ClusterShadeCoreResource core = ClusterShadeCoreResources.Resolve(resource.Name, resource.Type);
        if (core != ClusterShadeCoreResource.None)
        {
            BindShadeCoreResource(ref edit, ref pass, core, uniforms, _installer.GraphData.ShadeScalars, _installer.GraphData.ShadeScalarOffsets);
            return;
        }

        if (_stageResourceProvider is not null && _stageResourceProvider.TryResolve(resource.Name, resource.Type, out GraphResourceBinder binder))
        {
            binder(ref edit, ref pass, PipelineSync.ComputeShading);
            return;
        }

        string name = resource.Name;
        MaterialPropertyDefinition property = material.Material.Properties.First(value => value.Name == name);
        var binding = new ClusterMaterialStageBinding(property.Index, resource.Type, true);
        BindStageMaterialResource(ref edit, ref pass, resource, binding, material.Material, PipelineSync.ComputeShading);
    }

    public void ReadTextures(ref RenderGraphEdit edit, ref PassDefinition pass, ImmutableArray<RenderMaterialTextureRead> textures, PipelineSync sync)
    {
        foreach (RenderMaterialTextureRead texture in textures)
        {
            MaterialInput input = GetMaterialInput(ref edit, texture.Material, texture.Property, texture.Material.Properties[checked((int)texture.Property)].Name, ResourceBindingType.TextureSrv);
            pass.Read(input.Input, sync);
        }
    }

    internal void UpdateMaterialProjection(ClusterMaterialAssignmentSet assignments, ClusterShadeStageSet stages)
    {
        if (MaterialProjectionMatches(assignments, stages)) return;
        var materials = new List<RenderMaterialEntry>();
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        foreach (int address in assignments.ActiveOffsets.Order())
        {
            assignments.TryGetEntry(address, out Material material, out _);
            stages.BinIndices.CopyTo(bytes, address * sizeof(uint));
            uint bin = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            ClusterShadeDispatch stage = stages.GetBin(bin);
            var textures = ImmutableArray.CreateBuilder<RenderMaterialTextureRead>();
            foreach (MaterialPropertyDefinition property in material.Properties)
                if (property.ValueType == typeof(EngineTexture) || material.GetTexture(property.Index)is not null)
                    textures.Add(new RenderMaterialTextureRead(material, property.Index, material.GetTexture(property.Index)));
            materials.Add(new RenderMaterialEntry(address, bin, material, (uint)stage.VisibilityDepth, bin, textures.ToImmutable()));
        }

        _materialEntries = materials.ToArray();
    }

    private bool MaterialProjectionMatches(ClusterMaterialAssignmentSet assignments, ClusterShadeStageSet stages)
    {
        if ((uint)_materialEntries.Length != assignments.MaterialAddressCount) return false;
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        foreach (RenderMaterialEntry entry in _materialEntries)
        {
            if (!assignments.TryGetEntry(entry.Address, out Material material, out _)) return false;
            stages.BinIndices.CopyTo(bytes, entry.Address * sizeof(uint));
            uint bin = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (entry.BinIndex != bin || entry.ResourceGroup != bin || !ReferenceEquals(entry.Material, material) ||
                entry.VisibilityDepth != (uint)stages.GetBin(bin).VisibilityDepth) return false;
            int textureIndex = 0;
            foreach (MaterialPropertyDefinition property in material.Properties)
            {
                EngineTexture? texture = material.GetTexture(property.Index);
                if (property.ValueType != typeof(EngineTexture) && texture is null) continue;
                if (textureIndex == entry.TextureReads.Length) return false;
                RenderMaterialTextureRead read = entry.TextureReads[textureIndex++];
                if (!ReferenceEquals(read.Material, material) || read.Property != property.Index || !ReferenceEquals(read.Texture, texture)) return false;
            }
            if (textureIndex != entry.TextureReads.Length) return false;
        }
        return true;
    }

    internal ClusterRasterStages? _rasterStages;
    internal ClusterShadeStages? _shadeStages;
    internal readonly record struct MaterialBuffer(GraphBufferId Id, int References, bool RegisteredHere);
    internal sealed class RasterMaterialResources(ClusterGeometryPipeline pipeline, IGraphResourceProvider? features) : IGraphResourceProvider
    {
        public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
        {
            if (type == ResourceBindingType.BufferSrv)
            {
                if (name == "MaterialScalarRegion")
                {
                    binder = pipeline._shading.BindRasterMaterialScalars;
                    return true;
                }

                if (name == "MaterialScalarOffsets")
                {
                    binder = pipeline._shading.BindRasterMaterialOffsets;
                    return true;
                }
            }

            if (features is not null)
                return features.TryResolve(name, type, out binder);
            binder = null !;
            return false;
        }
    }

    private bool BindRasterInputResource(ref RenderGraphEdit graph, ref PassDefinition pass,
        in ShaderParameterResource resource, ClusterGeometryPipeline.DeformDrawData draw, PipelineSync sync)
    {
        ClusterGraphInstaller.FrameResources frame = _geometry._installer.GraphData.Resources;
        GraphBufferId buffer;
        uint stride;
        switch (resource.Name)
        {
            case "RasterSpillClusters": buffer = frame.RasterSpillClusters; stride = sizeof(uint); break;
            case "RasterInputBinIndices": buffer = frame.RasterBinIndices; stride = sizeof(uint); break;
            case "RasterInputDrawArgs": buffer = draw.Count; stride = 0; break;
            case "RasterInputReadOffset0": buffer = draw.Previous0; stride = 0; break;
            case "RasterInputReadOffset1": buffer = draw.Previous1; stride = 0; break;
            case "RasterInputReadOffset2": buffer = draw.Previous2; stride = 0; break;
            default: return false;
        }
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(buffer, structureStride: stride),
            sync, resource.Name == "RasterSpillClusters" ? BufferContentScope.CountBoundedRanges : BufferContentScope.FullRange));
        return true;
    }

    internal void BindRasterMaterialScalars(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.ShadeScalars), sync);
        ReadIndexedMaterialTextures(ref graph, ref pass, _geometry._rasterInputs!.TextureReads, sync);
    }

    internal void BindRasterMaterialOffsets(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) => _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.ShadeScalarOffsets, structureStride: sizeof(uint)), sync);
    internal (ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer> Buffers, ImmutableDictionary<EngineTexture, MaterialTexture> Textures) UpdateMaterialResources(ref RenderGraphEdit edit, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        HashSet<GraphParameterInput> inputs = edit.GetParameterInputs().ToHashSet();
        _installer.GraphData.MaterialInputs = _installer.GraphData.MaterialInputs.RemoveRange(_installer.GraphData.MaterialInputs.Where(pair => !inputs.Contains(pair.Value.Input)).Select(static pair => pair.Key));
        // The installed shader bindings already retain the graph's resource references.
        // Count only changed bins; no copy of material properties is needed for removals.
        var changes = new Dictionary<SomeEngine.Graphics.Buffer, int>();
        var resources = new HashSet<SomeEngine.Graphics.Buffer>();
        foreach (uint bin in rasterStages.ChangedBins)
        {
            if (_installer._installedRasterStages is not null && _installer._installedRasterStages.TryGetBin(bin, out var before))
                CountBuffers(before.CommandBindings, -1);
            if (rasterStages.TryGetBin(bin, out var after))
                CountBuffers(after.CommandBindings, 1);
        }

        foreach (uint bin in shadeStages.ChangedBins)
        {
            if (_installer._installedShadeStages is not null && _installer._installedShadeStages.TryGetBin(bin, out var before))
            {
                CountBuffers(before.CommandBindings, -1);
            }

            if (shadeStages.TryGetBin(bin, out var after))
            {
                CountBuffers(after.CommandBindings, 1);
            }
        }

        void CountBuffers(ClusterCommandBindingKey bindings, int change)
        {
            resources.Clear();
            bindings.CollectBuffers(resources);
            foreach (SomeEngine.Graphics.Buffer buffer in resources)
                changes[buffer] = changes.GetValueOrDefault(buffer) + change;
        }

        var buffers = _installer.GraphData.MaterialBuffers;
        foreach ((SomeEngine.Graphics.Buffer buffer, int change)in changes)
        {
            if (change == 0)
                continue;
            if (buffers.TryGetValue(buffer, out MaterialBuffer current))
            {
                int references = checked(current.References + change);
                if (references != 0)
                    buffers = buffers.SetItem(buffer, current with { References = references });
                else
                {
                    if (current.RegisteredHere)
                        edit.Remove(current.Id);
                    buffers = buffers.Remove(buffer);
                }
            }
            else
            {
                bool registeredHere = !_installer._graph.TryGetBufferId(buffer, out GraphBufferId id);
                if (registeredHere)
                    id = edit.RegisterExternalBuffer(buffer);
                buffers = buffers.Add(buffer, new MaterialBuffer(id, change, registeredHere));
            }
        }

        return (buffers, UpdateMaterialTextures(ref edit));
    }

    internal readonly Dictionary<SamplerDesc, Sampler> _materialSamplers = [];
    internal readonly Dictionary<long, MaterialBindingVersions> _materialBindingVersions = [];
    internal sealed record MaterialBindingVersions(Shader? Shader, bool HasLayer, ulong[] Properties)
    {
        internal bool Matches(Material material, ClusterRasterStages raster, ClusterShadeStages shade)
        {
            if (!ReferenceEquals(Shader, material.Shader) || HasLayer != material.Layer.HasValue || Properties.Length != material.PropertyCount)
                return false;
            // Stage resolution already retains the complete resource-property sets.
            // Read only those revisions; scalar-only edits need no full-property scan.
            return raster.ResourcePropertiesMatch(material, Properties) && shade.ResourcePropertiesMatch(material, Properties);
        }

        internal bool MatchesDepthInputs(Material material, HashSet<uint> properties)
        {
            foreach (uint property in properties)
                if (material.GetPropertyRevision(property) != Properties[property])
                    return false;
            return true;
        }
    }

    internal bool CollectMaterialChanges(ClusterMaterialAssignmentSet assignments, bool topologyChanged)
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

            bool resourcesMatch = _materialBindingVersions.TryGetValue(id, out var versions) && versions.Matches(material, _rasterStages!, _shadeStages!);
            resourcesChanged |= !resourcesMatch;
            if (topologyChanged || !resourcesMatch || !_rasterStages!.TryGetMaterialDepthInputs(id, out var properties) || !versions!.MatchesDepthInputs(material, properties))
                _rasterChangedMaterials.Add(id);
        }

        return resourcesChanged;
    }

    internal static bool BindInactiveStageResource(ref PassDefinition pass, in ShaderParameterResource resource)
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

    internal void BindTessellationResources(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterRasterDispatch dispatchData, ClusterRasterStageResources<ClusterTessellationCoreResource> stage, GraphBufferSlice uniformBuffer, bool phaseOne, uint nextLevelArgument, PipelineSync sync, bool policyTarget, ClusterGeometryPipeline.DeformDrawData draw = default, bool layerTiles = false)
    {
        ReadOnlySpan<ShaderParameterResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ShaderParameterResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (BindRasterInputResource(ref graph, ref pass, resource, draw, sync)) continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException($"Cluster tessellation resource arrays are not supported: " + $"'{resource.Name}[{resource.Element}]'.");
            }

            ClusterTessellationCoreResource core = stage.CoreResources[resourceIndex];
            if (!layerTiles && (core is ClusterTessellationCoreResource.LayerWindows or ClusterTessellationCoreResource.LayerParameters))
            {
                pass.Bind(GraphParameterResourceBinding.Null(resource.Type));
                continue;
            }
            if (_geometry.BindDeformRead(ref graph, ref pass, resource, sync))
                continue;
            if (core != ClusterTessellationCoreResource.None)
            {
                BindTessellationCoreResource(ref graph, ref pass, core, uniformBuffer, phaseOne, nextLevelArgument, sync, policyTarget, layerTiles);
                continue;
            }

            if (stage.FeatureBinders[resourceIndex] is { } featureBinder)
            {
                if (!policyTarget && BindNeutralRasterTargetResource(ref graph, ref pass, resource))
                {
                    continue;
                }

                featureBinder(ref graph, ref pass, sync);
                continue;
            }

            ClusterMaterialStageBinding materialBinding = stage.MaterialBindings[resourceIndex];
            if (!materialBinding.IsMaterialResource)
            {
                throw new InvalidDataException($"Final tessellation entry requires unknown linked resource '{resource.Name}'.");
            }

            BindStageMaterialResource(ref graph, ref pass, resource, materialBinding, dispatchData.Material, sync);
        }
    }

    internal void BindTessellationCoreResource(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterTessellationCoreResource resource, GraphBufferSlice uniformBuffer, bool phaseOne, uint nextLevelArgument, PipelineSync sync, bool policyTarget, bool layerTiles = false)
    {
        ClusterGraphInstaller.FrameResources frame = ClusterGraphInstaller.GetRasterFrameResources(_installer.GraphData.Resources, policyTarget, layerTiles);
        switch (resource)
        {
            case ClusterTessellationCoreResource.LayerWindows:
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(graph.CreateBufferSrv(frame.LayerTileWindows, structureStride: 16), sync, BufferContentScope.CountBoundedRanges));
                return;
            case ClusterTessellationCoreResource.LayerParameters:
                GraphBufferSlice layerParameters = _installer.UploadUniform(ref graph,
                    new LayerCullParameters(_installer.GraphData.LayerTileSize, _installer.GraphData.LayerHardwareWindowTiles),
                    "Layer tessellation tile dimensions");
                pass.Bind(graph.CreateBufferCbv(layerParameters.Buffer, layerParameters.Range), sync);
                return;
            case ClusterTessellationCoreResource.InstanceData:
                _ = pass.Bind(graph.CreateBufferSrv(frame.InstanceData, label: "Cluster tessellation instance data"), sync);
                return;
            case ClusterTessellationCoreResource.InstanceProperties:
                _ = pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange, "Cluster tessellation instance properties"), sync);
                return;
            case ClusterTessellationCoreResource.Uniforms:
                _ = pass.Bind(uniformBuffer, sync);
                return;
            case ClusterTessellationCoreResource.Views:
                _ = pass.Bind(graph.CreateBufferSrv(frame.PackedViews, structureStride: ClusterViewUniforms.SizeInBytes, label: "Cluster tessellation views"), sync);
                return;
            case ClusterTessellationCoreResource.PolicyViews:
                if (policyTarget)
                    _geometry.BindPolicyClusterViews(ref graph, ref pass, sync, true);
                else
                    _ = pass.Bind(graph.CreateBufferSrv(frame.PackedViews, structureStride: ClusterViewUniforms.SizeInBytes,
                        label: "Cluster tessellation active view metadata"), sync);
                return;
            case ClusterTessellationCoreResource.PageHeap:
                BindPageHeapDescriptor(ref graph, ref pass, "Cluster tessellation PageHeap", sync);
                return;
            case ClusterTessellationCoreResource.VisibleClusters:
                _ = pass.Bind(graph.CreateBufferSrv(frame.VisibleClusters, structureStride: ClusterGraphInstaller.VisibleClusterStride, label: "Cluster tessellation visible clusters"), sync, BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.BinnedClusters:
                _ = pass.Bind(graph.CreateBufferUav(frame.BinnedClusters, structureStride: ClusterGraphInstaller.BinnedClusterStride, label: "Cluster tessellation binned clusters"), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedRanges);
                return;
            case ClusterTessellationCoreResource.BinMetadata:
                _ = pass.Bind(graph.CreateBufferSrv(frame.RasterBinMeta, structureStride: ClusterGraphInstaller.RasterBinStride, label: "Cluster tessellation bin metadata"), sync);
                return;
            case ClusterTessellationCoreResource.RasterBinIndices:
                _ = pass.Bind(graph.CreateBufferSrv(frame.RasterBinIndices, structureStride: sizeof(uint), label: "Cluster tessellation raster material slots"), sync);
                return;
            case ClusterTessellationCoreResource.HiZ:
                _ = pass.Bind(graph.CreateTextureSrv(phaseOne ? frame.PreviousHiZ : frame.CurrentHiZ, format: Format.R32Float, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster tessellation HiZ"), sync);
                return;
            case ClusterTessellationCoreResource.Depth:
                _ = pass.Bind(graph.CreateTextureSrv(frame.Depth, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth), Format.D32Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Cluster tessellation depth"), sync);
                return;
            case ClusterTessellationCoreResource.Visibility:
                _ = pass.Bind(graph.CreateTextureUav(frame.VisBuffer, format: Format.R32UInt, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster tessellation visibility"), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.WinnerImage:
                BindVisibilityWinnerImage(ref graph, ref pass, sync);
                return;
            case ClusterTessellationCoreResource.WinnerBuffer:
                BindVisibilityWinnerBuffer(ref graph, ref pass, sync);
                return;
            case ClusterTessellationCoreResource.WorkStatus:
                _geometry.BindWorkStatus(ref graph, ref pass);
                return;
            case ClusterTessellationCoreResource.Counters:
                _ = pass.Bind(graph.CreateBufferUav(frame.TessellationCounters, label: "Cluster tessellation counters"), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.WorkPatches0:
                _ = pass.Bind(graph.CreateBufferUav(frame.TessellationWorkPatches, structureStride: ClusterGraphInstaller.TessellationWorkPatchStride, label: "Cluster tessellation even-level work patches"), sync, (nextLevelArgument & 1u) == 0u ? GraphAccessMode.Write : GraphAccessMode.Read, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.WorkPatches1:
                _ = pass.Bind(graph.CreateBufferUav(frame.TessellationWorkPatchesAlternate, structureStride: ClusterGraphInstaller.TessellationWorkPatchStride, label: "Cluster tessellation odd-level work patches"), sync, (nextLevelArgument & 1u) == 0u ? GraphAccessMode.Read : GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.LevelCounts:
                _ = pass.Bind(graph.CreateBufferUav(frame.TessellationLevelCounts, structureStride: sizeof(uint), label: "Cluster tessellation level counts"), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.NextLevelArgs:
                _ = pass.Bind(graph.CreateBufferUav(frame.TessellationLevelDispatchArgs[checked((int)nextLevelArgument)], label: $"Cluster tessellation level {nextLevelArgument} dispatch"), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
                return;
            case ClusterTessellationCoreResource.FinalPatches:
                _ = pass.Bind(graph.CreateBufferUav(frame.TessellationFinalPatches, structureStride: ClusterGraphInstaller.TessellationFinalPatchStride, label: "Cluster tessellation final patches"), sync, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.RasterPatches:
                _ = pass.Bind(graph.CreateBufferSrv(frame.TessellationFinalPatches, structureStride: ClusterGraphInstaller.TessellationFinalPatchStride, label: "Cluster tessellation raster patches"), sync, BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.GroupedPatchIndices:
                _ = pass.Bind(graph.CreateBufferSrv(frame.TessellationWorkPatches, structureStride: sizeof(uint), label: "Cluster tessellation grouped patch indices"), sync, BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterTessellationCoreResource.BinCounts:
                _ = pass.Bind(graph.CreateBufferSrv(frame.TessellationBinCounts, structureStride: sizeof(uint), label: "Cluster tessellation bin counts"), sync);
                return;
            case ClusterTessellationCoreResource.BinOffsets:
                _ = pass.Bind(graph.CreateBufferSrv(frame.TessellationBinOffsets, structureStride: sizeof(uint), label: "Cluster tessellation bin offsets"), sync);
                return;
            default:
                throw new InvalidOperationException($"Unknown Cluster tessellation core resource {resource}.");
        }
    }

    internal void BindSoftwareRasterResources(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterRasterDispatch dispatchData, GraphBufferSlice uniformBuffer, bool policyTarget, ClusterGeometryPipeline.DeformDrawData draw = default, bool spilled = false)
    {
        ClusterRasterStageResources<ClusterSoftwareRasterCoreResource> stage = spilled
            ? (policyTarget ? dispatchData.SpillTargetStages?.SoftwareResources : null) ?? dispatchData.SpillStages.SoftwareResources!
            : (policyTarget ? dispatchData.TargetStages?.SoftwareResources : null) ?? dispatchData.SoftwareResources!;
        ReadOnlySpan<ShaderParameterResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ShaderParameterResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (BindRasterInputResource(ref graph, ref pass, resource, draw, PipelineSync.ComputeShading)) continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException($"Cluster software-raster resource arrays are not supported: " + $"'{resource.Name}[{resource.Element}]'.");
            }

            ClusterSoftwareRasterCoreResource core = stage.CoreResources[resourceIndex];
            if (_geometry.BindDeformRead(ref graph, ref pass, resource, PipelineSync.ComputeShading))
                continue;
            if (core == ClusterSoftwareRasterCoreResource.PolicyViews)
            {
                _geometry.BindPolicyClusterViews(ref graph, ref pass, PipelineSync.ComputeShading, policyTarget);
                continue;
            }

            if (core != ClusterSoftwareRasterCoreResource.None)
            {
                BindSoftwareRasterCoreResource(ref graph, ref pass, core, uniformBuffer, policyTarget);
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
                throw new InvalidDataException($"Final software-raster entry requires unknown linked resource '{resource.Name}'.");
            }

            BindStageMaterialResource(ref graph, ref pass, resource, materialBinding, dispatchData.Material, PipelineSync.ComputeShading);
        }
    }

    internal void BindHardwareRasterResources(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterRasterDispatch dispatchData, GraphBufferSlice uniformBuffer, GraphBufferSlice dispatchUniformBuffer, bool policyTarget, ClusterGeometryPipeline.DeformDrawData draw = default, bool spilled = false)
    {
        ClusterRasterStageResources<ClusterHardwareRasterCoreResource> stage = spilled
            ? (policyTarget ? dispatchData.SpillTargetStages?.HardwareResources : null) ?? dispatchData.SpillStages.HardwareResources!
            : (policyTarget ? dispatchData.TargetStages?.HardwareResources : null) ?? dispatchData.HardwareResources;
        ReadOnlySpan<ShaderParameterResource> resources = stage.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ShaderParameterResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (BindRasterInputResource(ref graph, ref pass, resource, draw, PipelineSync.AllShading)) continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException($"Cluster hardware-raster resource arrays are not supported: " + $"'{resource.Name}[{resource.Element}]'.");
            }

            ClusterHardwareRasterCoreResource core = stage.CoreResources[resourceIndex];
            if (_geometry.BindDeformRead(ref graph, ref pass, resource, PipelineSync.AllShading))
                continue;
            if (core == ClusterHardwareRasterCoreResource.PolicyViews)
            {
                _geometry.BindPolicyClusterViews(ref graph, ref pass, PipelineSync.AllShading, policyTarget);
                continue;
            }

            if (core != ClusterHardwareRasterCoreResource.None)
            {
                BindHardwareRasterCoreResource(ref graph, ref pass, core, uniformBuffer, dispatchUniformBuffer, policyTarget);
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
                throw new InvalidDataException($"Final hardware-raster entry requires unknown linked resource '{resource.Name}'.");
            }

            BindStageMaterialResource(ref graph, ref pass, resource, materialBinding, dispatchData.Material, PipelineSync.AllShading);
        }
    }

    internal bool BindNeutralRasterTargetResource(ref RenderGraphEdit graph, ref PassDefinition pass, in ShaderParameterResource resource)
    {
        switch (resource.Name, resource.Type)
        {
            case ("RasterTargetViews", ResourceBindingType.BufferSrv):
                _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.Resources.NeutralPolicyView, structureStride: RenderRasterContext.TargetViewSizeInBytes, label: "Cluster neutral raster target view"), PipelineSync.AllShading);
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
                _ = pass.Bind(graph.CreateBufferSrv(_installer.GraphData.Resources.NeutralPolicyView, structureStride: stride, label: $"Cluster neutral {resource.Name}"), PipelineSync.AllShading);
                return true;
            case ("RasterTargetReceiverMasks", ResourceBindingType.TextureSrv):
                _ = pass.Bind(graph.CreateTextureSrv(_installer.GraphData.Resources.NeutralPolicyOutput, format: Format.R32UInt, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D), PipelineSync.AllShading);
                return true;
            case ("RasterTargetCurrentHiZ", ResourceBindingType.TextureSrv):
            case ("RasterTargetPreviousHiZ", ResourceBindingType.TextureSrv):
                _ = pass.Bind(graph.CreateTextureSrv(_installer.GraphData.Resources.PreviousHiZ, format: Format.R32Float, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: $"Cluster neutral {resource.Name}"), PipelineSync.AllShading);
                return true;
            case ("RasterTargetOutput", ResourceBindingType.TextureUav):
            case ("RasterTargetStaticOutput", ResourceBindingType.TextureUav):
                pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.TextureUav));
                return true;
            default:
                return false;
        }
    }

    internal void BindSoftwareRasterCoreResource(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterSoftwareRasterCoreResource resource, GraphBufferSlice uniformBuffer, bool policyTarget)
    {
        ClusterGraphInstaller.FrameResources frame = ClusterGraphInstaller.GetRasterFrameResources(_installer.GraphData.Resources, policyTarget);
        switch (resource)
        {
            case ClusterSoftwareRasterCoreResource.InstanceData:
                _ = pass.Bind(graph.CreateBufferSrv(frame.InstanceData, label: "Cluster software raster instance data"), PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.InstanceProperties:
                _ = pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange, "Cluster software raster instance properties"), PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Uniforms:
                _ = pass.Bind(uniformBuffer, PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Views:
                _ = pass.Bind(graph.CreateBufferSrv(frame.PackedViews, structureStride: ClusterViewUniforms.SizeInBytes, label: "Cluster software raster views"), PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.PolicyViews:
                _geometry.BindPolicyClusterViews(ref graph, ref pass, PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.PageHeap:
                BindPageHeapDescriptor(ref graph, ref pass, "Cluster software raster PageHeap", PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.VisibleClusters:
                _ = pass.Bind(graph.CreateBufferSrv(frame.VisibleClusters, structureStride: ClusterGraphInstaller.VisibleClusterStride, label: "Cluster software raster visible clusters"), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterSoftwareRasterCoreResource.BinnedClusters:
                _ = pass.Bind(graph.CreateBufferSrv(frame.BinnedClusters, structureStride: ClusterGraphInstaller.BinnedClusterStride, label: "Cluster software raster binned clusters"), PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges);
                return;
            case ClusterSoftwareRasterCoreResource.BinMetadata:
                _ = pass.Bind(graph.CreateBufferSrv(frame.RasterBinMeta, structureStride: ClusterGraphInstaller.RasterBinStride, label: "Cluster software raster bin metadata"), PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Depth:
                _ = pass.Bind(graph.CreateTextureSrv(frame.Depth, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth), Format.D32Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Cluster software raster depth"), PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Visibility:
                _ = pass.Bind(graph.CreateTextureUav(frame.VisBuffer, format: Format.R32UInt, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster software raster visibility"), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial);
                return;
            case ClusterSoftwareRasterCoreResource.WinnerImage:
                BindVisibilityWinnerImage(ref graph, ref pass, PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.WinnerBuffer:
                BindVisibilityWinnerBuffer(ref graph, ref pass, PipelineSync.ComputeShading);
                return;
            case ClusterSoftwareRasterCoreResource.Diagnostics:
                _ = pass.Bind(graph.CreateBufferUav(frame.SoftwareDebug, label: "Cluster software raster diagnostics"), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial);
                _ = pass.Read(frame.SoftwareDebug, new BufferRange(0, sizeof(uint)), PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
                return;
            default:
                throw new InvalidOperationException($"Unknown Cluster software-raster core resource {resource}.");
        }
    }

    internal void BindHardwareRasterCoreResource(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterHardwareRasterCoreResource resource, GraphBufferSlice uniformBuffer, GraphBufferSlice dispatchUniformBuffer, bool policyTarget)
    {
        ClusterGraphInstaller.FrameResources frame = ClusterGraphInstaller.GetRasterFrameResources(_installer.GraphData.Resources, policyTarget);
        switch (resource)
        {
            case ClusterHardwareRasterCoreResource.InstanceData:
                _ = pass.Bind(graph.CreateBufferSrv(frame.InstanceData, label: "Cluster hardware raster instance data"), PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.InstanceProperties:
                _ = pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange, "Cluster hardware raster instance properties"), PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.Uniforms:
                _ = pass.Bind(uniformBuffer, PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.Views:
                _ = pass.Bind(graph.CreateBufferSrv(frame.PackedViews, structureStride: ClusterViewUniforms.SizeInBytes, label: "Cluster hardware raster views"), PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.PolicyViews:
                _geometry.BindPolicyClusterViews(ref graph, ref pass, PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.DispatchUniforms:
                _ = pass.Bind(graph.CreateBufferCbv(dispatchUniformBuffer.Buffer, dispatchUniformBuffer.Range, label: "Cluster hardware raster dispatch uniforms"), PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.PageHeap:
                BindPageHeapDescriptor(ref graph, ref pass, "Cluster hardware raster PageHeap", PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.BinnedClusters:
                _ = pass.Bind(graph.CreateBufferSrv(frame.BinnedClusters, structureStride: ClusterGraphInstaller.BinnedClusterStride, label: "Cluster hardware raster binned clusters"), PipelineSync.AllShading, BufferContentScope.CountBoundedRanges);
                return;
            case ClusterHardwareRasterCoreResource.VisibleClusters:
                _ = pass.Bind(graph.CreateBufferSrv(frame.VisibleClusters, structureStride: ClusterGraphInstaller.VisibleClusterStride, label: "Cluster hardware raster visible clusters"), PipelineSync.AllShading, BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterHardwareRasterCoreResource.DrawMetadata:
                _ = pass.Bind(graph.CreateBufferSrv(frame.HardwareIndirectArgs, label: "Cluster hardware raster draw metadata"), PipelineSync.AllShading);
                return;
            case ClusterHardwareRasterCoreResource.WinnerImage:
                BindVisibilityWinnerImage(ref graph, ref pass, PipelineSync.PixelShading);
                return;
            case ClusterHardwareRasterCoreResource.WinnerBuffer:
                BindVisibilityWinnerBuffer(ref graph, ref pass, PipelineSync.PixelShading);
                return;
            default:
                throw new InvalidOperationException($"Unknown Cluster hardware-raster core resource {resource}.");
        }
    }

    internal void BindVisibilityWinnerImage(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        if (_geometry._visibilityMode != ClusterVisibilityMode.ImageAtomic64 || _installer.GraphData.Resources.VisibilityWinnerImage == default)
        {
            throw new InvalidOperationException("An image-A64 material entry was selected without an image winner resource.");
        }

        _ = pass.Bind(graph.CreateTextureUav(_installer.GraphData.Resources.VisibilityWinnerImage, format: Format.R64UInt, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster visibility winner image"), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial, unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable);
    }

    internal void BindVisibilityWinnerBuffer(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        if (_geometry._visibilityMode is not (ClusterVisibilityMode.BufferAtomic64 or ClusterVisibilityMode.NonAtomic) || _installer.GraphData.Resources.VisibilityWinnerBuffer == default)
        {
            throw new InvalidOperationException("A buffer visibility material entry was selected without its visibility resource.");
        }

        _ = pass.Bind(graph.CreateBufferUav(_installer.GraphData.Resources.VisibilityWinnerBuffer, label: "Cluster visibility buffer"), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            unorderedAccessHazards: _geometry._visibilityMode == ClusterVisibilityMode.NonAtomic
                ? UnorderedAccessHazardMode.Ordered : UnorderedAccessHazardMode.AtomicMergeable);
    }

    internal void BindPageHeapDescriptor(ref RenderGraphEdit graph, ref PassDefinition pass, string label, PipelineSync sync)
    {
        ClusterGraphInstaller.FrameResources frame = _installer.GraphData.Resources;
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(graph.CreateBufferSrv(frame.PageHeap, label: label)));
    }

    internal GraphBufferSlice BindShadeResources(ref RenderGraphEdit graph, GraphPassId passId, ClusterShadeDispatch dispatchData, ClusterShadeStageSet stages)
    {
        _installer.GraphData.ShadeScalarOffsets ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        _installer.GraphData.ShadeScalars ??= graph.CreateBufferInput(BufferUsages.ShaderRead);
        PassDefinition pass = graph.GetPass(passId);
        pass.SetParameterBlock(dispatchData.Pipeline.ParameterLayout);
        GraphBufferSlice uniformBuffer = _installer.UploadUniform(ref graph, ShadeUniforms(dispatchData.BinIndex, stages), "Cluster shade parameters");
        ReadOnlySpan<ShaderParameterResource> resources = dispatchData.Resources.Resources;
        for (int resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
        {
            ref readonly ShaderParameterResource resource = ref resources[resourceIndex];
            if (BindInactiveStageResource(ref pass, resource))
                continue;
            if (resource.Element != 0)
            {
                throw new NotSupportedException($"Cluster material resource arrays are not supported: '{resource.Name}[{resource.Element}]'.");
            }

            if (_geometry.BindDeformRead(ref graph, ref pass, resource, PipelineSync.ComputeShading))
                continue;
            ClusterShadeCoreResource coreResource = dispatchData.CoreResources[resourceIndex];
            if (coreResource != ClusterShadeCoreResource.None)
            {
                BindShadeCoreResource(ref graph, ref pass, coreResource, uniformBuffer, _installer.GraphData.ShadeScalars, _installer.GraphData.ShadeScalarOffsets);
                continue;
            }

            if (dispatchData.FeatureResourceBinders[resourceIndex] is { } featureBinder)
            {
                featureBinder(ref graph, ref pass, PipelineSync.ComputeShading);
                continue;
            }

            ClusterMaterialStageBinding materialBinding = dispatchData.MaterialBindings[resourceIndex];
            if (!materialBinding.IsMaterialResource)
            {
                throw new InvalidDataException($"Final shade entry requires unknown linked resource '{resource.Name}'.");
            }

            BindStageMaterialResource(ref graph, ref pass, resource, materialBinding, dispatchData.Material, PipelineSync.ComputeShading);
        }

        return uniformBuffer;
    }

    internal void BindShadeCoreResource(ref RenderGraphEdit graph, ref PassDefinition pass, ClusterShadeCoreResource resource, GraphBufferSlice uniformBuffer, GraphBufferInput scalarBuffer, GraphBufferInput scalarOffsetBuffer)
    {
        ClusterGraphInstaller.FrameResources frame = _installer.GraphData.Resources;
        switch (resource)
        {
            case ClusterShadeCoreResource.BinIndices:
                BindReadOnly(ref graph, ref pass, frame.ShadeBinIndices, sizeof(uint), "ShadeSlots");
                return;
            case ClusterShadeCoreResource.InstanceData:
                _ = pass.Bind(graph.CreateBufferSrv(frame.InstanceData, label: "Cluster shade instance data"), PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.InstanceProperties:
                _ = pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange, "Cluster shade instance properties"), PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.Uniforms:
                _ = pass.Bind(uniformBuffer, PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.Views:
                BindReadOnly(ref graph, ref pass, frame.PackedViews, ClusterViewUniforms.SizeInBytes, "Views");
                return;
            case ClusterShadeCoreResource.PixelCoordinates:
                BindReadOnly(ref graph, ref pass, frame.PixelCoordinates, 2 * sizeof(uint), "PixelCoordBuffer", BufferContentScope.CountBoundedRanges);
                return;
            case ClusterShadeCoreResource.BinOffsets:
                BindReadOnly(ref graph, ref pass, frame.ShadeBinOffsets, sizeof(uint), "BinOffsets");
                return;
            case ClusterShadeCoreResource.BinCounts:
                BindReadOnly(ref graph, ref pass, frame.ShadeBinCounts, sizeof(uint), "BinCounts");
                return;
            case ClusterShadeCoreResource.Visibility:
                _ = pass.Bind(graph.CreateTextureSrv(frame.VisBuffer, format: Format.R32UInt, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster shade visibility"), PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.Depth:
                _ = pass.Bind(graph.CreateTextureSrv(frame.Depth, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth), Format.D32Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster shade depth"), PipelineSync.ComputeShading);
                return;
            case ClusterShadeCoreResource.VisibleClusters:
                BindReadOnly(ref graph, ref pass, frame.VisibleClusters, ClusterGraphInstaller.VisibleClusterStride, "VisibleClusters", BufferContentScope.CountBoundedPrefix);
                return;
            case ClusterShadeCoreResource.PageHeap:
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(graph.CreateBufferSrv(frame.PageHeap, label: "Cluster shade PageHeap")));
                return;
            case ClusterShadeCoreResource.OutputColor:
                _ = pass.Bind(graph.CreateTextureUav(frame.SceneColor, format: Format.R16G16B16A16Float, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster material shade output"), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
                return;
            case ClusterShadeCoreResource.OutputMotionVectors:
                _ = pass.Bind(graph.CreateTextureUav(frame.MotionVectors, format: Format.R16G16Float, dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray, label: "Cluster material motion output"), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, unorderedAccessHazards: UnorderedAccessHazardMode.DisjointWrites);
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

        void BindReadOnly(ref RenderGraphEdit activeGraph, ref PassDefinition activePass, GraphBufferId buffer, uint stride, string name, BufferContentScope contentScope = BufferContentScope.FullRange)
        {
            _ = activePass.Bind(activeGraph.CreateBufferSrv(buffer, structureStride: stride, label: $"Cluster shade {name}"), PipelineSync.ComputeShading, contentScope);
        }
    }

    internal void BindStageMaterialResource(ref RenderGraphEdit graph, ref PassDefinition pass, in ShaderParameterResource resource, in ClusterMaterialStageBinding binding, Material material, PipelineSync sync)
    {
        MaterialInput input = GetMaterialInput(ref graph, material, binding.MaterialProperty, resource.Name, binding.Type);
        pass.Bind(input.Input, sync);
    }

    internal MaterialInput GetMaterialInput(ref RenderGraphEdit graph, Material material, uint property, string name, ResourceBindingType type)
    {
        var key = (material, property);
        if (type == ResourceBindingType.Sampler) _ = ResolveMaterialSampler(material, property, name);
        _installer.GraphData.MaterialInputChanges = _installer.GraphData.MaterialInputChanges.Add(key);
        if (_installer.GraphData.MaterialInputs.TryGetValue(key, out MaterialInput input))
        {
            DevelopmentCheck.Require(input.Input.Type == type, "A material property must keep the same resource binding type.");
            return input;
        }

        input = new MaterialInput(graph.CreateParameterInput(type), name);
        _installer.GraphData.MaterialInputs = _installer.GraphData.MaterialInputs.Add(key, input);
        return input;
    }

    internal ResourceBinding GetMaterialBufferSrv(Material material, uint materialProperty, string name)
    {
        BufferSrv view = RequireMaterialValue<BufferSrv>(material, materialProperty, name);
        return ResourceBinding.ReadOnlyBuffer(view);
    }

    internal ResourceBinding GetMaterialBufferUav(Material material, uint materialProperty, string name)
    {
        BufferUav view = RequireMaterialValue<BufferUav>(material, materialProperty, name);
        return ResourceBinding.WritableBuffer(view);
    }

    internal ResourceBinding GetMaterialConstantBuffer(Material material, uint materialProperty, string name)
    {
        BufferCbv view = RequireMaterialValue<BufferCbv>(material, materialProperty, name);
        return ResourceBinding.ConstantBuffer(view);
    }

    internal ResourceBinding GetMaterialAccelerationStructure(Material material, uint materialProperty, string name)
    {
        AccelerationStructureSrv view = RequireMaterialValue<AccelerationStructureSrv>(material, materialProperty, name);
        return ResourceBinding.AccelerationStructure(view);
    }

    internal static T RequireMaterialValue<T>(Material material, uint propertyIndex, string name)
        where T : class
    {
        object? value = material.GetDeviceResource(propertyIndex);
        DevelopmentCheck.Require(value is T, $"Material '{material.Name}' slot '{name}' must contain a {typeof(T).Name}.");
        return (T)value!;
    }

    internal Sampler ResolveMaterialSampler(Material material, uint materialProperty, string name)
    {
        SamplerDesc description = material.GetSampler(materialProperty);
        if (_materialSamplers.TryGetValue(description, out Sampler? samplerResource))
            return samplerResource;
        samplerResource = _geometry._backend.CreateSampler(_geometry._device, description);
        _materialSamplers.Add(description, samplerResource);
        return samplerResource;
    }

    internal void DisposeMaterialSamplers(ref List<Exception>? failures)
    {
        foreach (Sampler sampler in _materialSamplers.Values)
        {
            try
            {
                sampler.Dispose();
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        _materialSamplers.Clear();
    }

    internal ClusterMaterialScalars _materialScalars = ClusterMaterialScalars.Empty;
    internal void UpdateMaterialIndexBuffers(ref RenderGraphEdit edit, ClusterRasterStageSet raster, ClusterShadeStageSet shade)
    {
        if (!ReferenceEquals(raster, _installer._installedRasterStages))
        {
            Update(ref edit, _installer.GraphData.Resources.RasterBinIndices, raster.BinIndices, raster.ChangedOffsets);
            Update(ref edit, _installer.GraphData.Resources.MaterialBounds, raster.MaterialBounds, raster.ChangedBounds);
        }

        if (!ReferenceEquals(shade, _installer._installedShadeStages))
            Update(ref edit, _installer.GraphData.Resources.ShadeBinIndices, shade.BinIndices, shade.ChangedOffsets);
        void Update(ref RenderGraphEdit graph, GraphBufferId buffer, ClusterBytes values, IReadOnlyList<int> addresses)
        {
            BufferRange[] changes = addresses.Select(address => new BufferRange(checked((ulong)address * sizeof(uint)), sizeof(uint))).ToArray();
            _installer.UpdateBufferData(ref graph, buffer, values, changes);
        }
    }

    internal void UpdateScalarBuffers(ref RenderGraphEdit edit, ClusterMaterialScalars scalars)
    {
        _installer.UpdateBufferData(ref edit, _installer.GraphData.ScalarBytes, scalars.Bytes, scalars.ByteChanges);
        _installer.UpdateBufferData(ref edit, _installer.GraphData.ScalarOffsets, scalars.Offsets, scalars.OffsetChanges);
    }

    internal readonly record struct MaterialInput(GraphParameterInput Input, string Name);
    internal void UpdateShadeInputs(ref RenderGraphFrame graph, ClusterShadeStageSet stages)
    {
        ClusterDispatchParameters binDispatch = new(new DispatchArguments(Math.Max(1u, (stages.BinCount + 127u) / 128u), 1, 1));
        foreach (ClusterLayerDispatch dispatch in _installer.GraphData.ShadeBinDispatches)
            graph.SetPassData(dispatch.Pass, binDispatch);
        graph.SetBufferInput(_installer.GraphData.ShadeScalarOffsets, new GraphBufferSlice(_installer.GraphData.ScalarOffsets, new BufferRange(0, checked((ulong)_materialScalars.Offsets.Length))));
        graph.SetBufferInput(_installer.GraphData.ShadeScalars, new GraphBufferSlice(_installer.GraphData.ScalarBytes, new BufferRange(0, checked((ulong)GetFrameData(ref graph).Scalars.Bytes.Length))));
    }

    internal void UpdateMaterialInputs(ref RenderGraphFrame graph)
    {
        MaterialFrame data = GetFrameData(ref graph);
        graph.ObserveExecution(data);
        foreach ((Material material, uint property) in data.Source.MaterialInputChanges)
        {
            if (!data.Source.MaterialInputs.TryGetValue((material, property), out MaterialInput input) ||
                input.Input.Type is ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav) continue;
            ResourceBinding value = input.Input.Type switch
            {
                ResourceBindingType.BufferSrv => GetMaterialBufferSrv(material, property, input.Name),
                ResourceBindingType.BufferUav => GetMaterialBufferUav(material, property, input.Name),
                ResourceBindingType.ConstantBuffer => GetMaterialConstantBuffer(material, property, input.Name),
                ResourceBindingType.AccelerationStructure => GetMaterialAccelerationStructure(material, property, input.Name),
                _ => ResourceBinding.SampledWith(_materialSamplers[material.GetSampler(property)]),
            };
            graph.SetPersistentParameterInput(input.Input, value);
        }
        UpdateMaterialTextureInputs(ref graph, data);
        if (data.ScalarChanges.Count != 0)
        {
            ClusterMaterialScalars values = data.Scalars.Update(_installer._installedShadeStages!, _materialAssignments.Current,
                _renderDevice.GetTextureDescriptorIndex, data.ScalarChanges);
            ClusterGeometryPipeline.FrameData geometry = _geometry.GetFrameData(ref graph);
            foreach (BufferRange range in values.ByteChanges)
                geometry.Writes.Add((data.Source.ScalarBytes, new ClusterGraphInstaller.BufferWrite(range, values.Bytes)));
            data.Scalars = values;
            data.ScalarChanges.Clear();
        }
    }

    internal MaterialFrame GetFrameData(ref RenderGraphFrame graph) => graph.GetFrameData(this, () => new MaterialFrame(this));

    internal sealed class MaterialFrame : IRenderGraphFrameExecutionObserver
    {
        private readonly ClusterShadingPipeline _pipeline;
        internal readonly ClusterGraphInstaller.ClusterGraphData Source;
        internal readonly HashSet<EngineTexture> TextureInputs;
        internal readonly HashSet<int> ScalarChanges;
        internal bool MissingInputs;
        internal ImmutableDictionary<EngineTexture, MaterialTexture> Textures;
        internal MaterialTexture? MissingTexture;
        internal ClusterMaterialScalars Scalars;
        internal MaterialFrame(ClusterShadingPipeline pipeline)
        {
            _pipeline = pipeline;
            Source = pipeline._installer.GraphData;
            TextureInputs = [.. pipeline._materialTextureInputsToPrepare];
            ScalarChanges = [.. pipeline._textureScalarChanges];
            MissingInputs = pipeline._missingTextureInputsToPrepare;
            Textures = Source.MaterialTextures;
            MissingTexture = Source.MissingMaterialTexture;
            Scalars = pipeline._materialScalars;
        }
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            Source.MaterialInputChanges = Source.MaterialInputChanges.Clear();
            Source.MaterialTextures = Textures;
            Source.MissingMaterialTexture = MissingTexture;
            _pipeline._materialTextureInputsToPrepare.Clear();
            _pipeline._materialTextureInputsToPrepare.UnionWith(TextureInputs);
            _pipeline._textureScalarChanges.Clear();
            _pipeline._missingTextureInputsToPrepare = MissingInputs;
            _pipeline._materialScalars = Scalars;
        }
    }

    internal readonly record struct IndexedTextureRead(SomeEngine.Resources.Material Material, uint Property, SomeEngine.Resources.Texture? Texture);
    internal sealed record ShadeGroup(ClusterShadeDispatch Stage, ImmutableArray<IndexedTextureRead> TextureReads)
    {
        internal GraphBufferSlice Uniforms;
        internal GraphPassId Pass;
    }

    internal sealed class ShadeGroupComparer : IEqualityComparer<ShadeGroup>
    {
        internal static readonly ShadeGroupComparer Instance = new();
        public bool Equals(ShadeGroup? left, ShadeGroup? right) => ReferenceEquals(left, right) || left is not null && right is not null && ReferenceEquals(left.Stage.Material, right.Stage.Material) && left.Stage.Pipeline == right.Stage.Pipeline && left.Stage.CommandBindings.Equals(right.Stage.CommandBindings) && left.Stage.MaterialBindings.AsSpan().SequenceEqual(right.Stage.MaterialBindings) && left.TextureReads.AsSpan().SequenceEqual(right.TextureReads.AsSpan());
        public int GetHashCode(ShadeGroup value) => value.Stage.BinIndex.GetHashCode();
    }

    internal GraphRegionMap<uint, ShadeGroup> UpdateShadeGroups(ref RenderGraphEdit graph, ClusterShadeStageSet stages)
    {
        return UpdateMaterialGroups(ref graph, stages, _installer.GraphData.ShadeGroups);
    }

    internal GraphRegionMap<uint, ShadeGroup> UpdateMaterialGroups(ref RenderGraphEdit graph, ClusterShadeStageSet stages, GraphRegionMap<uint, ShadeGroup> previousGroups)
    {
        var reads = CollectIndexedTextureReads(stages.BinIndices, stages);
        var desired = stages.Dispatches.ToDictionary(static stage => stage.BinIndex, stage => new ShadeGroup(stage, reads.GetValueOrDefault(stage.BinIndex, [])));
        foreach (ShadeGroup group in previousGroups.Values)
            if (!desired.TryGetValue(group.Stage.BinIndex, out ShadeGroup? next) || !ShadeGroupComparer.Instance.Equals(group, next))
                _installer.RemoveParameters(group.Uniforms);
        GraphRegionMap<uint, ShadeGroup> groups = previousGroups.Reconcile(ref graph, desired, (ref RenderGraphEdit edit, GraphRegionId region, uint bin, ShadeGroup value) =>
        {
            var dispatch = new ClusterIndirectDispatchParameters(_geometry.RequireDispatchIndirectLayout(), _installer.GraphData.Resources.ShadeIndirectArgs, checked((ulong)bin * ClusterIndirectLayout.DispatchBytes));
            GraphPassId pass = edit.AddComputeFramePass($"Evaluate Cluster color bin {bin}", PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch, new PassOptions(RepeatCommands: true), value.Stage.Pipeline.Pipeline, VariableLayoutReflection.Null, default, static (ref PassDefinition access, ref ClusterIndirectDispatchParameters data) => _ = access.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectLayout.DispatchBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), ClusterIndirectDispatchParameters.Record, region: region);
            value.Pass = pass;
            value.Uniforms = BindShadeResources(ref edit, pass, value.Stage, stages);
            PassDefinition access = edit.GetPass(pass);
            ReadIndexedMaterialTextures(ref edit, ref access, value.TextureReads, PipelineSync.ComputeShading);
        });
        if (_installer._installedShadeStages is { } previous && (previous.BinCount != stages.BinCount || previous.BinIndices.Length != stages.BinIndices.Length))
        {
            Span<ClusterShadeUniforms> value = stackalloc ClusterShadeUniforms[1];
            foreach (ShadeGroup group in groups.Values)
            {
                value[0] = ShadeUniforms(group.Stage.BinIndex, stages);
                _installer.UpdateParameters(ref graph, group.Uniforms, MemoryMarshal.AsBytes(value));
            }
        }

        return groups;
    }

    internal Dictionary<uint, ImmutableArray<IndexedTextureRead>> CollectIndexedTextureReads(ClusterBytes binIndices, ClusterShadeStageSet stages, uint binMask = uint.MaxValue)
    {
        var reads = new Dictionary<uint, HashSet<IndexedTextureRead>>();
        Span<byte> binBytes = stackalloc byte[sizeof(uint)];
        foreach (int address in _materialAssignments.Current.ActiveOffsets)
        {
            if (!stages.ScalarLayouts.TryGetValue(address, out var scalarLayout))
                continue;
            _materialAssignments.Current.TryGetEntry(address, out var material, out _);
            binIndices.CopyTo(binBytes, address * sizeof(uint));
            uint bin = BinaryPrimitives.ReadUInt32LittleEndian(binBytes) & binMask;
            foreach (uint property in scalarLayout.TextureProperties)
            {
                if (!reads.TryGetValue(bin, out var members))
                    reads.Add(bin, members = []);
                members.Add(new(material, property, material.GetTexture(property)));
            }
        }

        return reads.ToDictionary(static pair => pair.Key, static pair => pair.Value.OrderBy(static value => value.Material.MaterialId).ThenBy(static value => value.Property).ToImmutableArray());
    }

    internal void ReadIndexedMaterialTextures(ref RenderGraphEdit graph, ref PassDefinition pass, ImmutableArray<IndexedTextureRead> reads, PipelineSync sync)
    {
        foreach (IndexedTextureRead read in reads)
        {
            MaterialInput input = GetMaterialInput(ref graph, read.Material, read.Property, read.Material.Properties[checked((int)read.Property)].Name, ResourceBindingType.TextureSrv);
            pass.Read(input.Input, sync);
        }
    }

    internal ClusterShadeUniforms ShadeUniforms(uint bin, ClusterShadeStageSet stages) => new()
    {
        ScreenWidth = checked((uint)_installer.GraphData.Resources.Width),
        ScreenHeight = checked((uint)_installer.GraphData.Resources.Height),
        ShadingBin = bin,
        ShadeBinCount = stages.BinCount,
        MaterialAddressCapacity = checked((uint)(stages.BinIndices.Length / sizeof(uint))),
        ViewCount = _installer.GraphData.Resources.ViewCount,
        VisibilityDepth = (uint)stages.GetBin(bin).VisibilityDepth
    };
    internal static uint CalculateShadeBinItemCapacity(in ClusterGraphInstaller.FrameResources frame)
    {
        ulong itemCapacity = Math.Min((ulong)frame.Width * (ulong)frame.Height * frame.ViewCount, uint.MaxValue);
        ulong quadWidth = ((ulong)frame.Width + 1u) / 2u;
        ulong quadHeight = ((ulong)frame.Height + 1u) / 2u;
        ulong quads = quadWidth * quadHeight * frame.ViewCount;
        // Reserve a disjoint worst-case range per bin only when it fits the existing
        // pixel buffer. Larger bin sets retain compact count/reserve/scatter storage.
        return quads != 0 && quads <= uint.MaxValue && frame.ShadeBinCount <= itemCapacity / quads ? (uint)quads : 0u;
    }

    internal void UpdateShadeBinning(ref RenderGraphEdit graph, ClusterShadeStageSet stages)
    {
        Span<ClusterShadeBinUniforms> value = stackalloc ClusterShadeBinUniforms[1];
        value[0] = new ClusterShadeBinUniforms
        {
            ScreenWidth = checked((uint)_installer.GraphData.Resources.Width),
            ScreenHeight = checked((uint)_installer.GraphData.Resources.Height),
            ShadeBinCount = _installer.GraphData.Resources.ShadeBinCount,
            MaterialAddressCapacity = _installer.GraphData.Resources.MaterialAddressCapacity,
            BinItemCapacity = CalculateShadeBinItemCapacity(_installer.GraphData.Resources),
            ViewCount = _installer.GraphData.Resources.ViewCount
        };
        _installer.UpdateParameters(ref graph, _installer.GraphData.ShadeBinUniforms, MemoryMarshal.AsBytes(value));
    }

    internal readonly record struct MaterialTexture(GraphTextureId Id, RhiTexture Texture, int References, bool RegisteredHere)
    {
        internal ImmutableArray<GraphParameterInput> Inputs { get; init; }
        internal TextureSrv? Srv { get; init; }
        internal TextureUav? Uav { get; init; }
        internal TextureVersions Versions { get; init; }
    }

    internal readonly HashSet<EngineTexture> _materialTextureInputsToPrepare = [];
    internal readonly HashSet<int> _textureScalarChanges = [];
    internal bool _missingTextureInputsToPrepare;
    internal ImmutableDictionary<EngineTexture, MaterialTexture> UpdateMaterialTextures(ref RenderGraphEdit edit)
    {
        var textureInputs = new Dictionary<EngineTexture, List<GraphParameterInput>>();
        var missingInputs = new List<GraphParameterInput>();
        foreach (((var material, uint property), MaterialInput input)in _installer.GraphData.MaterialInputs)
        {
            if (input.Input.Type is not (ResourceBindingType.TextureSrv or ResourceBindingType.TextureUav))
                continue;
            if (material.GetTexture(property)is { } asset)
            {
                if (!textureInputs.TryGetValue(asset, out var members))
                    textureInputs.Add(asset, members = []);
                members.Add(input.Input);
            }
            else if (input.Input.Type == ResourceBindingType.TextureUav)
                throw new InvalidDataException($"Material '{material.Name}' slot '{input.Name}' requires a Texture for writable access.");
            else
                missingInputs.Add(input.Input);
        }

        var textures = _installer._graphData.MaterialTextures;
        foreach ((EngineTexture asset, MaterialTexture previous)in _installer._graphData.MaterialTextures)
            if (!textureInputs.ContainsKey(asset))
            {
                if (previous.RegisteredHere)
                    edit.Remove(previous.Id);
                textures = textures.Remove(asset);
            }

        foreach ((EngineTexture asset, List<GraphParameterInput> inputs)in textureInputs)
        {
            TextureSrv sampled = _renderDevice.GetTextureSrv(asset);
            if (inputs.Any(input => input.Type == ResourceBindingType.TextureUav))
                _ = _renderDevice.GetTextureUav(asset, sampled.Description.Range);
            MaterialTexture value = textures.TryGetValue(asset, out var current) ? current : RegisterMaterialTexture(ref edit, asset, inputs.Count);
            if (!value.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inputs)))
                value = value with
                {
                    Inputs = inputs.ToImmutableArray(),
                    Srv = null,
                    Uav = null
                };
            textures = textures.SetItem(asset, value with { References = inputs.Count });
        }

        if (missingInputs.Count != 0)
        {
            MaterialTexture missing;
            if (_installer.GraphData.MissingMaterialTexture is { } existing)
                missing = existing;
            else
            {
                RhiTexture resource = _renderDevice.MissingTexture;
                bool registeredHere = !_installer._graph.TryGetTextureId(resource, out GraphTextureId id);
                if (registeredHere) id = edit.RegisterExternalTexture(resource);
                missing = new MaterialTexture(id, resource, 1, registeredHere);
            }

            if (!missing.Inputs.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(missingInputs)))
                missing = missing with
                {
                    Inputs = missingInputs.ToImmutableArray(),
                    Srv = null
                };
            _installer.GraphData.MissingMaterialTexture = missing;
        }
        else
            _installer.GraphData.MissingMaterialTexture = _installer.GraphData.MissingMaterialTexture is { } missing ? missing with
            {
                Inputs = []
            }

            : null;
        return textures;
    }

    internal MaterialTexture RegisterMaterialTexture(ref RenderGraphEdit edit, EngineTexture texture, int references)
    {
        RhiTexture resource = _renderDevice.GetTexture(texture);
        bool registeredHere = !_installer._graph.TryGetTextureId(resource, out GraphTextureId id);
        if (registeredHere)
            id = edit.RegisterExternalTexture(resource);
        return new(id, resource, references, registeredHere)
        {
            Versions = _geometry._resources.World.ReadTextureVersions(texture)
        };
    }

    internal void AcceptMaterialTextures(ImmutableDictionary<EngineTexture, MaterialTexture> textures, MaterialTexture? missing = null, bool shutdown = false)
    {
        foreach (EngineTexture texture in _installer._graphData.MaterialTextures.Keys)
            if (!textures.ContainsKey(texture))
                _materialTextureInputsToPrepare.Remove(texture);
        foreach ((EngineTexture texture, MaterialTexture next)in textures)
            if (!_installer._graphData.MaterialTextures.TryGetValue(texture, out MaterialTexture previous) || !ReferenceEquals(next.Texture, previous.Texture) || next.Versions != previous.Versions || !next.Inputs.Equals(previous.Inputs))
                _materialTextureInputsToPrepare.Add(texture);
        _missingTextureInputsToPrepare = missing is { Inputs.IsDefaultOrEmpty: false } value && (_missingTextureInputsToPrepare || _installer._graphData.MissingMaterialTexture is not { } before || !before.Inputs.Equals(value.Inputs));
        _installer._graphData.MaterialTextures = textures;
        _installer._graphData.MissingMaterialTexture = missing;
    }

    internal void RefreshMaterialTextures(ClusterMaterialAssignmentSet assignments)
    {
        var active = new HashSet<EngineTexture>();
        foreach (Material material in assignments.DistinctMaterials)
            foreach (MaterialPropertyDefinition property in material.Properties)
                if (material.GetTexture(property.Index) is { } texture) active.Add(texture);
        _materialTextureInputsToPrepare.IntersectWith(active);
        var changed = new HashSet<EngineTexture>();
        foreach (EngineTexture texture in active)
            if (_installer._graphData.MaterialTextures.TryGetValue(texture, out MaterialTexture value) &&
                (_geometry._resources.World.ReadTextureVersions(texture) != value.Versions || !ReferenceEquals(_renderDevice.GetTexture(texture), value.Texture)))
                changed.Add(texture);
        if (changed.Count == 0)
            return;
        var textures = _installer._graphData.MaterialTextures;
        using RenderGraphEdit editScope = _installer._graph.BeginEdit();
        RenderGraphEdit edit = editScope;
        foreach (EngineTexture asset in changed)
        {
            MaterialTexture current = textures[asset];
            if (!ReferenceEquals(_renderDevice.GetTexture(asset), current.Texture))
            {
                if (current.RegisteredHere)
                    edit.Remove(current.Id);
                current = RegisterMaterialTexture(ref edit, asset, current.References)with
                {
                    Inputs = current.Inputs
                };
            }

            textures = textures.SetItem(asset, current with { Versions = _geometry._resources.World.ReadTextureVersions(asset) });
            TextureSrv sampled = _renderDevice.GetTextureSrv(asset);
            if (current.Inputs.Any(input => input.Type == ResourceBindingType.TextureUav))
                _ = _renderDevice.GetTextureUav(asset, sampled.Description.Range);
        }

        if (_installer._installedShadeStages is { } stages)
            foreach (int address in _materialAssignments.Current.ActiveOffsets)
            {
                _materialAssignments.Current.TryGetEntry(address, out var material, out _);
                if (stages.ScalarLayouts.TryGetValue(address, out var layout) && layout.TextureProperties.Any(property => material.GetTexture(property)is { } texture && changed.Contains(texture)))
                    _textureScalarChanges.Add(address);
            }

        edit.Commit();
        AcceptMaterialTextures(textures, _installer._graphData.MissingMaterialTexture);
        foreach (EngineTexture asset in changed)
            _materialTextureInputsToPrepare.Add(asset);
        _geometry._rasterRevision = checked(_geometry._rasterRevision + 1);
        RecordTextureInstanceChanges(changed);
    }

    internal void UpdateMaterialTextureInputs(ref RenderGraphFrame frame, MaterialFrame data)
    {
        var textures = data.Textures;
        foreach (EngineTexture asset in data.TextureInputs.Count != 0 ? data.TextureInputs.ToArray() : [])
        {
            MaterialTexture material = data.Textures[asset];
            if (material.Inputs.IsDefaultOrEmpty)
            {
                data.TextureInputs.Remove(asset);
                continue;
            }

            RenderDevice.TextureViews views = _renderDevice.PrepareTextureForInputs(ref frame, material.Inputs.AsSpan(), asset, out bool hasFrameUploads);
            TextureSrv? srv = views.Srv;
            TextureUav? uav = views.Uav;
            if (!ReferenceEquals(views.Texture, material.Texture) && _installer._installedShadeStages is { } stages)
                foreach (int address in _materialAssignments.Current.ActiveOffsets)
                {
                    _materialAssignments.Current.TryGetEntry(address, out var value, out _);
                    if (stages.ScalarLayouts.TryGetValue(address, out var layout) && layout.TextureProperties.Any(property => ReferenceEquals(value.GetTexture(property), asset)))
                        data.ScalarChanges.Add(address);
                }

            bool registered = _installer._graph.TryGetTextureId(views.Texture, out _);
            if (registered && !hasFrameUploads)
                data.TextureInputs.Remove(asset);
            if (registered && ReferenceEquals(srv, material.Srv) && ReferenceEquals(uav, material.Uav))
                continue;
            foreach (GraphParameterInput input in material.Inputs)
            {
                ResourceBinding value = input.Type == ResourceBindingType.TextureSrv ? ResourceBinding.SampledTexture(srv!) : ResourceBinding.StorageTexture(uav!);
                if (registered)
                    frame.SetPersistentParameterInput(input, value);
                else
                    frame.SetParameterInput(input, value);
            }

            if (registered)
                textures = textures.SetItem(asset, material with { Srv = srv, Uav = uav });
        }

        data.Textures = textures;
        if (data.MissingInputs && data.MissingTexture is { Inputs.IsDefaultOrEmpty: false } missing)
        {
            TextureSrv srv = _renderDevice.PrepareMissingTextureForInputs(ref frame, missing.Inputs.AsSpan(), out bool hasFrameUploads);
            data.MissingInputs = hasFrameUploads;
            if (!ReferenceEquals(srv, missing.Srv))
            {
                foreach (GraphParameterInput input in missing.Inputs)
                    frame.SetPersistentParameterInput(input, ResourceBinding.SampledTexture(srv));
                data.MissingTexture = missing with
                {
                    Srv = srv
                };
            }
        }
    }

    internal ClusterMaterialChange ResolveMaterialChange(in ClusterMaterialChange change)
    {
        ClusterMaterialAssignmentSet assignments = change.Assignments;
        foreach (Material material in assignments.DistinctMaterials)
            foreach (MaterialPropertyDefinition property in material.Properties)
                if (material.GetTexture(property.Index)is { } texture)
                    _renderDevice.PrepareTexture(texture);
        CollectMaterialChanges(assignments, assignments.TopologyVersion != _installer._installedMaterialTopologyVersion);
        RefreshMaterialTextures(assignments);
        bool pathChanged = _installer._installedShadeStages is null || _installer._installedShadeStages.ShadingPath != _geometry._options.ShadingPath || _installer._installedShadeStages.DebugMode != _geometry._options.DebugMode;
        ClusterRasterStageSet raster = _rasterStages!.Resolve(assignments);
        ClusterShadeStageSet shade = _shadeStages!.Resolve(assignments, _geometry._options.ShadingPath, _geometry._options.DebugMode);
        ClusterMaterialScalars scalars = _materialScalars.Update(shade, assignments, _renderDevice.GetTextureDescriptorIndex, pathChanged ? assignments.ActiveOffsets.Concat(_textureScalarChanges) : _textureScalarChanges);
        var result = change with
        {
            RasterStages = raster,
            ShadeStages = shade,
            Scalars = scalars,
            ChangedRasterBins = ReferenceEquals(raster, _installer._installedRasterStages) ? [] : raster.ChangedBins,
            ChangedShadeBins = ReferenceEquals(shade, _installer._installedShadeStages) ? [] : shade.ChangedBins,
            ChangedScalarRanges = ReferenceEquals(scalars, _materialScalars) ? default : scalars.ByteChanges,
            ChangedTextures = _materialTextureInputsToPrepare.ToArray(),
            ShadingPathChanged = pathChanged,
        };
        _materialScalars = scalars;
        _textureScalarChanges.Clear();
        RecordMaterialInstanceChanges(assignments);
        RecordMaterialVersions(assignments);
        return result;
    }

    internal void RecordMaterialVersions(ClusterMaterialAssignmentSet assignments)
    {
        foreach (long id in assignments.ChangedMaterialIds)
        {
            if (!assignments.TryGetMaterial(id, out Material material))
            {
                _materialBindingVersions.Remove(id);
                continue;
            }

            var properties = new ulong[material.PropertyCount];
            for (uint property = 0; property < material.PropertyCount; property++)
                properties[property] = material.GetPropertyRevision(property);
            _materialBindingVersions[id] = new(material.Shader, material.Layer.HasValue, properties);
        }
    }

    internal readonly HashSet<long> _rasterChangedMaterials = [];
    internal void RecordMaterialInstanceChanges(ClusterMaterialAssignmentSet assignments)
    {
        if (_rasterChangedMaterials.Count == 0 && !assignments.ChangedOffsets.Any())
            return;
        var entities = new HashSet<Entity>();
        assignments.CollectChangedEntities(entities, _rasterChangedMaterials);
        _geometry._instanceChanges.RecordChanges([], entities);
    }

    internal void RecordTextureInstanceChanges(HashSet<EngineTexture> changed)
    {
        if (_installer._installedRasterStages is null)
            return;
        var materials = new HashSet<long>();
        var textures = new HashSet<EngineTexture>();
        foreach (ClusterRasterDispatch raster in _installer._installedRasterStages.Dispatches)
        {
            textures.Clear();
            raster.CommandBindings.CollectTextures(textures);
            if (textures.Overlaps(changed))
                materials.Add(raster.Material.MaterialId);
        }

        // Indexed textures live in the material scalar payload, not the direct command
        // binding key. Their contents can change without replacing the material property.
        foreach (Material material in _materialAssignments.Current.DistinctMaterials)
            foreach (MaterialPropertyDefinition property in material.Properties)
                if (material.GetTexture(property.Index)is { } texture && changed.Contains(texture))
                {
                    materials.Add(material.MaterialId);
                    break;
                }

        var entities = new HashSet<Entity>();
        _materialAssignments.Current.CollectMaterialEntities(materials, entities);
        _geometry._instanceChanges.RecordChanges([], entities);
    }
}
