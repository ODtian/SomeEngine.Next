using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

public readonly record struct ScreenBinningExpansionResources(GraphRegionId Region, GraphBufferId Parameters,
    GraphBufferId ItemOffsets, GraphBufferId Dispatch, GraphPassId CompletePass, GraphBufferCbvId ParametersView,
    GraphBufferId[] Buffers, GraphBufferUavId[] Views, GraphPassId FirstPass = default);

/// <summary>Registers count, shared prefix scan and one output operation per covered coordinate.</summary>
public static partial class ScreenBinningExpansionPasses
{
    public static ScreenBinningExpansionResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader sharedShader, Shader itemShader, IndirectCommandLayout dispatchLayout, uint itemCapacity,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveResource,
        GraphRegionId region = default)
    {
        GraphRegionId expansionRegion = edit.AddRegion("Screen binning expansion", region);
        uint alignment = Math.Max(1u, pipelines.Device.Capabilities.Limits.ConstantBufferAlignment);
        uint parameterSize = (ScreenBinningParameters.SizeInBytes + alignment - 1u) / alignment * alignment;
        GraphBufferId parameters = edit.CreatePersistentBuffer(new BufferDesc(parameterSize,
            BufferUsages.Constant, "Screen binning coverage parameters"), MemoryType.Upload);
        GraphBufferId offsets = edit.CreatePersistentBuffer(new BufferDesc((ulong)(itemCapacity + 1u) * 4u,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Screen binning coverage offsets"));
        GraphBufferId dispatch = edit.CreatePersistentBuffer(new BufferDesc(12,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.Indirect,
            "Screen binning coverage dispatch"));
        GraphBufferCbvId parametersView = edit.CreateBufferCbv(parameters);
        GraphBufferUavId offsetsView = edit.CreateBufferUav(offsets, structureStride: 4);
        GraphBufferUavId dispatchView = edit.CreateBufferUav(dispatch, structureStride: 4);
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
        {
            "ScreenBinningUniforms" => GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading),
            "ScreenBinningItemOffsets" => GraphParameterResourceBinding.WritableBufferDescriptor(offsetsView),
            "ScreenBinningDispatch" => GraphParameterResourceBinding.WritableBufferDescriptor(dispatchView),
            _ => resolveResource(name, type),
        };
        uint groups = (itemCapacity + 1u + 255u) / 256u;
        var countDispatch = new DispatchArguments(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1);
        GraphPassId count = AddPass(ref edit, pipelines, itemShader, "CountScreenItems", expansionRegion,
            Resolve, new(countDispatch, default));
        PassDefinition counter = edit.GetPass(count);
        counter.Write(offsets, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        ScreenBinningScanResources scan = ScreenBinningScanPasses.Add(ref edit, pipelines, sharedShader,
            offsets, itemCapacity + 1u, count, expansionRegion, Resolve);
        GraphPassId finish = AddPass(ref edit, pipelines, sharedShader, "ScreenBinningFinishExpansion", expansionRegion,
            Resolve, new(new(1, 1, 1), default));
        PassDefinition finisher = edit.GetPass(finish);
        finisher.Read(offsets, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        finisher.Write(dispatch, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        edit.OrderAfter(finish, scan.CompletePass);
        GraphPassId complete = AddPass(ref edit, pipelines, itemShader, "ExpandScreenItems", expansionRegion,
            Resolve, new(default, dispatch, dispatchLayout));
        PassDefinition expander = edit.GetPass(complete);
        expander.Read(offsets, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        expander.Read(dispatch, new(0, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        edit.OrderAfter(complete, finish);
        return new(expansionRegion, parameters, offsets, dispatch, complete, parametersView,
            [parameters, offsets, dispatch, .. scan.Buffers], [offsetsView, dispatchView, .. scan.Views], count);
    }

    public static void SetFrameParameters(ref RenderGraphFrame frame, in ScreenBinningExpansionResources resources,
        uint itemCount, uint itemCapacity)
    {
        var parameters = new ScreenBinningParameters(itemCount, itemCapacity, 0, 0, uint.MaxValue);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);
    }

    public static void Remove(ref RenderGraphEdit edit, in ScreenBinningExpansionResources resources)
    {
        edit.Remove(resources.Region);
        foreach (GraphBufferUavId view in resources.Views) edit.Remove(view);
        edit.Remove(resources.ParametersView);
        foreach (GraphBufferId buffer in resources.Buffers) edit.Remove(buffer);
    }

    private static GraphPassId AddPass(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, GraphRegionId region, Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve,
        DispatchData data)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, "Screen binning " + entry);
        GraphParameterResourceBinding[] resources = ShaderEntryBindings.Resolve(shader, pipeline, resolve);
        GraphPassId id = edit.AddComputeFramePass("Screen binning " + entry, PassQueueSelection.AnyCompatible(), data,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchData _) => { }, DispatchData.Record, region);
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding resource in resources) pass.Bind(resource);
        return id;
    }

    private readonly record struct DispatchData(DispatchArguments Arguments, GraphBufferId Indirect, IndirectCommandLayout? Layout = null)
    {
        internal static void Record(ref ComputePassCommandScope commands, in DispatchData data)
        {
            if (data.Indirect == default) commands.Dispatch(data.Arguments);
            else commands.ExecuteIndirect(data.Layout!, new BufferRegion(commands.GetBuffer(data.Indirect), new(0, 12)), 1);
        }
    }
}
