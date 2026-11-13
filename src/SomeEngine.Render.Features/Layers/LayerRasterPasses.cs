using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Layers;

/// <summary>A geometry-compatible stage and the material providing its vertex, domain and visibility inputs.</summary>
public readonly record struct LayerRasterStage(uint StageIndex, RenderMaterialEntry Material);

public readonly record struct LayerSoftwareRasterResources(GraphRegionId Region, GraphBufferId[] Parameters,
    GraphBufferCbvId[] ParameterViews, GraphPassId[] Passes, GraphBufferSrvId[] Srvs, GraphPassId CompletePass);

/// <summary>Registers the baked software visibility entry once per supplied geometry stage.</summary>
public static class LayerRasterPasses
{
    public static LayerSoftwareRasterResources AddSoftware(ref RenderGraphEdit edit, RenderPipelines pipelines,
        in LayerStageResources stages, in LayerTileBinningResources tiles, RenderGeometryResources geometry,
        IRenderMaterialResources materials, scoped ReadOnlySpan<LayerRasterStage> rasterStages,
        IndirectCommandLayout dispatchLayout, IGraphResourceProvider? requestResources = null)
    {
        GraphRegionId region = edit.AddRegion("Layer software visibility", stages.VisibilityRegion);
        uint count = 0;
        foreach (LayerRasterStage stage in rasterStages) count = Math.Max(count, stage.StageIndex + 1u);
        var parameters = new GraphBufferId[count];
        var parameterViews = new GraphBufferCbvId[count];
        var passes = new GraphPassId[count];
        GraphBufferSrvId rasterViews = edit.CreateBufferSrv(geometry.PackedViews, structureStride: geometry.PackedViewStride);
        // VisibleCluster is the engine's uint4 visibility record, independent of material vertex data.
        GraphBufferSrvId visible = edit.CreateBufferSrv(geometry.VisibleClusters, structureStride: 16);
        GraphBufferSrvId instances = edit.CreateBufferSrv(geometry.InstanceData);
        GraphBufferSrvId clusters = edit.CreateBufferSrv(tiles.StageClusters, structureStride: 4);
        GraphBufferSrvId works = edit.CreateBufferSrv(tiles.StageWorks, structureStride: 16);
        GraphBufferSrvId ranges = edit.CreateBufferSrv(tiles.StageWorkRanges, structureStride: 8);
        LayerStageResources stageResources = stages;
        LayerTileBinningResources tileResources = tiles;
        uint alignment = Math.Max(1u, pipelines.Device.Capabilities.Limits.ConstantBufferAlignment);
        foreach (LayerRasterStage stage in rasterStages)
        {
            Shader shader = stage.Material.Material.Shader!.RequireSpecialization("LayerRasterSoftware",
                ["LayerShadowPageRequests<32>"]);
            LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, "LayerRasterSoftware",
                "Rasterize layer geometry stage " + stage.StageIndex);
            GraphBufferId values = edit.CreatePersistentBuffer(new((16u + alignment - 1u) / alignment * alignment,
                BufferUsages.Constant, "Layer raster stage parameters"), MemoryType.Upload);
            GraphBufferCbvId view = edit.CreateBufferCbv(values);
            var dispatch = new RasterDispatch(dispatchLayout, tiles.StageDispatch, (ulong)stage.StageIndex * 12u);
            GraphPassId passId = edit.AddComputeFramePass(pipeline.Label, PassQueueSelection.AnyCompatible(), dispatch,
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref RasterDispatch data) =>
                    pass.Read(data.Arguments, new(data.Offset, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                RasterDispatch.Record, region);
            PassDefinition pass = edit.GetPass(passId);
            pass.SetPipeline(pipeline.Pipeline);
            pass.SetParameterBlock(pipeline.ParameterLayout);
            GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
            {
                "LayerRasterStage" => GraphParameterResourceBinding.ConstantBuffer(view, PipelineSync.ComputeShading),
                "LayerRasterViews" => GraphParameterResourceBinding.ReadOnlyBuffer(rasterViews, PipelineSync.ComputeShading),
                "LayerVisibleClusters" => GraphParameterResourceBinding.ReadOnlyBuffer(visible,
                    PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges),
                "InstanceData" => GraphParameterResourceBinding.ReadOnlyBuffer(instances, PipelineSync.ComputeShading),
                "InstanceProperties" => GraphParameterResourceBinding.ConstantBuffer(geometry.InstanceProperties, PipelineSync.ComputeShading),
                "LayerStageClusters" => StageBuffer(type, clusters, tileResources.StageClustersView, BufferContentScope.CountBoundedRanges),
                "LayerStageWorks" => StageBuffer(type, works, tileResources.StageWorksView, BufferContentScope.CountBoundedRanges),
                "LayerStageWorkRanges" => StageBuffer(type, ranges, tileResources.StageWorkRangesView, BufferContentScope.FullRange),
                _ => stageResources.ResolveVisibility(name, type),
            };
            foreach (ShaderParameterResource resource in ShaderParameterResources.Create(shader,
                pipelines.Device.Capabilities.CompileTarget, "LayerRasterSoftware").Resources)
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
                if (Resolve(resource.Name, resource.Type) is { } binding)
                {
                    pass.Bind(binding);
                    continue;
                }
                if (resource.Name == "PageHeap")
                {
                    geometry.BindPageHeap(edit, ref pass, PipelineSync.ComputeShading);
                    continue;
                }
                if (requestResources is not null && requestResources.TryResolve(resource.Name, resource.Type, out GraphResourceBinder binder))
                {
                    binder(ref edit, ref pass, PipelineSync.ComputeShading);
                    continue;
                }
                RenderMaterialEntry material = stage.Material;
                materials.BindResource(ref edit, ref pass, in resource, in material, materials.GetUniforms(in material));
            }
            materials.ReadTextures(ref edit, ref pass, stage.Material.TextureReads, PipelineSync.ComputeShading);
            edit.OrderAfter(passId, tiles.CompletePass);
            parameters[stage.StageIndex] = values;
            parameterViews[stage.StageIndex] = view;
            passes[stage.StageIndex] = passId;
        }
        GraphPassId complete = edit.AddComputeFramePass("Layer software visibility complete", PassQueueSelection.AnyCompatible(), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull), static (ref PassDefinition _, ref int _) => { },
            static (ref ComputePassCommandScope _, in int _) => { }, region);
        edit.OrderAfter(complete, tiles.CompletePass);
        foreach (GraphPassId pass in passes) if (pass != default) edit.OrderAfter(complete, pass);
        return new(region, parameters, parameterViews, passes, [rasterViews, visible, instances, clusters, works, ranges], complete);
    }

    public static void RecordSoftware(ref RenderGraphFrame frame, in LayerSoftwareRasterResources resources,
        RenderGeometryResources geometry)
    {
        for (int index = 0; index < resources.Parameters.Length; index++)
        {
            GraphBufferId buffer = resources.Parameters[index];
            if (buffer == default) continue;
            var values = new StageParameters((uint)index);
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(buffer), in values);
            PassDefinition pass = frame.GetPass(resources.Passes[index]);
            geometry.ReadPageHeap(ref frame, ref pass, PipelineSync.ComputeShading);
        }
    }

    public static void RemoveSoftware(ref RenderGraphEdit edit, in LayerSoftwareRasterResources resources)
    {
        edit.Remove(resources.Region);
        RemoveResources(ref edit, resources);
    }

    internal static void RemoveResources(ref RenderGraphEdit edit, in LayerSoftwareRasterResources resources)
    {
        foreach (GraphBufferCbvId view in resources.ParameterViews) if (view != default) edit.Remove(view);
        foreach (GraphBufferId buffer in resources.Parameters) if (buffer != default) edit.Remove(buffer);
        foreach (GraphBufferSrvId view in resources.Srvs) edit.Remove(view);
    }

    private static GraphParameterResourceBinding StageBuffer(ResourceBindingType type, GraphBufferSrvId srv,
        GraphBufferUavId uav, BufferContentScope scope) => type == ResourceBindingType.BufferSrv
        ? GraphParameterResourceBinding.ReadOnlyBuffer(srv, PipelineSync.ComputeShading, scope)
        : GraphParameterResourceBinding.WritableBuffer(uav, PipelineSync.ComputeShading,
            GraphAccessMode.Read, contentScope: scope);

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct StageParameters(uint RasterStage);

    private readonly record struct RasterDispatch(IndirectCommandLayout Layout, GraphBufferId Arguments, ulong Offset)
    {
        internal static void Record(ref ComputePassCommandScope commands, in RasterDispatch data) =>
            commands.ExecuteIndirect(data.Layout, new BufferRegion(commands.GetBuffer(data.Arguments), new(data.Offset, 12)), 1);
    }
}
