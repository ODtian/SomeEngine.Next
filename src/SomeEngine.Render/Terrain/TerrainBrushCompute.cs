using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

internal static class TerrainBrushCompute
{
    internal static GraphPassId Add<TParameters>(RenderTerrainData data, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension, GraphPassId previous, Shader shader, string entryPoint,
        TParameters parameters, DispatchArguments dispatch, scoped ReadOnlySpan<GraphParameterResourceBinding> bindings)
        where TParameters : unmanaged
    {
        LinkedComputePipeline pipeline = data.GetPipeline(shader, entryPoint);
        var state = new ComputeData(pipeline, frame.Upload(parameters, BufferUsages.Constant), bindings.ToArray(), dispatch);
        GraphPassId pass = extension.AddComputePass($"Terrain {entryPoint}", PassQueueSelection.AnyCompatible(), state,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref ComputeData value) =>
            {
                access.SetPipeline(value.Pipeline.Pipeline);
                access.SetParameterBlock(value.Pipeline.ParameterLayout);
                access.Bind(value.Parameters, PipelineSync.ComputeShading);
                foreach (GraphParameterResourceBinding binding in value.Bindings) access.Bind(binding);
            },
            static (ref ComputePassCommandScope commands, in ComputeData value) => commands.Dispatch(value.Dispatch));
        if (previous != default) extension.OrderAfter(pass, previous);
        return pass;
    }

    private readonly record struct ComputeData(LinkedComputePipeline Pipeline, GraphBufferSlice Parameters,
        GraphParameterResourceBinding[] Bindings, DispatchArguments Dispatch);
}
