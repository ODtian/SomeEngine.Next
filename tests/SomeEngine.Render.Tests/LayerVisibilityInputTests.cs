using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class LayerVisibilityInputTests
{
    [Fact]
    public async Task SoftwareVisibilityReceivesTheMainViewAndTheGeometryViewSeparately()
    {
        using var graphics = new BinningTestDevice();
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "cluster_raster_shared.slang")))
            directory = directory.Parent!;
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [Path.Combine(directory.FullName, "assets", "Shaders")];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader shader = await shaders.CreateAsync("Layer visibility inputs", "layer_visibility_input_checks.slang", ShaderSource);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        var result = new ViewReadback();
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            GraphBufferId values = edit.CreatePersistentBuffer(new(64 * 8,
                BufferUsages.ShaderWrite | BufferUsages.CopySource, "Visibility input views"));
            GraphBufferUavId view = edit.CreateBufferUav(values, structureStride: 8);
            GraphPassId initialize = Add(ref edit, graphics.Pipelines, shader, "InitializeVisibilityViews",
                GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete));
            GraphPassId raster = Add(ref edit, graphics.Pipelines, shader, "ObserveLayerVisibilityViews",
                GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading));
            edit.OrderAfter(raster, initialize);
            GraphReadbackId readback = edit.AddReadback(values, BufferRange.Whole, result, "Read visibility input views");
            edit.OrderAfter(readback, raster);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        RenderGraphFrame frame = graph.BeginFrame();
        ulong identity;
        try
        {
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.Equal(2u, result.Values[(1 * 8 + 1) * 2]);
        Assert.Equal(7u, result.Values[(1 * 8 + 1) * 2 + 1]);
    }

    private static GraphPassId Add(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, GraphParameterResourceBinding output)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), new DispatchArguments(1, 1, 1),
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments groups) => commands.Dispatch(groups));
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline,
            (name, _) => name == "ObservedVisibilityViews" ? output : null)) pass.Bind(binding);
        return id;
    }

    private sealed class ViewReadback : IGraphReadbackReceiver
    {
        internal uint[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    private const string ShaderSource = """
        __exported import cluster_raster_shared;
        RWStructuredBuffer<uint2> ObservedVisibilityViews;
        struct ViewVisibility : IVisibilityEvaluate<float3>
        {
            typealias Varying = EmptyVisibilityVarying;
            Varying makeVarying(float3 vertex, VisibilityVertexInput input) { return {}; }
            Varying readLane(Varying value, uint lane) { return {}; }
            Varying interpolateVertex(Varying first, Varying second, Varying third, float3 bary) { return {}; }
            Varying interpolatePixel(Varying first, Varying second, Varying third, VisibilityInterpolationInput input) { return {}; }
            VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
            {
                ObservedVisibilityViews[input.PixelCoord.y * 8u + input.PixelCoord.x] =
                    uint2(input.PixelCoord.z, input.ViewIndex);
                VisibilityPixelResult result = { 1u, input.Depth };
                return result;
            }
        };
        struct EmptyRasterOutput : IClusterRasterOutput
        {
            static bool UsesReverseDepth() { return false; }
            int4 scissor(uint visible, uint view, uint width, uint height) { return int4(0,0,width,height); }
            bool resolvePixel(int2 pixel, uint view, uint identity, out ClusterRasterOutputPixel result)
            {
                result = {};
                result.Position = pixel;
                return true;
            }
            uint readDepth(ClusterRasterOutputPixel pixel, uint view) { return 0u; }
            void write(ClusterRasterOutputPixel pixel, uint view, uint targetView, float depth, uint identity) { }
        };
        [shader("compute")][numthreads(64,1,1)]
        void InitializeVisibilityViews(uint3 id : SV_DispatchThreadID)
        {
            ObservedVisibilityViews[id.x] = uint2(0xffffffffu);
        }
        [shader("compute")][numthreads(32,1,1)]
        void ObserveLayerVisibilityViews(uint lane : SV_GroupIndex)
        {
            float4 clips[3] = { float4(-1,1,.4,1), float4(1,1,.4,1), float4(-1,-1,.4,1) };
            EmptyVisibilityVarying varyings[3] = {};
            DrawRequest request = MakeDrawRequest(0u, 0u, 0u, 7u);
            RasterizeClusterTriangleWave<float3, ViewVisibility, EmptyRasterOutput>(ViewVisibility(), EmptyRasterOutput(),
                clips, varyings, int4(0,0,8,8), 0u, request, 0u, 7u, 2u,
                lane == 0u, 8u, 8u, true, 0u, false, 2u);
        }
        """;
}
