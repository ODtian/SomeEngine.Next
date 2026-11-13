using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Layers;

public readonly record struct LayerHardwareRasterResources(GraphRegionId Region,
    GraphBufferId[] Parameters, GraphBufferCbvId[] ParameterViews,
    GraphPassId[] Passes, GraphBufferSrvId[] Srvs, GraphPassId CompletePass);

/// <summary>Registers one indirect hardware draw per geometry stage from its large-window cluster list.</summary>
public static class LayerHardwareRasterPasses
{
    public static GraphPassId AddTessellation(ref RenderGraphEdit edit, RenderPipelines pipelines,
        in LayerStageResources stages, in RenderTessellationRasterData data, Material material,
        IndirectCommandLayout drawLayout, RenderRasterResourceBinder bindResource)
    {
        Shader shader = material.Shader!;
        Pipeline pipeline = CreatePipeline(pipelines, shader, "Layer hardware tessellation stage " + data.StageIndex, true);
        var draw = new IndexedDrawData(drawLayout, data.DrawArguments, data.DrawArgumentsOffset,
            data.PatternIndices, data.PatchIndices, data.Width, data.Height);
        GraphPassId id = edit.AddRasterFramePass(pipeline.Label!, PassQueueSelection.AnyOfType(QueueType.Graphics), draw,
            new PassOptions(RepeatCommands: true, RasterWidth: data.Width, RasterHeight: data.Height),
            IndexedDrawData.Declare, IndexedDrawData.Record, data.Region);
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline);
        pass.SetParameterBlock(shader.GetSlangReflection(shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget))
            .GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        foreach (ShaderParameterResource resource in ShaderParameterResources.Create(shader,
                     pipelines.Device.Capabilities.CompileTarget, "LayerRasterVertex", "LayerRasterPixel").Resources)
        {
            if (resource.ParameterLayout != VariableLayoutReflection.Null)
            {
                pass.BindParameterBlock(resource.ParameterLayout);
                continue;
            }
            if (!resource.IsUsed)
            {
                pass.Bind(GraphParameterResourceBinding.Null(resource.Type));
                continue;
            }
            if (stages.ResolveVisibility(resource.Name, resource.Type) is { } binding) pass.Bind(binding);
            else bindResource(ref edit, ref pass, in resource, PipelineSync.AllShading);
        }
        return id;
    }

    public static LayerHardwareRasterResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        in LayerStageResources stages, in LayerHardwareBinningResources hardware, RenderGeometryResources geometry,
        IRenderMaterialResources materials, scoped ReadOnlySpan<LayerRasterStage> rasterStages,
        IndirectCommandLayout drawLayout, uint width, uint height, IGraphResourceProvider? requestResources = null)
    {
        GraphRegionId region = edit.AddRegion("Layer hardware visibility", stages.VisibilityRegion);
        uint count = 0;
        foreach (LayerRasterStage stage in rasterStages) count = Math.Max(count, stage.StageIndex + 1u);
        var parameters = new GraphBufferId[count];
        var parameterViews = new GraphBufferCbvId[count];
        var passes = new GraphPassId[count];
        GraphBufferSrvId rasterViews = edit.CreateBufferSrv(geometry.PackedViews, structureStride: geometry.PackedViewStride);
        GraphBufferSrvId visible = edit.CreateBufferSrv(geometry.VisibleClusters, structureStride: 16);
        GraphBufferSrvId instances = edit.CreateBufferSrv(geometry.InstanceData);
        GraphBufferSrvId clusters = edit.CreateBufferSrv(hardware.Clusters, structureStride: 4);
        GraphBufferSrvId ranges = edit.CreateBufferSrv(hardware.Ranges, structureStride: 8);
        LayerStageResources stageResources = stages;
        uint alignment = pipelines.Device.Capabilities.Limits.ConstantBufferAlignment;
        foreach (LayerRasterStage stage in rasterStages)
        {
            Shader shader = stage.Material.Material.Shader!;
            Pipeline pipeline = CreatePipeline(pipelines, shader, "Layer hardware geometry stage " + stage.StageIndex);
            GraphBufferId values = edit.CreatePersistentBuffer(new((16u + alignment - 1u) / alignment * alignment,
                BufferUsages.Constant, "Layer hardware raster stage parameters"), MemoryType.Upload);
            GraphBufferCbvId view = edit.CreateBufferCbv(values);
            var draw = new DrawData(drawLayout, hardware.DrawArguments, (ulong)stage.StageIndex * 16u, width, height);
            GraphPassId passId = edit.AddRasterFramePass(pipeline.Label!, PassQueueSelection.AnyOfType(QueueType.Graphics), draw,
                new PassOptions(RepeatCommands: true, RasterWidth: width, RasterHeight: height),
                static (ref PassDefinition pass, ref DrawData data) => pass.Read(data.Arguments, new(data.Offset, 16),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument), DrawData.Record, region);
            PassDefinition pass = edit.GetPass(passId);
            pass.SetPipeline(pipeline);
            pass.SetParameterBlock(shader.GetSlangReflection(shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget))
                .GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
            GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
            {
                "LayerRasterStage" => GraphParameterResourceBinding.ConstantBuffer(view, PipelineSync.AllShading),
                "LayerRasterViews" => GraphParameterResourceBinding.ReadOnlyBuffer(rasterViews, PipelineSync.AllShading),
                "LayerVisibleClusters" => GraphParameterResourceBinding.ReadOnlyBuffer(visible,
                    PipelineSync.AllShading, BufferContentScope.CountBoundedRanges),
                "InstanceData" => GraphParameterResourceBinding.ReadOnlyBuffer(instances, PipelineSync.AllShading),
                "InstanceProperties" => GraphParameterResourceBinding.ConstantBuffer(geometry.InstanceProperties, PipelineSync.AllShading),
                "LayerHardwareClusters" => GraphParameterResourceBinding.ReadOnlyBuffer(clusters,
                    PipelineSync.VertexShading, BufferContentScope.CountBoundedPrefix),
                "LayerHardwareRanges" => GraphParameterResourceBinding.ReadOnlyBuffer(ranges, PipelineSync.VertexShading),
                _ => stageResources.ResolveVisibility(name, type),
            };
            foreach (ShaderParameterResource resource in ShaderParameterResources.Create(shader,
                         pipelines.Device.Capabilities.CompileTarget, "LayerRasterVertex", "LayerRasterPixel").Resources)
            {
                if (resource.ParameterLayout != VariableLayoutReflection.Null)
                {
                    pass.BindParameterBlock(resource.ParameterLayout);
                    continue;
                }
                if (!resource.IsUsed)
                {
                    pass.Bind(GraphParameterResourceBinding.Null(resource.Type));
                    continue;
                }
                if (Resolve(resource.Name, resource.Type) is { } binding) pass.Bind(binding);
                else if (resource.Name == "PageHeap") geometry.BindPageHeap(edit, ref pass, PipelineSync.AllShading);
                else if (requestResources is not null && requestResources.TryResolve(resource.Name, resource.Type, out GraphResourceBinder binder))
                    binder(ref edit, ref pass, PipelineSync.AllShading);
                else
                {
                    RenderMaterialEntry material = stage.Material;
                    materials.BindResource(ref edit, ref pass, in resource, in material, materials.GetUniforms(in material));
                }
            }
            materials.ReadTextures(ref edit, ref pass, stage.Material.TextureReads, PipelineSync.AllShading);
            edit.OrderAfter(passId, hardware.CompletePass);
            parameters[stage.StageIndex] = values;
            parameterViews[stage.StageIndex] = view;
            passes[stage.StageIndex] = passId;
        }
        GraphPassId complete = edit.AddComputeFramePass("Layer hardware visibility complete", PassQueueSelection.AnyCompatible(), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull), static (ref PassDefinition _, ref int _) => { },
            static (ref ComputePassCommandScope _, in int _) => { }, region);
        edit.OrderAfter(complete, hardware.CompletePass);
        foreach (GraphPassId pass in passes) if (pass != default) edit.OrderAfter(complete, pass);
        return new(region, parameters, parameterViews, passes, [rasterViews, visible, instances, clusters, ranges], complete);
    }

    public static void Record(ref RenderGraphFrame frame, in LayerHardwareRasterResources resources, RenderGeometryResources geometry)
    {
        for (int index = 0; index < resources.Parameters.Length; index++)
        {
            GraphBufferId buffer = resources.Parameters[index];
            if (buffer == default) continue;
            var values = new StageParameters((uint)index);
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(buffer), in values);
            PassDefinition pass = frame.GetPass(resources.Passes[index]);
            geometry.ReadPageHeap(ref frame, ref pass, PipelineSync.AllShading);
        }
    }

    public static void Remove(ref RenderGraphEdit edit, in LayerHardwareRasterResources resources)
    {
        edit.Remove(resources.Region);
        RemoveResources(ref edit, resources);
    }

    internal static void RemoveResources(ref RenderGraphEdit edit, in LayerHardwareRasterResources resources)
    {
        foreach (GraphBufferCbvId view in resources.ParameterViews) if (view != default) edit.Remove(view);
        foreach (GraphBufferId buffer in resources.Parameters) if (buffer != default) edit.Remove(buffer);
        foreach (GraphBufferSrvId view in resources.Srvs) edit.Remove(view);
    }

    private static Pipeline CreatePipeline(RenderPipelines pipelines, Shader shader, string label, bool tessellated = false)
    {
        int target = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
        EntryPointReflection vertex = shader.RequireSlangEntry(target, "LayerRasterVertex", SlangStage.Vertex);
        EntryPointReflection pixel = shader.RequireSlangEntry(target, "LayerRasterPixel", SlangStage.Fragment);
        return pipelines.CreateGraphics(new(shader.SlangCode, shader.GetSlangReflection(target), vertex,
            shader.GetSlangEntryBytes(target, vertex), pixel, shader.GetSlangEntryBytes(target, pixel),
            tessellated ? [new VertexBufferLayout(0, sizeof(uint), PerInstance: true)] : [],
            tessellated ? [new VertexAttribute(0, 0, Format.R32UInt, 0)] : [],
            PrimitiveTopology.TriangleList, StripCut.Disabled, new RasterizerState(Cull: CullType.None),
            new MultisampleState(), new DepthStencilState(), new BlendState([]), new AttachmentFormatSignature([], null, 1),
            DynamicStates.Viewport | DynamicStates.Scissor, label,
            vertexCodeBlob: shader.GetSlangEntryBlob(target, vertex), pixelCodeBlob: shader.GetSlangEntryBlob(target, pixel)))
            .GetAwaiter().GetResult();
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct StageParameters(uint RasterStage);

    private readonly record struct DrawData(IndirectCommandLayout Layout, GraphBufferId Arguments, ulong Offset, uint Width, uint Height)
    {
        internal static void Record(ref RasterPassCommandScope commands, in DrawData data)
        {
            commands.SetViewports([new(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new(0, 0, (int)data.Width, (int)data.Height)]);
            commands.ExecuteIndirect(data.Layout, new(commands.GetBuffer(data.Arguments), new(data.Offset, 16)), 1);
        }
    }

    private readonly record struct IndexedDrawData(IndirectCommandLayout Layout, GraphBufferId Arguments, ulong Offset,
        GraphBufferId PatternIndices, GraphBufferId PatchIndices, uint Width, uint Height)
    {
        private static ulong ArgumentsBytes => 8ul * (uint)Marshal.SizeOf<DrawIndexedArguments>();

        internal static void Declare(ref PassDefinition pass, ref IndexedDrawData data)
        {
            pass.Read(data.Arguments, new(data.Offset, ArgumentsBytes), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
            pass.Read(data.PatternIndices, BufferRange.Whole, PipelineSync.IndexInput, ResourceAccess.IndexBuffer);
            pass.Read(data.PatchIndices, BufferRange.Whole, PipelineSync.VertexShading, ResourceAccess.VertexBuffer,
                contentScope: BufferContentScope.CountBoundedPrefix);
        }

        internal static void Record(ref RasterPassCommandScope commands, in IndexedDrawData data)
        {
            commands.SetViewports([new(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new(0, 0, (int)data.Width, (int)data.Height)]);
            SomeEngine.Graphics.Buffer indices = commands.GetBuffer(data.PatternIndices);
            commands.SetIndexBuffer(new(indices, 0, indices.Info.Size, IndexType.UInt16));
            SomeEngine.Graphics.Buffer patches = commands.GetBuffer(data.PatchIndices);
            commands.SetVertexBuffers(0, [new(patches, 0, sizeof(uint), patches.Info.Size)]);
            commands.ExecuteIndirect(data.Layout, new(commands.GetBuffer(data.Arguments), new(data.Offset, ArgumentsBytes)), 8);
        }
    }
}
