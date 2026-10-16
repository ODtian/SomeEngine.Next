using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
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
        internal GraphRegionId Geometry, Requests, Color;
        internal GraphBufferId Triangles, TriangleCount, Counts, References, Arguments, States;
        internal GraphBufferId Offsets, Writes, Streams, Sources, SourceCounts, OrderedStates;
        internal GraphBufferSlice Parameters, ShadeParameters, GeometryParameters;
        internal uint TriangleCapacity, MaterialCapacity;
        internal ClusterBlendMeasurements? Measurements;
        internal GraphPassId[] Builds = [];
        internal RenderInstanceBatch? InstanceBatch;
        internal ulong InstanceRevision, GeometryRevision, RasterRevision;
        internal float StreamingQuality;
        internal RenderView[] Views = [];
        internal bool Reused, Completed;

    }

    private void ReconcileTransparency(ref RenderGraphEdit edit, ClusterMaterialAssignmentSet assignments, ClusterShadeStageSet stages)
    {
        ClusterShadeInvocation[] materials = stages.Invocations.Where(stage =>
            stage.Material.State.Surface == SurfaceMode.Translucent || stage.Material.State.Ordered || assignments.UsesXray(stage.Material)).ToArray();
        uint capacity = _options.BlendTriangleCapacity;
        ClusterShadeInvocation[][] resources = [];
        TransparencyShaders? shaders = materials.Length == 0 ? null : GetTransparencyShaders(materials, out resources);
        var textureReads = CollectIndexedTextureReads(stages.BinIndices, stages, includeDecals: true)
            .Where(pair => materials.Any(material => material.BinIndex == pair.Key)).OrderBy(pair => pair.Key)
            .SelectMany(pair => pair.Value).ToImmutableArray();
        TransparencyData? previous = GraphData.Transparency;
        if (previous is not null && ReferenceEquals(previous.Shaders, shaders) && previous.TriangleCapacity == capacity &&
            previous.MaterialCapacity == assignments.SlotCapacity && SameTransparencyBindings(previous.Materials, materials) &&
            previous.TextureReads.AsSpan().SequenceEqual(textureReads.AsSpan()))
        {
            previous.Completed = false;
            UpdateTransparencyParameters(ref edit, previous, assignments);
            UpdateTransparencyStates(ref edit, previous, assignments, stages);
            return;
        }
        if (previous is not null) RemoveTransparency(ref edit, previous);
        GraphData.Transparency = null;
        GraphData.Outputs = GraphData.Outputs with { Transparency = null };
        if (shaders is null)
        {
            if (GraphData.Stencil is { } existingStencil)
                existingStencil.SetSourceReady(ref edit, existingStencil.ClearPass, null);
            return;
        }
        FrameResources frame = GraphData.Resources;
        ClusterStencil stencil = GraphData.Stencil ??= new(_backend, _device, _graph, ref edit,
            frame.Width, frame.Height, frame.ViewCount, _regions.Shading);
        GraphData.Outputs = GraphData.Outputs with { Stencil = stencil };
        uint tiles = checked(_viewGrid.CellCountPerView * frame.ViewCount);
        GraphRegionId preparation = _transparencyRequestRegion != default ? _transparencyRequestRegion : _surfaceReceiverRegion;
        if (preparation == default) preparation = _regions.Shading;
        BufferUsages storage = BufferUsages.ShaderRead | BufferUsages.ShaderWrite;
        BufferUsages counted = storage | (_options.MeasureBlending ? BufferUsages.CopySource : BufferUsages.None);
        BufferDesc triangles = new((ulong)capacity * shaders.TriangleStride, storage, "Cluster transparent projected triangles");
        BufferDesc counts = new((ulong)tiles * 4u, counted, "Cluster transparency tile counts");
        BufferDesc references = new((ulong)_options.BlendReferenceCapacity * 4u, storage, "Cluster transparency continuous references");
        BufferDesc triangleCount = new(16, counted, "Cluster transparent triangle count");
        BufferDesc arguments = new(48, storage | BufferUsages.Indirect, "Cluster transparent geometry arguments");
        var data = new TransparencyData
        {
            Shaders = shaders, Materials = materials, ResourceMaterials = resources, TextureReads = textureReads,
            TriangleCapacity = capacity, MaterialCapacity = assignments.SlotCapacity,
            Geometry = edit.AddRegion("Cluster transparent geometry", GraphData.PrimaryGeometry),
            Requests = edit.AddRegion("Cluster transparent shadow requests", preparation),
            Color = edit.AddRegion("Cluster transparent color", _regions.Shading),
            Triangles = edit.CreatePersistentBuffer(triangles),
            Counts = edit.CreatePersistentBuffer(counts),
            References = edit.CreatePersistentBuffer(references),
            TriangleCount = edit.CreatePersistentBuffer(triangleCount),
            Arguments = edit.CreatePersistentBuffer(arguments),
            States = CreateBufferData(ref edit, new byte[checked((int)assignments.SlotCapacity * 8)], BufferUsages.ShaderRead, "Cluster transparency material states"),
            OrderedStates = CreateBufferData(ref edit, new byte[checked((int)assignments.SlotCapacity * 64)], BufferUsages.ShaderRead, "Cluster ordered material states"),
            Offsets = edit.CreatePersistentBuffer(counts with { Label = "Cluster transparency cell offsets" }),
            Writes = edit.CreatePersistentBuffer(counts with { Label = "Cluster transparency cell writes" }),
            Streams = edit.CreatePersistentBuffer(counts with { Label = "Cluster transparency streaming cells" }),
            Sources = edit.CreatePersistentBuffer(new BufferDesc((ulong)_options.MaxCandidates * 12, storage, "Cluster transparency source requests")),
            SourceCounts = edit.CreatePersistentBuffer(new BufferDesc(16, storage, "Cluster transparency source counts")),
            GeometryParameters = CreateParameters(ref edit, new byte[8], "Cluster transparency geometry parameters"),
            Parameters = CreateParameters(ref edit, new byte[48], "Cluster transparency parameters"),
            ShadeParameters = CreateParameters(ref edit, new byte[48], "Cluster transparency surface parameters"),
        };
        GraphData.Transparency = data;
        UpdateTransparencyParameters(ref edit, data, assignments);
        UpdateTransparencyStates(ref edit, data, assignments, stages);
        string[] entries = ["ClearTransparentTiles", "CopyTransparentSources", "BuildTransparentGeometry", "ReserveTransparentCells", "ScatterTransparentCells"];
        uint[] offsets = [0, 12, 0, 0, 36];
        data.Builds = new GraphPassId[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            bool indirect = index is 1 or 2 or 4;
            data.Builds[index] = AddTransparencyPass(ref edit, data, shaders.Pipelines[entries[index]], entries[index], data.Geometry,
                new DispatchArguments((tiles + 63u) / 64u, 1, 1), indirect, offsets[index]);
            if (index != 0) edit.OrderAfter(data.Builds[index], data.Builds[index - 1]);
        }
        GraphPassId cleared = data.Builds[0], geometryReady = data.Builds[^1];
        GraphData.Outputs = GraphData.Outputs with { Transparency = new(data.Triangles, data.Counts,
            data.References, data.TriangleCount, capacity, shaders.TriangleStride, geometryReady)
            {
                CellOffsets = data.Offsets, StreamingCells = data.Streams, Sources = data.Sources,
                SourceCounts = data.SourceCounts, ReferenceCapacity = _options.BlendReferenceCapacity,
            } };
        GraphPassId requests = default;
        if (_transparencyRequestRegion != default)
        {
            requests = AddTransparencyPass(ref edit, data, shaders.Pipelines["RequestTransparentShadows"], "RequestTransparentShadows", data.Requests,
                new DispatchArguments(((uint)frame.Width + 15u) / 16u, ((uint)frame.Height + 15u) / 16u, frame.ViewCount));
            edit.OrderAfter(requests, geometryReady);
        }
        GraphPassId color = AddTransparencyPass(ref edit, data, shaders.Pipelines["ResolveTransparentColor"], "ResolveTransparentColor", data.Color,
            new DispatchArguments(((uint)frame.Width + 15u) / 16u, ((uint)frame.Height + 15u) / 16u, frame.ViewCount));
        edit.OrderAfter(color, geometryReady);
        edit.OrderAfter(color, stencil.ClearPass);
        if (_options.MeasureBlending)
        {
            BufferDesc states = GraphData.BufferDescriptions[data.States].Description;
            BufferDesc orderedStates = GraphData.BufferDescriptions[data.OrderedStates].Description;
            BufferDesc[] descriptions = [triangles, counts, counts, counts, counts, references, triangleCount, arguments, states, orderedStates,
                new((ulong)_options.MaxCandidates * 12, storage), new(16, storage)];
            ulong imageBytes = (ulong)frame.Width * (ulong)frame.Height * frame.ViewCount * 4u;
            var stencilDescription = new TextureDesc(TextureDimension.Texture2D, (uint)frame.Width, (uint)frame.Height,
                1, 1, frame.ViewCount, 1, Format.R32UInt, TextureUsages.Sampled | TextureUsages.Storage);
            ulong capacityBytes = imageBytes, allocationBytes = _backend.GetTextureMemoryRequirements(_device, stencilDescription).Size;
            foreach (BufferDesc description in descriptions)
            {
                capacityBytes += description.Size;
                allocationBytes += _backend.GetBufferMemoryRequirements(_device, description).Size;
            }
            data.Measurements = new(_backend, _device, _graph, ref edit,
                new(data.Geometry, cleared, geometryReady, data.Requests, requests, data.Color, stencil.ClearPass, color, _regions.Feedback),
                new(data.TriangleCount, shaders.TriangleStride,
                    counts.Size * 4 + triangleCount.Size + arguments.Size + states.Size + orderedStates.Size + 16 + imageBytes,
                    capacityBytes, allocationBytes, capacity),
                ReceiveBlendFrame);
        }
        stencil.SetSourceReady(ref edit, color, data.Measurements);
    }

    private static bool SameTransparencyBindings(ClusterShadeInvocation[] previous, ClusterShadeInvocation[] next) =>
        previous.Length == next.Length && previous.Zip(next).All(pair =>
            ReferenceEquals(pair.First.Material, pair.Second.Material) && pair.First.CommandBindings.Equals(pair.Second.CommandBindings));

    private void UpdateTransparencyParameters(ref RenderGraphEdit edit, TransparencyData data,
        ClusterMaterialAssignmentSet assignments)
    {
        FrameResources frame = GraphData.Resources;
        Span<TransparencyParameters> parameters = stackalloc TransparencyParameters[1];
        Material[] transparent = data.Materials.Select(stage => stage.Material).ToArray();
        parameters[0] = new()
        {
            Width = (uint)frame.Width, Height = (uint)frame.Height, ViewCount = frame.ViewCount,
            MaterialCapacity = assignments.SlotCapacity, TriangleCapacity = data.TriangleCapacity,
            VisibleCapacity = _options.MaxCandidates, ReferenceCapacity = _options.BlendReferenceCapacity,
            UseIndex = _options.BlendIndex == ClusterBlendIndex.Xyz ? 1u : 0u, CellCount = _viewGrid.CellCountPerView * frame.ViewCount,
            Effects = (assignments.HasXray ? 1u : 0u) | (assignments.HasOrdered ? 2u : 0u),
            ColorLimit = transparent.Max(material => material.State.ColorLimit),
            MaximumError = assignments.HasXray ? 0 : transparent.Min(material => material.State.MaximumError),
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
                if (material.State.Surface != SurfaceMode.Translucent && !material.State.Ordered) facing |= 0x80000000u;
                BinaryPrimitives.WriteUInt32LittleEndian(states.AsSpan(address * 8), ((uint)evaluator + 1u) << 2 | facing);
                BinaryPrimitives.WriteUInt32LittleEndian(states.AsSpan(address * 8 + 4), (uint)resource);
            }
        ClusterBytes bytes = ClusterBytes.FromBytes(states);
        UpdateBufferData(ref edit, data.States, bytes, [new BufferRange(0, (ulong)states.Length)]);
        UpdateOrderedStates(ref edit, data, assignments);
    }

    private GraphPassId AddTransparencyPass(ref RenderGraphEdit edit, TransparencyData data, ClusterComputePipeline pipeline,
        string entry, GraphRegionId region, DispatchArguments groups, bool indirect = false, uint argumentOffset = 0)
    {
        GraphPassId id;
        if (indirect)
        {
            var dispatch = new ClusterIndirectDispatchParameters(RequireDispatchIndirectLayout(), data.Arguments, argumentOffset);
            id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch,
                new PassOptions(RepeatCommands: true), pipeline.Pipeline, pipeline.ParameterLayout, default,
                static (ref PassDefinition pass, ref ClusterIndirectDispatchParameters value) =>
                    _ = pass.Read(value.Arguments, new BufferRange(value.Offset, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                ClusterIndirectDispatchParameters.Record, region: region);
        }
        else id = AddComputePass(ref edit, entry, pipeline, new ClusterDispatchParameters(groups), 0,
            ClusterDispatchParameters.Record, region: region);
        PassDefinition pass = edit.GetPass(id);
        int target = data.Shaders.Shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        ShaderReflection reflection = data.Shaders.Shader.GetSlangReflection(target);
        BindBlock(ref edit, ref pass, reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, null, true);
        EntryPointReflection reflectedEntry = data.Shaders.Shader.RequireSlangEntry(target, entry, SlangStage.Compute);
        VariableLayoutReflection entryLayout = reflectedEntry.VarLayout;
        BindBlock(ref edit, ref pass, entryLayout, GetTransparencyEntryMaterials(data, entryLayout), false);
        ReadIndexedMaterialTextures(ref edit, ref pass, data.TextureReads, PipelineSync.ComputeShading);
        return id;

        void BindBlock(ref RenderGraphEdit graph, ref PassDefinition access, VariableLayoutReflection layout, List<Material>? materials, bool global)
        {
            if (layout == VariableLayoutReflection.Null) return;
            access.BindParameterBlock(layout);
            var resources = ClusterLinkedStageLayout.Create(layout, global
                ? (category, space, register) => data.Shaders.Shader.IsSlangParameterLocationUsed(target, entry, category, space, register)
                : null);
            int resourceIndex = 0;
            foreach (ClusterLinkedStageResource resource in resources.Resources)
            {
                Material boundMaterial = materials is null ? data.Materials[0].Material : materials[resourceIndex++];
                if (BindInactiveStageResource(ref access, resource)) continue;
                if (TryBindTransparencyResource(ref graph, ref access, data, resource, entry)) continue;
                ClusterShadeCoreResource core = ClusterShadeCoreResources.Resolve(resource.Name, resource.Type);
                if (core != ClusterShadeCoreResource.None)
                {
                    BindShadeCoreResource(ref graph, ref access, core, data.ShadeParameters,
                        GraphData.ShadeScalars, GraphData.ShadeScalarOffsets, UnorderedAccessHazardMode.Ordered);
                    continue;
                }
                if (_stageResourceProvider?.TryResolve(resource.Name, resource.Type, out GraphResourceBinder binder) == true)
                {
                    binder(ref graph, ref access, PipelineSync.ComputeShading);
                    continue;
                }
                uint property = checked((uint)boundMaterial.Properties.ToList().FindIndex(value => value.Name == resource.Name));
                BindStageMaterialResource(ref graph, ref access, resource, new ClusterMaterialStageBinding(property, resource.Type, true),
                    boundMaterial, PipelineSync.ComputeShading);
            }
        }
    }

    private bool TryBindTransparencyResource(ref RenderGraphEdit graph, ref PassDefinition pass, TransparencyData data,
        ClusterLinkedStageResource resource, string entry)
    {
        FrameResources frame = GraphData.Resources;
        GraphBufferId buffer;
        uint stride = 4;
        switch (resource.Name)
        {
            case "TransparencyUniforms": pass.Bind(data.Parameters, PipelineSync.ComputeShading); return true;
            case "TransparencyGeometryUniforms": pass.Bind(data.GeometryParameters, PipelineSync.ComputeShading); return true;
            case "TransparencyLightingViews": pass.Bind(graph.CreateBufferSrv(_viewGrid.Parameters, structureStride: 192), PipelineSync.ComputeShading); return true;
            case "TransparencyViews": buffer = frame.PackedViews; stride = ClusterViewUniforms.SizeInBytes; break;
            case "TransparencySources": case "TransparencySourceOutput": buffer = data.Sources; stride = 12; break;
            case "TransparencySourceCounts": buffer = data.SourceCounts; stride = 0; break;
            case "TransparencyCellOffsets": buffer = data.Offsets; break;
            case "TransparencyCellWrites": buffer = data.Writes; break;
            case "TransparencyCellStreams": buffer = data.Streams; break;
            case "TransparencyInstances": buffer = frame.InstanceData; stride = 0; break;
            case "TransparencyProperties": pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading); return true;
            case "TransparencyVisibleClusters": buffer = frame.VisibleClusters; stride = VisibleClusterStride; break;
            case "TransparencyPageHeap": BindPageHeapDescriptor(ref graph, ref pass, "Cluster transparent geometry", PipelineSync.ComputeShading); return true;
            case "TransparencyFirstDraw": buffer = frame.DrawArgs; stride = 0; break;
            case "TransparencySecondDraw": buffer = frame.Phase2DrawArgs; stride = 0; break;
            case "TransparencyMaterialStates": buffer = data.States; stride = 8; break;
            case "OrderedMaterialStates": buffer = data.OrderedStates; stride = 64; break;
            case "OrderedStencil": pass.Bind(graph.CreateTextureUav(GraphData.Stencil!.Values, format: Format.R32UInt,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading,
                GraphAccessMode.Write, WriteCoverage.Complete); return true;
            case "TransparencyTriangles": buffer = data.Triangles; stride = data.Shaders.TriangleStride; break;
            case "TransparencyTileCounts": buffer = data.Counts; break;
            case "TransparencyReferences": buffer = data.References; break;
            case "TransparencyArguments": buffer = data.Arguments; stride = 0; break;
            case "TransparencyTriangleCount": buffer = data.TriangleCount; stride = 0; break;
            case "TransparencyOpaqueDepth": pass.Bind(graph.CreateTextureSrv(frame.Depth, new TextureSubresourceRange(0, 1, 0, frame.ViewCount, TextureAspects.Depth),
                Format.D32Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading); return true;
            case "TransparencyColor": pass.Bind(graph.CreateTextureUav(frame.SceneColor, format: Format.R16G16B16A16Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite, WriteCoverage.Partial); return true;
            default: return false;
        }
        if (resource.Type == ResourceBindingType.BufferUav)
        {
            bool clear = entry == "ClearTransparentTiles";
            GraphAccessMode mode = (entry, resource.Name) switch
            {
                ("ClearTransparentTiles", _) => GraphAccessMode.Write,
                ("CopyTransparentSources", "TransparencySourceOutput") => GraphAccessMode.Write,
                ("BuildTransparentGeometry", "TransparencyTriangles") => GraphAccessMode.Write,
                ("BuildTransparentGeometry", "TransparencyTriangleCount" or "TransparencyTileCounts" or "TransparencyCellStreams") => GraphAccessMode.ReadWrite,
                ("ReserveTransparentCells", "TransparencyCellOffsets" or "TransparencyArguments") => GraphAccessMode.Write,
                ("ReserveTransparentCells", "TransparencyTriangleCount" or "TransparencyCellStreams") => GraphAccessMode.ReadWrite,
                ("ScatterTransparentCells", "TransparencyReferences") => GraphAccessMode.Write,
                ("ScatterTransparentCells", "TransparencyCellWrites") => GraphAccessMode.ReadWrite,
                _ => GraphAccessMode.Read,
            };
            pass.Bind(graph.CreateBufferUav(buffer, structureStride: stride), PipelineSync.ComputeShading, mode,
                clear ? WriteCoverage.Complete : WriteCoverage.Partial, ResourceContentState.Defined);
        }
        else pass.Bind(graph.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading,
            resource.Name is "TransparencyVisibleClusters" or "TransparencySources" ? BufferContentScope.CountBoundedPrefix : BufferContentScope.FullRange);
        return true;
    }

    private void BindTransparencyFrame(ref RenderGraphFrame graph, RenderInstanceBatch batch,
        scoped ReadOnlySpan<RenderView> views, bool geometryChanged)
    {
        if (GraphData.Transparency is not { } data) return;
        bool reuse = data.Completed && !geometryChanged && ReferenceEquals(data.InstanceBatch, batch) &&
            data.InstanceRevision == batch.ContentRevision && data.GeometryRevision == GeometryRevision &&
            data.RasterRevision == RasterRevision && data.StreamingQuality == _resources.Residency.StreamingQuality &&
            views.SequenceEqual(data.Views);
        data.Reused = reuse;
        foreach (GraphPassId pass in data.Builds) graph.SetPassEnabled(pass, !reuse);
        if (!reuse)
        {
            data.Completed = false;
            data.InstanceBatch = batch;
            data.InstanceRevision = batch.ContentRevision;
            data.GeometryRevision = GeometryRevision;
            data.RasterRevision = RasterRevision;
            data.StreamingQuality = _resources.Residency.StreamingQuality;
            if (data.Views.Length != views.Length) data.Views = new RenderView[views.Length];
            views.CopyTo(data.Views);
        }
        data.Measurements?.Stage(ref graph, _measurementSimulationFrame, reuse);
    }

    private void CompleteTransparencyFrame(RenderGraphFrameResult result)
    {
        if (GraphData.Transparency is { Reused: false } data && result.TryGetPassCompletion(data.Builds[^1], out _))
            data.Completed = true;
    }

    private void RemoveTransparency(ref RenderGraphEdit edit, TransparencyData data)
    {
        data.Measurements?.Remove(ref edit);
        edit.Remove(data.Geometry); edit.Remove(data.Requests); edit.Remove(data.Color);
        edit.Remove(data.Triangles); edit.Remove(data.TriangleCount); edit.Remove(data.Counts); edit.Remove(data.References); edit.Remove(data.Arguments);
        edit.Remove(data.Offsets); edit.Remove(data.Writes); edit.Remove(data.Streams); edit.Remove(data.Sources); edit.Remove(data.SourceCounts);
        RemoveParameters(data.GeometryParameters);
        RemoveBufferData(ref edit, data.States);
        RemoveBufferData(ref edit, data.OrderedStates);
        RemoveParameters(data.Parameters); RemoveParameters(data.ShadeParameters);
    }
}
