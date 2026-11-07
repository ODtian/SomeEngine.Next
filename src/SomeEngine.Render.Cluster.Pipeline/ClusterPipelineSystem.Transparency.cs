using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Tiles;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
#if SOMEENGINE_TRANSPARENCY_RESEARCH
    private bool UseTransparencyReference => _options.TransparencyPixelListReference;
#else
    private const bool UseTransparencyReference = false;
#endif
    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private struct TransparencyParameters
    {
        internal uint Width, Height, ViewCount, MaterialCapacity;
        internal uint TriangleCapacity, VisibleCapacity;
        internal float ColorLimit, MaximumError;
        internal uint ReferenceCapacity, UseIndex, CellCount, Effects;
    }

    private sealed class TransparencyData
    {
        internal required TransparencyShaders Shaders;
        internal required ClusterShadeInvocation[] Materials;
        internal required ClusterShadeInvocation[][] ResourceMaterials;
        internal required ImmutableArray<IndexedTextureRead> TextureReads;
        internal required TileRendering Tiles;
        internal GraphBufferId ShadingTriangles, States;
        internal GraphBufferId Sources, SourceCounts, ActiveSources, OrderedStates;

        internal GraphBufferSlice Parameters, ShadeParameters, GeometryParameters;
        internal uint MaterialCapacity;
        internal RenderInstanceBatch? InstanceBatch;
        internal ulong InstanceRevision, GeometryRevision, RasterRevision;
        internal float StreamingQuality;
        internal RenderView[] Views = [];
    }

    private void ReconcileTransparency(ref RenderGraphEdit edit, ClusterMaterialAssignmentSet assignments, ClusterShadeStageSet stages)
    {
        var bins = new HashSet<uint>();
        Span<byte> binBytes = stackalloc byte[4];
        foreach (int address in assignments.ActiveOffsets)
            if (assignments.TryGetEntry(address, out Material material, out _) &&
                (material.State.Surface == SurfaceMode.Translucent || material.State.Ordered || assignments.UsesXray(material)))
            {
                stages.BinIndices.CopyTo(binBytes, address * 4);
                bins.Add(BinaryPrimitives.ReadUInt32LittleEndian(binBytes));
            }
        ClusterShadeInvocation[] materials = bins.Order().Select(stages.GetBin).ToArray();
        bool tileColor = _options.DebugMode == 0 && _options.BlendIndex != ClusterBlendIndex.FullScan && !assignments.HasXray && !assignments.HasOrdered &&
            materials.All(material => material.VisibilityDepth != ClusterVisibilityDepth.Offset);
        uint capacity = _options.BlendTriangleCapacity;
        ClusterShadeInvocation[][] resources = [];
        TransparencyShaders? shaders = materials.Length == 0 ? null : GetTransparencyShaders(materials, tileColor, out resources);
        if (shaders is not null && assignments.HasOrdered) PrepareOrderedTransparencyShaders(shaders);
        var textureReads = CollectIndexedTextureReads(stages.BinIndices, stages, includeDecals: true)
            .Where(pair => materials.Any(material => material.BinIndex == pair.Key)).OrderBy(pair => pair.Key)
            .SelectMany(pair => pair.Value).ToImmutableArray();
        TransparencyData? previous = GraphData.Transparency;
        if (previous is not null && ReferenceEquals(previous.Shaders, shaders) && previous.Tiles.TriangleCapacity == capacity &&
            previous.MaterialCapacity == assignments.SlotCapacity && SameTransparencyBindings(previous.Materials, materials))
        {
            previous.Tiles.SetColorPasses(ref edit, previous.Shaders.Pipelines,
                new TilePassInputs(RequireDispatchIndirectLayout(), RequireDrawIndirectLayout(),
                    GraphData.Resources.Depth, GraphData.Resources.Width, GraphData.Resources.Height),
                previous.Shaders.ColorEntries, GraphData.Stencil!.ClearPass,
                (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                    BindTransparencyResources(ref graph, ref pass, previous, sync));
            if (!previous.TextureReads.AsSpan().SequenceEqual(textureReads.AsSpan()))
            {
                previous.TextureReads = textureReads;
                foreach (GraphPassId pass in previous.Tiles.Builds) UpdateTransparencyTextureReads(ref edit, pass, previous);
                foreach (GraphPassId pass in previous.Tiles.RequestPasses) UpdateTransparencyTextureReads(ref edit, pass, previous);
                foreach (GraphPassId pass in previous.Tiles.ColorPasses) UpdateTransparencyTextureReads(ref edit, pass, previous);
            }
            UpdateTransparencyParameters(ref edit, previous, assignments);
            UpdateTransparencyStates(ref edit, previous, assignments, stages);
            return;
        }
        if (previous is not null)
        {
            RemoveBlendMeasurements(ref edit);
            RemoveTransparency(ref edit, previous);
        }
        GraphData.Transparency = null;
        GraphData.Outputs = GraphData.Outputs with { Transparency = null };
        if (shaders is null) return;
        FrameResources frame = GraphData.Resources;
        _ = CreateStencil(ref edit);
        uint tiles = checked(_viewGrid.CellCountPerView * frame.ViewCount);
        bool pixelLists = UseTransparencyReference;
        bool adaptive = !pixelLists && tileColor && _options.BlendIndex == ClusterBlendIndex.Adaptive;
        bool hardwareBinning = !pixelLists && tileColor && _options.ForceHardwareRaster &&
            _options.BlendIndex is ClusterBlendIndex.Xy or ClusterBlendIndex.Adaptive;
        ulong pixels = (ulong)frame.Width * (ulong)frame.Height * frame.ViewCount;
        GraphRegionId preparation = _transparencyRequestRegion != default ? _transparencyRequestRegion : _regions.Shading;
        BufferUsages storage = BufferUsages.ShaderRead | BufferUsages.ShaderWrite;
        var tilesRendering = new TileRendering(ref edit,
            new TileRenderingDescription(CellCount: tiles, ViewCount: frame.ViewCount,
                TriangleCapacity: capacity, ReferenceCapacity: _options.BlendReferenceCapacity,
                TriangleStride: shaders.TriangleStride, SourceBoundsStride: shaders.SourceBoundsStride,
                IndexedColor: tileColor, AdaptiveDepth: adaptive, HardwareCoverage: _options.ForceHardwareRaster,
                HardwareBinning: hardwareBinning, PixelLists: pixelLists, Measure: _options.MeasureBlending,
                PixelCapacity: pixels, Indexed: _options.BlendIndex != ClusterBlendIndex.FullScan),
            GraphData.PrimaryGeometry, _regions.PrimaryGeometry, preparation, GraphData.BlendColors);
        var data = new TransparencyData
        {
            Tiles = tilesRendering,
            Shaders = shaders, Materials = materials, ResourceMaterials = resources, TextureReads = textureReads,
            MaterialCapacity = assignments.SlotCapacity,

            ShadingTriangles = edit.CreatePersistentBuffer(new BufferDesc((ulong)capacity * shaders.ShadingStride, storage,
                "Cluster transparent user triangle data")),
            States = CreateBufferData(ref edit, new byte[checked((int)assignments.SlotCapacity * 8)], BufferUsages.ShaderRead, "Cluster transparency material states"),

            OrderedStates = CreateBufferData(ref edit, new byte[checked((int)assignments.SlotCapacity * 64)], BufferUsages.ShaderRead, "Cluster ordered material states"),

            Sources = edit.CreatePersistentBuffer(new BufferDesc((ulong)_options.MaxCandidates * 12, storage, "Cluster transparency source requests")),
            SourceCounts = edit.CreatePersistentBuffer(new BufferDesc(16, storage, "Cluster transparency source counts")),
            ActiveSources = edit.CreatePersistentBuffer(new BufferDesc((ulong)_options.MaxCandidates * 4, storage, "Cluster transparency active sources")),

            GeometryParameters = CreateParameters(ref edit, new byte[8]),
            Parameters = CreateParameters(ref edit, new byte[48]),
            ShadeParameters = CreateParameters(ref edit, new byte[48]),
        };
        GraphData.Transparency = data;
        UpdateTransparencyParameters(ref edit, data, assignments);
        UpdateTransparencyStates(ref edit, data, assignments, stages);
        data.Tiles.Install(ref edit, shaders.Pipelines,
            new TilePassInputs(RequireDispatchIndirectLayout(), RequireDrawIndirectLayout(), frame.Depth, frame.Width, frame.Height),
            shaders.SplitSorting,
            _transparencyRequestRegion != default && shaders.Pipelines.ContainsKey("RequestTransparentShadows"),
            shaders.ColorEntries, GraphData.Stencil!.ClearPass, ConnectResources);
        GraphData.Outputs = GraphData.Outputs with { Transparency = new(data.Tiles.Triangles,data.Tiles.Counts,
            data.Tiles.References,data.Tiles.TriangleCount,capacity,shaders.TriangleStride,data.Tiles.GeometryReady)
            {
                CellOffsets = data.Tiles.Offsets, StreamingCells = data.Tiles.Streams, Sources = data.Sources,
                SourceCounts = data.SourceCounts, ReferenceCapacity = _options.BlendReferenceCapacity,
            } };

        void ConnectResources(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
            BindTransparencyResources(ref graph, ref pass, data, sync);
    }

    private static bool SameTransparencyBindings(ClusterShadeInvocation[] previous, ClusterShadeInvocation[] next) =>
        previous.Length == next.Length && previous.Zip(next).All(pair =>
            ReferenceEquals(pair.First.Material, pair.Second.Material) && pair.First.CommandBindings.Equals(pair.Second.CommandBindings));

    private void UpdateTransparencyParameters(ref RenderGraphEdit edit, TransparencyData data,
        ClusterMaterialAssignmentSet assignments)
    {
        FrameResources frame = GraphData.Resources;
        Span<TransparencyParameters> parameters = stackalloc TransparencyParameters[1];
        float colorLimit = 0;
        float maximumError = float.PositiveInfinity;
        foreach (int address in assignments.ActiveOffsets)
            if (assignments.TryGetEntry(address, out Material material, out _) &&
                (material.State.Surface == SurfaceMode.Translucent || material.State.Ordered || assignments.UsesXray(material)))
            {
                colorLimit = MathF.Max(colorLimit, material.State.ColorLimit);
                maximumError = MathF.Min(maximumError, material.State.MaximumError);
            }
        parameters[0] = new()
        {
            Width = (uint)frame.Width, Height = (uint)frame.Height, ViewCount = frame.ViewCount,
            MaterialCapacity = assignments.SlotCapacity, TriangleCapacity = data.Tiles.TriangleCapacity,
            VisibleCapacity = _options.MaxCandidates, ReferenceCapacity = _options.BlendReferenceCapacity,
            UseIndex = UseTransparencyReference ? 2u :
                _options.BlendIndex == ClusterBlendIndex.FullScan ? 0u : _options.ForceHardwareRaster ? 3u : 1u, CellCount = _viewGrid.CellCountPerView * frame.ViewCount,
            Effects = (assignments.HasXray ? 1u : 0u) | (assignments.HasOrdered ? 2u : 0u) |
                (data.Tiles.AdaptiveDepth ? 4u : 0u) | (data.Tiles.HardwareBinning ? 8u : 0u),
            ColorLimit = colorLimit,
            MaximumError = assignments.HasXray ? 0 : maximumError,
        };
        UpdateParameters(ref edit, data.Parameters, MemoryMarshal.AsBytes(parameters));
        Span<uint> geometry = stackalloc uint[2] { _options.MaxTessellationFactor, BitConverter.SingleToUInt32Bits(_options.TessellationTargetEdgePixels) };
        UpdateParameters(ref edit, data.GeometryParameters, MemoryMarshal.AsBytes(geometry));
        Span<ClusterShadeUniforms> shade = stackalloc ClusterShadeUniforms[1];
        shade[0] = new ClusterShadeUniforms { ScreenWidth = (uint)frame.Width, ScreenHeight = (uint)frame.Height,
            ViewCount = frame.ViewCount, MaterialSlotCapacity = assignments.SlotCapacity, DebugMode = _options.DebugMode };
        UpdateParameters(ref edit, data.ShadeParameters, MemoryMarshal.AsBytes(shade));
    }

    private void UpdateTransparencyStates(ref RenderGraphEdit edit, TransparencyData data, ClusterMaterialAssignmentSet assignments,
        ClusterShadeStageSet stages)
    {
        byte[] states = new byte[checked((int)assignments.SlotCapacity * 8)];
        Span<byte> bin = stackalloc byte[4];
        foreach (int address in assignments.ActiveOffsets)
            if (assignments.TryGetEntry(address, out Material material, out _) &&
                (material.State.Surface == SurfaceMode.Translucent || material.State.Ordered || assignments.UsesXray(material)))
            {
                stages.BinIndices.CopyTo(bin, address * 4);
                ClusterShadeInvocation stage = stages.GetBin(BinaryPrimitives.ReadUInt32LittleEndian(bin));
                int evaluator = Array.IndexOf(data.Shaders.Members, new TransparencyMember(material.Shader!, stage.VisibilityDepth));
                int resource = Array.FindIndex(data.ResourceMaterials[evaluator], entry => entry.CommandBindings.Equals(stage.CommandBindings));
                uint facing = material.State.TwoSided ? 2u : 1u;
                if (stage.VisibilityDepth == ClusterVisibilityDepth.Offset) facing |= 0x40000000u;
                if (material.State.Surface != SurfaceMode.Translucent && !material.State.Ordered) facing |= 0x80000000u;
                BinaryPrimitives.WriteUInt32LittleEndian(states.AsSpan(address * 8), ((uint)evaluator + 1u) << 2 | facing);
                BinaryPrimitives.WriteUInt32LittleEndian(states.AsSpan(address * 8 + 4), (uint)resource);
            }
        ClusterBytes bytes = ClusterBytes.FromBytes(states);
        UpdateBufferData(ref edit, data.States, bytes, [new BufferRange(0, (ulong)states.Length)]);
        UpdateOrderedStates(ref edit, data, assignments);
    }

    private void BindTransparencyResources(ref RenderGraphEdit edit, ref PassDefinition pass,
        TransparencyData data, PipelineSync sync)
    {
        string entry = data.Tiles.PassNames[pass.Id];
        if (entry == "RasterTransparentHardwareCoverage")
        {
            Shader hardware = data.Shaders.Pipelines.HardwareShader;
            int hardwareTarget = hardware.RequireSlangTarget(_device.Capabilities.CompileTarget);
            pass.BindParameterBlock(hardware.GetSlangReflection(hardwareTarget).GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
            string vertex = UseTransparencyReference ? "DrawTransparencyReference" : "DrawTransparentCoverage";
            string pixel = UseTransparencyReference ? "StoreTransparencyReference" : "StoreTransparentCoverage";
            foreach (ShaderParameterResource resource in hardware.GetSlangResourceLayout(
                _device.Capabilities.CompileTarget, vertex, pixel).Resources)
            {
                if (BindInactiveStageResource(ref pass, resource)) continue;
                TryBindTransparencyResource(ref edit, ref pass, data, resource, pixel, sync);
            }
            return;
        }
        Shader shader = data.Shaders.Pipelines[entry].Shader;
        int target = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        BindBlock(ref edit, ref pass, reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, null, true);
        EntryPointReflection reflectedEntry = shader.RequireSlangEntry(target, entry, SlangStage.Compute);
        VariableLayoutReflection entryLayout = reflectedEntry.VarLayout;
        BindBlock(ref edit, ref pass, entryLayout, GetTransparencyEntryMaterials(data, entryLayout), false);
        ReadIndexedMaterialTextures(ref edit, ref pass, data.TextureReads, PipelineSync.ComputeShading);

        void BindBlock(ref RenderGraphEdit graph, ref PassDefinition access, VariableLayoutReflection layout, List<Material>? materials, bool global)
        {
            if (layout == VariableLayoutReflection.Null) return;
            access.BindParameterBlock(layout);
            var resources = ShaderParameterLayout.FromReflection(layout, global
                ? (category, space, register) => shader.IsSlangParameterLocationUsed(target, entry, category, space, register)
                : null);
            int resourceIndex = 0;
            var propertyLayouts = new Dictionary<Material, uint?[]>();
            foreach (ShaderParameterResource resource in resources.Resources)
            {
                int index = resourceIndex++;
                Material boundMaterial = materials is null ? data.Materials[0].Material : materials[index];
                if (BindInactiveStageResource(ref access, resource)) continue;
                if (BindDeformRead(ref graph, ref access, resource, PipelineSync.ComputeShading)) continue;
                if (TryBindTransparencyResource(ref graph, ref access, data, resource, entry)) continue;
                ClusterShadeCoreResource core = ClusterShadeCoreResources.Resolve(resource.Name, resource.Type);
                if (core != ClusterShadeCoreResource.None)
                {
                    BindShadeCoreResource(ref graph, ref access, core, data.ShadeParameters);
                    continue;
                }
                if (_stageResourceProvider?.TryResolve(resource.Name, resource.Type, out GraphResourceBinder binder) == true)
                {
                    binder(ref graph, ref access, PipelineSync.ComputeShading);
                    continue;
                }
                if (!propertyLayouts.TryGetValue(boundMaterial, out uint?[]? properties))
                    propertyLayouts.Add(boundMaterial, properties = boundMaterial.ResolveResourceProperties(resources.Resources));
                uint property = properties[index] ?? throw new InvalidDataException(
                    $"Transparent material {boundMaterial.Name} does not bind resource {resource.Name}.");
                BindStageMaterialResource(ref graph, ref access, resource, property,
                    boundMaterial, PipelineSync.ComputeShading);
            }
        }
    }

    private void UpdateTransparencyTextureReads(ref RenderGraphEdit edit, GraphPassId id, TransparencyData data)
    {
        edit.ClearIndexedReads(id);
        PassDefinition pass = edit.GetPass(id);
        ReadIndexedMaterialTextures(ref edit, ref pass, data.TextureReads, PipelineSync.ComputeShading);
    }

    private bool TryBindTransparencyResource(ref RenderGraphEdit graph, ref PassDefinition pass, TransparencyData data,
        ShaderParameterResource resource, string entry, PipelineSync sync = PipelineSync.ComputeShading)
    {
        if (data.Tiles.GetResources(entry).TryResolve(resource.Name, resource.Type, out GraphResourceBinder tileBinder))
        {
            tileBinder(ref graph, ref pass, sync);
            return true;
        }
        FrameResources frame = GraphData.Resources;
        GraphBufferId buffer;
        uint stride = 4;
        switch (resource.Name)
        {
            case "TransparencyUniforms": pass.Bind(data.Parameters, sync); return true;
            case "TransparencyGeometryUniforms": pass.Bind(data.GeometryParameters, sync); return true;
            case "TransparencyLightingViews": pass.Bind(graph.CreateBufferSrv(_viewGrid.Parameters, structureStride: 192), sync); return true;
            case "TransparencyViews": buffer = frame.PackedViews; stride = ClusterViewUniforms.SizeInBytes; break;
            case "TransparencySources": case "TransparencySourceOutput": buffer = data.Sources; stride = 12; break;
            case "TransparencySourceCounts": buffer = data.SourceCounts; stride = 0; break;
            case "TransparencyActiveSources": buffer = data.ActiveSources; break;
            case "TransparencyInstances": buffer = frame.InstanceData; stride = 0; break;
            case "TransparencyProperties": pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange), sync); return true;
            case "TransparencyVisibleClusters": buffer = frame.VisibleClusters; stride = VisibleClusterStride; break;
            case "TransparencyPageHeap": BindPageHeapDescriptor(ref graph, ref pass, "Cluster transparent geometry"); return true;
            case "TransparencyFirstDraw": buffer = frame.DrawArgs; stride = 0; break;
            case "TransparencySecondDraw": buffer = frame.Phase2DrawArgs; stride = 0; break;
            case "TransparencyMaterialStates": buffer = data.States; stride = 8; break;
            case "OrderedMaterialStates": buffer = data.OrderedStates; stride = 64; break;
            case "OrderedStencil": pass.Bind(graph.CreateTextureUav(GraphData.Stencil!.Values, format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray), sync,
                GraphAccessMode.Write, WriteCoverage.Partial); return true;
            case "TransparencyShadingTriangles": case "TransparencyShadingInputs":
            case "SceneTransparencyShadingTriangles": case "SceneTransparencyShadingInputs":
                buffer = data.ShadingTriangles; stride = data.Shaders.ShadingStride; break;
            case "TransparencyOpaqueDepth": pass.Bind(graph.CreateTextureSrv(frame.Depth, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth),
                Format.D32Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray), sync); return true;
            case "TransparencyColor": pass.Bind(graph.CreateTextureUav(frame.SceneColor, format: Format.R16G16B16A16Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray), sync,
                GraphAccessMode.ReadWrite, WriteCoverage.Partial); return true;
            default: return false;
        }
        if (resource.Type == ResourceBindingType.BufferUav)
        {
            bool clear = entry == "ClearTransparentTiles";
            GraphAccessMode mode = (entry, resource.Name) switch
            {
                ("ClearTransparentTiles", _) => GraphAccessMode.Write,
                ("CopyTransparentSources", "TransparencySourceOutput" or "TransparencyActiveSources") => GraphAccessMode.Write,
                ("BuildTransparentGeometry", "TransparencyShadingTriangles" or "SceneTransparencyShadingTriangles") => GraphAccessMode.Write,
                _ => GraphAccessMode.Read,
            };
            pass.Bind(graph.CreateBufferUav(buffer, structureStride: stride), sync, mode,
                clear ? WriteCoverage.Complete : WriteCoverage.Partial, ResourceContentState.Defined);
        }
        else pass.Bind(graph.CreateBufferSrv(buffer, structureStride: stride), sync,
            resource.Name is "TransparencyVisibleClusters" or "TransparencySources" or "TransparencyShadingInputs" or "SceneTransparencyShadingInputs"
                ? BufferContentScope.CountBoundedPrefix : BufferContentScope.FullRange);
        return true;
    }

    private void BindTransparencyFrame(ref RenderGraphFrame graph, RenderInstanceBatch batch,
        scoped ReadOnlySpan<RenderView> views, bool geometryChanged)
    {
        if (GraphData.Transparency is not { } data) return;
        bool reuse = data.Tiles.HasSubmittedGeometry && !geometryChanged && ReferenceEquals(data.InstanceBatch, batch) &&
            data.InstanceRevision == batch.ContentRevision && data.GeometryRevision == GeometryRevision &&
            data.RasterRevision == RasterRevision && data.StreamingQuality == _resources.Residency.StreamingQuality &&
            views.SequenceEqual(data.Views);
        data.Tiles.BindFrame(ref graph, reuse);
        if (!reuse)
        {
            data.InstanceBatch = batch;
            data.InstanceRevision = batch.ContentRevision;
            data.GeometryRevision = GeometryRevision;
            data.RasterRevision = RasterRevision;
            data.StreamingQuality = _resources.Residency.StreamingQuality;
            if (data.Views.Length != views.Length) data.Views = new RenderView[views.Length];
            views.CopyTo(data.Views);
        }
    }

    private void RemoveTransparency(ref RenderGraphEdit edit, TransparencyData data)
    {
        data.Tiles.Remove(ref edit);
        edit.Remove(data.ShadingTriangles);
        edit.Remove(data.Sources); edit.Remove(data.SourceCounts); edit.Remove(data.ActiveSources);
        RemoveParameters(data.GeometryParameters);
        RemoveBufferData(ref edit, data.States);
        RemoveBufferData(ref edit, data.OrderedStates);
        RemoveParameters(data.Parameters); RemoveParameters(data.ShadeParameters);
    }
}
