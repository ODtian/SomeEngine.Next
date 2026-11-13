using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public readonly record struct VirtualShadowFilterRequestParameters(uint ViewCount,
    uint PageTableEntryCount, uint FrameIndex, uint EntriesPerView);

public readonly record struct VirtualShadowFilterRequestResources(GraphRegionId Region,
    GraphPassId CapturePass, GraphPassId CompletePass, GraphBufferId Parameters,
    GraphBufferId DispatchArguments, GraphBufferCbvId ParametersView,
    GraphBufferUavId DispatchView, GraphBufferSrvId DispatchRead,
    GraphBufferUavId SourcePages, GraphBufferUavId SourceCount);

/// <summary>Propagates filter requests from the request-list prefix captured before propagation.</summary>
public static class VirtualShadowFilterRequestPasses
{
    public static VirtualShadowFilterRequestResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader shader, IndirectCommandLayout dispatchLayout, GraphBufferId requestedPageIndices,
        GraphBufferId requestedPageCount, IGraphResourceProvider requests,
        GraphPassId requestsComplete = default, GraphRegionId region = default)
    {
        GraphRegionId propagation = edit.AddRegion("Virtual shadow filter requests", region);
        uint alignment = pipelines.Device.Capabilities.Limits.ConstantBufferAlignment;
        uint parameterBytes = (16u + alignment - 1u) / alignment * alignment;
        GraphBufferId parameters = edit.CreatePersistentBuffer(new(parameterBytes, BufferUsages.Constant,
            "Virtual shadow filter request parameters"), MemoryType.Upload);
        GraphBufferId dispatch = edit.CreatePersistentBuffer(new(16,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.Indirect,
            "Virtual shadow filter request dispatch"));
        GraphBufferCbvId parameterView = edit.CreateBufferCbv(parameters);
        GraphBufferUavId dispatchView = edit.CreateBufferUav(dispatch);
        GraphBufferSrvId dispatchRead = edit.CreateBufferSrv(dispatch);
        GraphBufferUavId sourcePages = edit.CreateBufferUav(requestedPageIndices, structureStride: 4);
        GraphBufferUavId sourceCount = edit.CreateBufferUav(requestedPageCount, structureStride: 4);
        GraphPassId capture = AddPass(ref edit, pipelines, shader, "CaptureVirtualShadowFilterRequests",
            new(default, null), requests, propagation, parameterView, dispatchView, dispatchRead, sourcePages, sourceCount);
        if (requestsComplete != default) edit.OrderAfter(capture, requestsComplete);
        GraphPassId complete = AddPass(ref edit, pipelines, shader, "PropagateVirtualShadowFilterRequestIndices",
            new(dispatch, dispatchLayout), requests, propagation, parameterView, dispatchView, dispatchRead, sourcePages, sourceCount);
        PassDefinition propagationPass = edit.GetPass(complete);
        propagationPass.Read(dispatch, new(0, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        edit.OrderAfter(complete, capture);
        return new(propagation, capture, complete, parameters, dispatch, parameterView, dispatchView, dispatchRead, sourcePages, sourceCount);
    }

    public static void Record(ref RenderGraphFrame frame, in VirtualShadowFilterRequestResources resources,
        in VirtualShadowFilterRequestParameters parameters)
        => MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);

    public static void Remove(ref RenderGraphEdit edit, in VirtualShadowFilterRequestResources resources)
    {
        edit.Remove(resources.Region);
        edit.Remove(resources.SourcePages); edit.Remove(resources.SourceCount);
        edit.Remove(resources.DispatchView); edit.Remove(resources.DispatchRead); edit.Remove(resources.ParametersView);
        edit.Remove(resources.DispatchArguments); edit.Remove(resources.Parameters);
    }

    private static GraphPassId AddPass(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, DispatchData data, IGraphResourceProvider requests, GraphRegionId region,
        GraphBufferCbvId parameters, GraphBufferUavId dispatch, GraphBufferSrvId dispatchRead,
        GraphBufferUavId sourcePages, GraphBufferUavId sourceCount)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        GraphPassId passId = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), data,
            new PassOptions(RepeatCommands: true), static (ref PassDefinition _, ref DispatchData _) => { },
            DispatchData.Record, region);
        PassDefinition pass = edit.GetPass(passId);
        pass.SetPipeline(pipeline.Pipeline);
        foreach (ShaderParameterResource resource in ShaderParameterResources.Create(shader,
                     pipelines.Device.Capabilities.CompileTarget, entry).Resources)
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
                "FilterRequests" => GraphParameterResourceBinding.ConstantBuffer(parameters, PipelineSync.ComputeShading),
                "FilterRequestDispatchArgs" => GraphParameterResourceBinding.WritableBuffer(dispatch,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                "FilterRequestArguments" => GraphParameterResourceBinding.ReadOnlyBuffer(dispatchRead, PipelineSync.ComputeShading),
                "FilterRequestPages" => GraphParameterResourceBinding.WritableBuffer(sourcePages,
                    PipelineSync.ComputeShading, GraphAccessMode.Read, contentScope: BufferContentScope.CountBoundedPrefix),
                "FilterRequestCount" => GraphParameterResourceBinding.WritableBuffer(sourceCount,
                    PipelineSync.ComputeShading, GraphAccessMode.Read),
                _ => null,
            };
            if (binding is { } value) pass.Bind(value);
            else if (requests.TryResolve(resource.Name, resource.Type, out GraphResourceBinder bind))
                bind(ref edit, ref pass, PipelineSync.ComputeShading);
            else throw new InvalidDataException($"Virtual shadow filter resource '{resource.Name}' has no binding.");
        }
        return passId;
    }

    private readonly record struct DispatchData(GraphBufferId Arguments, IndirectCommandLayout? Layout)
    {
        internal static void Record(ref ComputePassCommandScope commands, in DispatchData data)
        {
            if (data.Layout is null) commands.Dispatch(new(1, 1, 1));
            else commands.ExecuteIndirect(data.Layout, new(commands.GetBuffer(data.Arguments), new(0, 12)), 1);
        }
    }
}
