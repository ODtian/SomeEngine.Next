using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

public static partial class ScreenBinningExpansionPasses
{
    /// <summary>Expands the current frame's actual item set inside an existing frame region.</summary>
    public static ScreenBinningExpansionResources Add(ref RenderGraphFrame frame, RenderPipelines pipelines,
        Shader sharedShader, Shader itemShader, IndirectCommandLayout dispatchLayout, uint itemCount,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveResource,
        GraphRegionId region = default)
    {
        var arguments = new ScreenBinningParameters(itemCount, itemCount, 0, 0, uint.MaxValue);
        GraphBufferSlice parameters = frame.Upload(in arguments, BufferUsages.Constant, "Screen binning coverage parameters");
        GraphBufferCbvId parametersView = frame.CreateBufferCbv(parameters);
        GraphBufferId offsets = frame.CreateBuffer(new((ulong)(itemCount + 1u) * sizeof(uint),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Screen binning coverage offsets"));
        GraphBufferId dispatch = frame.CreateBuffer(new(12,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.Indirect,
            "Screen binning coverage dispatch"));
        GraphBufferUavId offsetsView = frame.CreateBufferUav(offsets, structureStride: sizeof(uint));
        GraphBufferUavId dispatchView = frame.CreateBufferUav(dispatch, structureStride: sizeof(uint));
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
        {
            "ScreenBinningUniforms" => GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading),
            "ScreenBinningItemOffsets" => GraphParameterResourceBinding.WritableBufferDescriptor(offsetsView),
            "ScreenBinningDispatch" => GraphParameterResourceBinding.WritableBufferDescriptor(dispatchView),
            _ => resolveResource(name, type),
        };
        uint groups = (itemCount + 1u + 255u) / 256u;
        var countDispatch = new DispatchArguments(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1);
        GraphPassId count = AddFramePass(ref frame, pipelines, itemShader, "CountScreenItems", region,
            Resolve, new(countDispatch, default));
        frame.GetPass(count).Write(offsets, BufferRange.Whole, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        ScreenBinningScanResources scan = ScreenBinningScanPasses.Add(ref frame, pipelines, sharedShader,
            offsets, itemCount + 1u, count, region, Resolve);
        GraphPassId finish = AddFramePass(ref frame, pipelines, sharedShader, "ScreenBinningFinishExpansion", region,
            Resolve, new(new(1, 1, 1), default));
        PassDefinition finisher = frame.GetPass(finish);
        finisher.Read(offsets, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        finisher.Write(dispatch, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        frame.OrderAfter(finish, scan.CompletePass);
        GraphPassId complete = AddFramePass(ref frame, pipelines, itemShader, "ExpandScreenItems", region,
            Resolve, new(default, dispatch, dispatchLayout));
        PassDefinition expander = frame.GetPass(complete);
        expander.Read(offsets, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        expander.Read(dispatch, new(0, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        frame.OrderAfter(complete, finish);
        return new(region, parameters.Buffer, offsets, dispatch, complete, parametersView,
            [offsets, dispatch, .. scan.Buffers], [offsetsView, dispatchView, .. scan.Views], count);
    }

    private static GraphPassId AddFramePass(ref RenderGraphFrame frame, RenderPipelines pipelines,
        Shader shader, string entry, GraphRegionId region,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve, DispatchData data)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, "Screen binning " + entry);
        GraphParameterResourceBinding[] resources = ShaderEntryBindings.Resolve(shader, pipeline, resolve);
        GraphPassId id = frame.AddComputePass("Screen binning " + entry, PassQueueSelection.AnyCompatible(), data,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref DispatchData _) => { }, DispatchData.Record, region);
        PassDefinition pass = frame.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding resource in resources) pass.Bind(resource);
        return id;
    }
}
