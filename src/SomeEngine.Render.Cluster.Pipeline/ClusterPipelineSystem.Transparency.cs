using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct TransparencyParameters
    {
        internal uint Width, Height, ViewCount, MaterialCapacity;
        internal uint TriangleCapacity, VisibleCapacity;
        internal float ColorLimit, MaximumError;
    }

    private sealed record TransparencyMember(uint Bin, Shader Shader, ClusterVisibilityDepth VisibilityDepth);

    private sealed class TransparencyShaders(Shader shader, TransparencyMember[] members, ClusterComputePipeline clear, ClusterComputePipeline build,
        ClusterComputePipeline request, ClusterComputePipeline resolve)
    {
        internal readonly Shader Shader = shader;
        internal readonly TransparencyMember[] Members = members;
        internal readonly ClusterComputePipeline Clear = clear, Build = build, Request = request, Resolve = resolve;
        internal readonly uint TriangleStride = GetTriangleStride(shader);

        private static uint GetTriangleStride(Shader shader)
        {
            ShaderReflection reflection = shader.GetSlangReflection(0);
            TypeReflection type = reflection.FindTypeByName("TransparencyTriangle") ?? throw new InvalidDataException("TransparencyTriangle is required.");
            TypeLayoutReflection layout = reflection.GetTypeLayout(type, LayoutRules.DefaultStructuredBuffer) ?? throw new InvalidDataException("TransparencyTriangle layout is required.");
            return checked((uint)layout.GetSize(SlangParameterCategory.Uniform));
        }
    }

    private sealed class TransparencyData
    {
        internal required TransparencyShaders Shaders;
        internal required ClusterShadeInvocation[] Materials;
        internal required ImmutableArray<IndexedTextureRead> TextureReads;
        internal GraphRegionId Geometry, Requests, Color;
        internal GraphBufferId Triangles, TriangleCount, Counts, References, Arguments, States;
        internal GraphBufferSlice Parameters, ShadeParameters;
        internal uint TriangleCapacity, MaterialCapacity;
    }

    private readonly List<TransparencyShaders> _transparencyShaders = [];

    private TransparencyShaders GetTransparencyShaders(ClusterShadeInvocation[] materials)
    {
        TransparencyMember[] members = materials.Select(stage =>
            new TransparencyMember(stage.BinIndex, stage.Material.Shader!, stage.VisibilityDepth)).ToArray();
        foreach (TransparencyShaders existing in _transparencyShaders)
            if (existing.Members.AsSpan().SequenceEqual(members)) return existing;

        var source = new StringBuilder();
        foreach (string unit in materials.SelectMany(stage => Enumerable.Range(0, stage.Material.Shader!.SlangSourceUnitCount)
                     .Select(stage.Material.Shader!.GetSlangSourceUnitName)).Distinct())
            source.Append("import \"").Append(unit).AppendLine("\";");
        source.AppendLine("struct SceneTransparencyMaterials : ITransparencyMaterials {");
        for (int index = 0; index < materials.Length; index++)
            source.Append(FindTransparencyType(materials[index].Material.Shader!)).Append(" Material").Append(index).AppendLine(";");
        AppendMethod("bool project(uint bin, uint visibility, uint view, out float4 first, out float4 second, out float4 third)",
            "project(visibility, view, first, second, third)", "return false;");
        AppendMethod("bool cover(uint bin, uint visibility, uint3 pixel, float depth, out float resultDepth)",
            "cover(visibility, pixel, depth, resultDepth)", "return false;");
        source.AppendLine("float4 shade(uint bin, uint visibility, uint3 pixel, float depth) {");
        for (int index = 0; index < materials.Length; index++)
            source.Append("if (bin == ").Append(materials[index].BinIndex).Append("u) return Material").Append(index)
                .Append(".shade<").Append((uint)materials[index].VisibilityDepth).AppendLine("u>(visibility, pixel, depth);");
        source.AppendLine("return 0.0.xxxx; }");
        source.AppendLine("void receiver(uint bin, uint visibility, uint3 pixel, float depth, out float3 position, out float3 normal, out uint flags) {");
        for (int index = 0; index < materials.Length; index++)
            source.Append("if (bin == ").Append(materials[index].BinIndex).Append("u) { Material").Append(index)
                .Append(".receiver<").Append((uint)materials[index].VisibilityDepth).AppendLine("u>(visibility, pixel, depth, position, normal, flags); return; }");
        source.AppendLine("position=0.0.xxx; normal=0.0.xxx; flags=0u; }");
        source.AppendLine("};");
        source.AppendLine("[shader(\"compute\")] [numthreads(64,1,1)] void ClearTransparentTiles(uint3 thread : SV_DispatchThreadID) { ClearTransparency(thread); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(64,1,1)] void BuildTransparentGeometry(uniform SceneTransparencyMaterials materials, uint3 thread : SV_DispatchThreadID) { BuildTransparency(materials, thread); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void RequestTransparentShadows(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { RequestTransparencyShadows(materials, pixel); }");
        source.AppendLine("[shader(\"compute\")] [numthreads(16,16,1)] void ResolveTransparentColor(uniform SceneTransparencyMaterials materials, uint3 pixel : SV_DispatchThreadID) { ResolveTransparency(materials, pixel); }");
        Shader shader = materials[0].Material.Shader!.Compose("Cluster transparent materials", "scene_transparency.slang",
            source.ToString(), materials.Skip(1).Select(stage => stage.Material.Shader!).ToArray(), _device.Capabilities.CompileTarget);
        ClusterComputePipeline Pipeline(string entry) => ClusterComputePipeline.Create(_backend, _device, shader, entry, entry);
        var result = new TransparencyShaders(shader, members, Pipeline("ClearTransparentTiles"),
            Pipeline("BuildTransparentGeometry"), Pipeline("RequestTransparentShadows"), Pipeline("ResolveTransparentColor"));
        _transparencyShaders.Add(result);
        return result;

        void AppendMethod(string signature, string call, string fallback)
        {
            source.Append(signature).AppendLine(" {");
            for (int index = 0; index < materials.Length; index++)
                source.Append("if (bin == ").Append(materials[index].BinIndex).Append("u) return Material").Append(index).Append('.').Append(call).AppendLine(";");
            source.AppendLine(fallback).AppendLine("}");
        }
    }

    private static string FindTransparencyType(Shader shader)
    {
        string? name = null;
        for (int unit = 0; unit < shader.SlangSourceUnitCount; unit++) Visit(shader.GetSlangSourceUnit(unit).GetModuleReflection());
        return name ?? throw new InvalidDataException($"Material shader '{shader.Name}' requires a TransparencyMaterial evaluator.");
        void Visit(DeclReflection declaration)
        {
            if (declaration == DeclReflection.Null) return;
            if (declaration.Kind == DeclReflectionKind.Struct)
            {
                TypeReflection type = declaration.Type;
                for (uint attribute = 0; attribute < type.AttributeCount; attribute++)
                    if (type.GetAttribute(attribute).Name == "TransparencyMaterial") name = type.Name;
            }
            for (uint child = 0; child < declaration.ChildrenCount; child++) Visit(declaration.GetChild(child));
        }
    }

    private void ReconcileTransparency(ref RenderGraphEdit edit, ClusterMaterialAssignmentSet assignments, ClusterShadeStageSet stages)
    {
        ClusterShadeInvocation[] materials = stages.Invocations.Where(stage => stage.Material.State.Surface == SurfaceMode.Translucent).ToArray();
        uint capacity = 0;
        if (materials.Length != 0)
        {
            foreach (var entity in assignments.TranslucentEntities)
            {
                foreach (MeshPayloadPageDigest page in _resources.World.Read<RenderMesh>(entity).Mesh.PageDigests)
                    capacity = checked(capacity + page.ClusterCount * 128u);
            }
            capacity = checked(capacity * GraphData.Resources.ViewCount);
        }
        TransparencyShaders? shaders = materials.Length == 0 ? null : GetTransparencyShaders(materials);
        var textureReads = CollectIndexedTextureReads(stages.BinIndices, stages)
            .Where(pair => materials.Any(material => material.BinIndex == pair.Key)).OrderBy(pair => pair.Key)
            .SelectMany(pair => pair.Value).ToImmutableArray();
        TransparencyData? previous = GraphData.Transparency;
        if (previous is not null && ReferenceEquals(previous.Shaders, shaders) && previous.TriangleCapacity == capacity &&
            previous.MaterialCapacity == assignments.SlotCapacity && SameTransparencyBindings(previous.Materials, materials) &&
            previous.TextureReads.AsSpan().SequenceEqual(textureReads.AsSpan()))
        {
            UpdateTransparencyParameters(ref edit, previous, assignments);
            UpdateTransparencyStates(ref edit, previous, assignments);
            return;
        }
        if (previous is not null) RemoveTransparency(ref edit, previous);
        GraphData.Transparency = null;
        GraphData.Outputs = GraphData.Outputs with { Transparency = null };
        if (shaders is null) return;
        FrameResources frame = GraphData.Resources;
        uint tiles = checked((uint)((frame.Width + 15) / 16) * (uint)((frame.Height + 15) / 16) * frame.ViewCount);
        GraphRegionId preparation = _transparencyRequestRegion != default ? _transparencyRequestRegion : _surfaceReceiverRegion;
        if (preparation == default) preparation = _regions.Shading;
        var data = new TransparencyData
        {
            Shaders = shaders, Materials = materials, TextureReads = textureReads,
            TriangleCapacity = capacity, MaterialCapacity = assignments.SlotCapacity,
            Geometry = edit.AddRegion("Cluster transparent geometry", preparation),
            Requests = edit.AddRegion("Cluster transparent shadow requests", preparation),
            Color = edit.AddRegion("Cluster transparent color", _regions.Shading),
            Triangles = edit.CreatePersistentBuffer(new BufferDesc((ulong)capacity * shaders.TriangleStride,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Cluster transparent projected triangles")),
            Counts = edit.CreatePersistentBuffer(new BufferDesc((ulong)tiles * 4u,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Cluster transparency tile counts")),
            References = edit.CreatePersistentBuffer(new BufferDesc((ulong)capacity * tiles * 4u,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Cluster transparency tile references")),
            TriangleCount = edit.CreatePersistentBuffer(new BufferDesc(4, BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                "Cluster transparent triangle count")),
            Arguments = edit.CreatePersistentBuffer(new BufferDesc(12, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.Indirect,
                "Cluster transparent geometry arguments")),
            States = CreateBufferData(ref edit, new byte[checked((int)assignments.SlotCapacity * 4)], BufferUsages.ShaderRead, "Cluster transparency material states"),
            Parameters = CreateParameters(ref edit, new byte[32], "Cluster transparency parameters"),
            ShadeParameters = CreateParameters(ref edit, new byte[48], "Cluster transparency surface parameters"),
        };
        GraphData.Transparency = data;
        UpdateTransparencyParameters(ref edit, data, assignments);
        UpdateTransparencyStates(ref edit, data, assignments);
        GraphPassId cleared = AddTransparencyPass(ref edit, data, shaders.Clear, "ClearTransparentTiles", data.Geometry,
            new DispatchArguments((tiles + 63u) / 64u, 1, 1));
        GraphPassId geometryReady = AddTransparencyPass(ref edit, data, shaders.Build, "BuildTransparentGeometry", data.Geometry, default, indirect: true);
        edit.OrderAfter(geometryReady, cleared);
        GraphData.Outputs = GraphData.Outputs with { Transparency = new(data.Triangles, data.Counts,
            data.References, data.TriangleCount, capacity, shaders.TriangleStride, geometryReady) };
        if (_transparencyRequestRegion != default)
        {
            GraphPassId requests = AddTransparencyPass(ref edit, data, shaders.Request, "RequestTransparentShadows", data.Requests,
                new DispatchArguments(((uint)frame.Width + 15u) / 16u, ((uint)frame.Height + 15u) / 16u, frame.ViewCount));
            edit.OrderAfter(requests, geometryReady);
        }
        GraphPassId color = AddTransparencyPass(ref edit, data, shaders.Resolve, "ResolveTransparentColor", data.Color,
            new DispatchArguments(((uint)frame.Width + 15u) / 16u, ((uint)frame.Height + 15u) / 16u, frame.ViewCount));
        edit.OrderAfter(color, geometryReady);
    }

    private static bool SameTransparencyBindings(ClusterShadeInvocation[] previous, ClusterShadeInvocation[] next) =>
        previous.Length == next.Length && previous.Zip(next).All(pair =>
            ReferenceEquals(pair.First.Material, pair.Second.Material) && pair.First.CommandBindings.Equals(pair.Second.CommandBindings));

    private void UpdateTransparencyParameters(ref RenderGraphEdit edit, TransparencyData data,
        ClusterMaterialAssignmentSet assignments)
    {
        FrameResources frame = GraphData.Resources;
        Span<TransparencyParameters> parameters = stackalloc TransparencyParameters[1];
        Material[] transparent = assignments.DistinctMaterials.Where(material => material.State.Surface == SurfaceMode.Translucent).ToArray();
        parameters[0] = new()
        {
            Width = (uint)frame.Width, Height = (uint)frame.Height, ViewCount = frame.ViewCount,
            MaterialCapacity = assignments.SlotCapacity, TriangleCapacity = data.TriangleCapacity,
            VisibleCapacity = _options.MaxCandidates,
            ColorLimit = transparent.Max(material => material.State.ColorLimit),
            MaximumError = transparent.Min(material => material.State.MaximumError),
        };
        UpdateParameters(ref edit, data.Parameters, MemoryMarshal.AsBytes(parameters));
        Span<ClusterShadeUniforms> shade = stackalloc ClusterShadeUniforms[1];
        shade[0] = new ClusterShadeUniforms { ScreenWidth = (uint)frame.Width, ScreenHeight = (uint)frame.Height,
            ViewCount = frame.ViewCount, MaterialSlotCapacity = assignments.SlotCapacity, DebugMode = _options.DebugMode };
        UpdateParameters(ref edit, data.ShadeParameters, MemoryMarshal.AsBytes(shade));
    }

    private void UpdateTransparencyStates(ref RenderGraphEdit edit, TransparencyData data, ClusterMaterialAssignmentSet assignments)
    {
        byte[] states = new byte[checked((int)assignments.SlotCapacity * 4)];
        foreach (int address in assignments.ActiveOffsets)
            if (assignments.TryGetEntry(address, out Material material, out _) && material.State.Surface == SurfaceMode.Translucent)
                BinaryPrimitives.WriteUInt32LittleEndian(states.AsSpan(address * 4), material.State.TwoSided ? 2u : 1u);
        ClusterBytes bytes = ClusterBytes.FromBytes(states);
        UpdateBufferData(ref edit, data.States, bytes, [new BufferRange(0, (ulong)states.Length)]);
    }

    private GraphPassId AddTransparencyPass(ref RenderGraphEdit edit, TransparencyData data, ClusterComputePipeline pipeline,
        string entry, GraphRegionId region, DispatchArguments groups, bool indirect = false)
    {
        GraphPassId id;
        if (indirect)
        {
            var dispatch = new ClusterIndirectDispatchParameters(RequireDispatchIndirectLayout(), data.Arguments, 0);
            id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch,
                new PassOptions(RepeatCommands: true), pipeline.Pipeline, pipeline.ParameterLayout, default,
                static (ref PassDefinition pass, ref ClusterIndirectDispatchParameters value) =>
                    _ = pass.Read(value.Arguments, new BufferRange(0, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
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
        var entryMaterials = new List<Material>();
        for (uint field = 0; field < entryLayout.TypeLayout.FieldCount; field++)
        {
            VariableLayoutReflection parameter = entryLayout.TypeLayout.GetFieldByIndex(field);
            if (parameter.Name != "materials") continue;
            for (uint material = 0; material < parameter.TypeLayout.FieldCount; material++)
            {
                var resources = ClusterLinkedStageLayout.Create(parameter.TypeLayout.GetFieldByIndex(material));
                foreach (var resource in resources.Resources) entryMaterials.Add(data.Materials[material].Material);
            }
        }
        BindBlock(ref edit, ref pass, entryLayout, entryMaterials, false);
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
            case "TransparencyInstances": buffer = frame.InstanceData; stride = 0; break;
            case "TransparencyProperties": pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading); return true;
            case "TransparencyVisibleClusters": buffer = frame.VisibleClusters; stride = VisibleClusterStride; break;
            case "TransparencyPageHeap": BindPageHeapDescriptor(ref graph, ref pass, "Cluster transparent geometry", PipelineSync.ComputeShading); return true;
            case "TransparencyFirstDraw": buffer = frame.DrawArgs; stride = 0; break;
            case "TransparencySecondDraw": buffer = frame.Phase2DrawArgs; stride = 0; break;
            case "TransparencyMaterialBins": buffer = frame.ShadeSlots; break;
            case "TransparencyMaterialStates": buffer = data.States; break;
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
            GraphAccessMode mode = clear ? GraphAccessMode.Write : entry == "BuildTransparentGeometry"
                ? resource.Name is "TransparencyTriangleCount" or "TransparencyTileCounts" ? GraphAccessMode.ReadWrite : GraphAccessMode.Write
                : GraphAccessMode.Read;
            pass.Bind(graph.CreateBufferUav(buffer, structureStride: stride), PipelineSync.ComputeShading, mode,
                clear ? WriteCoverage.Complete : WriteCoverage.Partial, ResourceContentState.Defined);
        }
        else pass.Bind(graph.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading,
            resource.Name == "TransparencyVisibleClusters" ? BufferContentScope.CountBoundedPrefix : BufferContentScope.FullRange);
        return true;
    }

    private void RemoveTransparency(ref RenderGraphEdit edit, TransparencyData data)
    {
        edit.Remove(data.Geometry); edit.Remove(data.Requests); edit.Remove(data.Color);
        edit.Remove(data.Triangles); edit.Remove(data.TriangleCount); edit.Remove(data.Counts); edit.Remove(data.References); edit.Remove(data.Arguments);
        RemoveBufferData(ref edit, data.States);
        RemoveParameters(data.Parameters); RemoveParameters(data.ShadeParameters);
    }
}
