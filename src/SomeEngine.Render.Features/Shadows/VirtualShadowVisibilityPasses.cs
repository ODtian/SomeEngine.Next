using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public readonly record struct VirtualShadowVisibilityResources(GraphRegionId Region,
    GraphPassId CompletePass, GraphBufferId Parameters, GraphBufferCbvId ParametersView,
    GraphTextureSrvId VisibilityView, GraphTextureSrvId DepthView, GraphBufferSrvId Views,
    GraphBufferSrvId VisibleClusters, GraphBufferSrvId InstanceData, GraphBufferCbvId InstanceProperties);

/// <summary>Requests opaque receiver pages from completed visibility and depth.</summary>
public static class VirtualShadowVisibilityPasses
{
    public static VirtualShadowVisibilityResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader shader, RenderGeometryResources geometry, GraphTextureId visibility, GraphTextureId depth,
        IGraphResourceProvider resources, uint width, uint height, uint viewCount, GraphRegionId region = default,
        string entryPoint = "RequestOpaqueShadowPages")
    {
        GraphRegionId requests = edit.AddRegion("Opaque visibility shadow requests", region);
        uint alignment = pipelines.Device.Capabilities.Limits.ConstantBufferAlignment;
        uint bytes = (16u + alignment - 1u) / alignment * alignment;
        GraphBufferId parameters = edit.CreatePersistentBuffer(new(bytes, BufferUsages.Constant,
            "Opaque visibility shadow request parameters"), MemoryType.Upload);
        GraphBufferCbvId parameterView = edit.CreateBufferCbv(parameters);
        GraphTextureSrvId visibilityView = edit.CreateTextureSrv(visibility, format: Format.R32UInt,
            dimension: TextureViewDimension.Texture2DArray);
        GraphTextureSrvId depthView = edit.CreateTextureSrv(depth,
            new TextureSubresourceRange(0, 1, 0, viewCount, TextureAspects.Depth), format: Format.D32Float,
            dimension: TextureViewDimension.Texture2DArray);
        GraphBufferSrvId views = edit.CreateBufferSrv(geometry.PackedViews, structureStride: geometry.PackedViewStride);
        GraphBufferSrvId visibleClusters = edit.CreateBufferSrv(geometry.VisibleClusters, structureStride: 16);
        GraphBufferSrvId instances = edit.CreateBufferSrv(geometry.InstanceData);
        GraphBufferCbvId properties = edit.CreateBufferCbv(geometry.InstanceProperties, geometry.InstancePropertiesRange);
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader,
            entryPoint, "Request opaque shadow pages from depth");
        DispatchArguments dispatch = new((width + 7u) / 8u, (height + 7u) / 8u, viewCount);
        GraphPassId passId = edit.AddComputeFramePass("Request opaque shadow pages from depth",
            PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch, new PassOptions(RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments arguments) => commands.Dispatch(arguments), requests);
        PassDefinition pass = edit.GetPass(passId);
        pass.SetPipeline(pipeline.Pipeline);
        foreach (ShaderParameterResource resource in ShaderParameterResources.Create(shader,
                     pipelines.Device.Capabilities.CompileTarget, pipeline.EntryPoint.Name).Resources)
        {
            if (resource.Type == ResourceBindingType.None)
            {
                pass.SetParameterBlock(resource.ParameterLayout);
                continue;
            }
            if (!resource.IsUsed)
            {
                pass.Bind(GraphParameterResourceBinding.Null(resource.Type));
                continue;
            }
            GraphParameterResourceBinding? binding = resource.Name switch
            {
                "VirtualShadowVisibilityRequests" => GraphParameterResourceBinding.ConstantBuffer(parameterView, PipelineSync.ComputeShading),
                "VisBuffer" => GraphParameterResourceBinding.SampledTexture(visibilityView, PipelineSync.ComputeShading),
                "DepthBuffer" => GraphParameterResourceBinding.SampledTexture(depthView, PipelineSync.ComputeShading),
                "Views" => GraphParameterResourceBinding.ReadOnlyBuffer(views, PipelineSync.ComputeShading),
                "VisibleClusters" => GraphParameterResourceBinding.ReadOnlyBuffer(visibleClusters,
                    PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix),
                "InstanceData" => GraphParameterResourceBinding.ReadOnlyBuffer(instances, PipelineSync.ComputeShading),
                "InstanceProperties" => GraphParameterResourceBinding.ConstantBuffer(properties, PipelineSync.ComputeShading),
                _ => null,
            };
            if (binding is { } value) pass.Bind(value);
            else if (resources.TryResolve(resource.Name, resource.Type, out GraphResourceBinder bind))
                bind(ref edit, ref pass, PipelineSync.ComputeShading);
            else throw new InvalidDataException($"Opaque shadow request resource '{resource.Name}' has no binding.");
        }
        edit.OrderAfter(passId, geometry.CompletePass);
        return new(requests, passId, parameters, parameterView, visibilityView, depthView, views,
            visibleClusters, instances, properties);
    }

    public static void Record(ref RenderGraphFrame frame, in VirtualShadowVisibilityResources resources,
        uint width, uint height, uint viewCount)
    {
        var parameters = new RequestParameters(width, height, viewCount);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);
        frame.SetPassDataIfChanged(resources.CompletePass, new DispatchArguments((width + 7u) / 8u, (height + 7u) / 8u, viewCount));
    }

    public static void Remove(ref RenderGraphEdit edit, in VirtualShadowVisibilityResources resources)
    {
        edit.Remove(resources.Region);
        edit.Remove(resources.VisibilityView);
        edit.Remove(resources.DepthView);
        edit.Remove(resources.Views);
        edit.Remove(resources.VisibleClusters);
        edit.Remove(resources.InstanceData);
        edit.Remove(resources.InstanceProperties);
        edit.Remove(resources.ParametersView);
        edit.Remove(resources.Parameters);
    }

    private readonly record struct RequestParameters(uint Width, uint Height, uint ViewCount, uint Padding = 0);
}
