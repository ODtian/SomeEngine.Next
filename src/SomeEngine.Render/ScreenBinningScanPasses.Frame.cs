using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

public static partial class ScreenBinningScanPasses
{
    /// <summary>Scans a current-frame prefix using work buffers sized to that prefix.</summary>
    public static ScreenBinningScanResources Add(ref RenderGraphFrame frame, RenderPipelines pipelines,
        Shader shader, GraphBufferId values, uint count, GraphPassId previous = default,
        GraphRegionId region = default,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?>? resolveResource = null)
    {
        var buffers = new List<GraphBufferId>();
        var views = new List<GraphBufferUavId>();
        var steps = new List<ScreenBinningScanStep>();
        LinkedComputePipeline scan = LinkedComputePipeline.Create(pipelines, shader, "ScreenBinningScan", "Screen binning prefix scan");
        LinkedComputePipeline add = LinkedComputePipeline.Create(pipelines, shader, "ScreenBinningAddGroupPrefixes", "Screen binning group prefixes");
        GraphPassId complete = AddFrameLevels(ref frame, shader, scan, add, values, count, previous,
            region, resolveResource, buffers, views, steps);
        return new(region, complete, buffers.ToArray(), views.ToArray(), steps.ToArray());
    }

    private static GraphPassId AddFrameLevels(ref RenderGraphFrame frame, Shader shader,
        LinkedComputePipeline scan, LinkedComputePipeline add, GraphBufferId values, uint count,
        GraphPassId previous, GraphRegionId region,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?>? resolveResource,
        List<GraphBufferId> buffers, List<GraphBufferUavId> views, List<ScreenBinningScanStep> steps)
    {
        uint groups = (count + 255u) / 256u;
        GraphBufferId sums = frame.CreateBuffer(new((ulong)groups * sizeof(uint),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Screen binning group sums"));
        buffers.Add(sums);
        GraphBufferUavId valuesView = frame.CreateBufferUav(values, new(0, (ulong)count * sizeof(uint)), structureStride: sizeof(uint));
        GraphBufferUavId sumsView = frame.CreateBufferUav(sums, structureStride: sizeof(uint));
        views.Add(valuesView);
        views.Add(sumsView);
        GraphPassId scanPass = AddFramePass(ref frame, shader, scan, values, sums, valuesView, sumsView,
            count, previous, region, resolveResource, GraphAccessMode.Write);
        previous = scanPass;
        GraphPassId addPass = default;
        if (groups > 1u)
        {
            previous = AddFrameLevels(ref frame, shader, scan, add, sums, groups, previous, region,
                resolveResource, buffers, views, steps);
            addPass = AddFramePass(ref frame, shader, add, values, sums, valuesView, sumsView,
                count, previous, region, resolveResource, GraphAccessMode.Read);
            previous = addPass;
        }
        steps.Add(new(scanPass, addPass, valuesView, sumsView));
        return previous;
    }

    private static GraphPassId AddFramePass(ref RenderGraphFrame frame, Shader shader,
        LinkedComputePipeline pipeline, GraphBufferId values, GraphBufferId sums,
        GraphBufferUavId valuesView, GraphBufferUavId sumsView, uint count,
        GraphPassId previous, GraphRegionId region,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?>? resolveResource, GraphAccessMode sumsAccess)
    {
        GraphParameterResourceBinding[] resources = ShaderEntryBindings.Resolve(shader, pipeline, (name, type) => name switch
        {
            "ScreenBinningScanValues" => GraphParameterResourceBinding.WritableBufferDescriptor(valuesView),
            "ScreenBinningScanSums" => GraphParameterResourceBinding.WritableBufferDescriptor(sumsView),
            _ => resolveResource?.Invoke(name, type),
        });
        uint groups = (count + 255u) / 256u;
        var dispatch = new DispatchArguments(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1u);
        GraphPassId id = frame.AddComputePass("Screen binning " + pipeline.EntryPoint.Name,
            PassQueueSelection.AnyCompatible(), dispatch, new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data), region);
        PassDefinition pass = frame.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding resource in resources) pass.Bind(resource);
        pass.ReadWrite(values, new(0, (ulong)count * sizeof(uint)), PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        if (sumsAccess == GraphAccessMode.Write)
            pass.Write(sums, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        else pass.Read(sums, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        if (previous != default) frame.OrderAfter(id, previous);
        return id;
    }
}
