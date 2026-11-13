using SomeEngine.Render;
using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Features.Layers;
using SomeEngine.Assets.Importers;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Runtime.Tests;

internal readonly record struct HardwareOrderedState
{
    internal byte DrawOrder { get; init; }
    internal bool TwoSided { get; init; }
    internal byte StencilRef { get; init; }
    internal BlendAttachmentState Blend { get; init; }
    internal Vector4 BlendConstant { get; init; }
    internal DepthStencilState DepthStencil { get; init; }

    public HardwareOrderedState()
    {
        this = default;
        Blend = new();
        DepthStencil = new();
    }
}

internal sealed record HardwareOrderedDraw(Vector3[] Positions, Vector4 Color, HardwareOrderedState State)
{
    internal bool Enabled { get; set; }
}

/// <summary>Draws the test's explicit triangles with fixed-function RHI blending and stencil.</summary>
internal sealed class HardwareOrderedFeature : IApplicationColorFeature
{
    private readonly List<IDisposable> _resources = [];
    private readonly List<(GraphPassId Pass, HardwareOrderedDraw Draw)> _draws = [];
    private readonly GraphTextureInput _color, _depth;
    private readonly GraphPassId _depthPass;
    private readonly GraphPassId _begin;

    internal HardwareOrderedFeature(ApplicationRendering rendering, IReadOnlyList<HardwareOrderedDraw> draws, Matrix4x4 view,
        HardwareOrderedDraw? roundTripProbe = null)
    {
        LayerComposition layers = rendering.Layers!;
        LayerStencil stencil = layers.Stencil;
        Shader shader = rendering.ShaderLibrary.CreateAsync("Ordered hardware reference", "ordered_hardware_reference.slang", Source)
            .AsTask().GetAwaiter().GetResult();
        Shader transfer = rendering.ShaderLibrary.GetAsync(rendering.Files, EngineShaderAssets.LayerStencilTransfer)
            .AsTask().GetAwaiter().GetResult();
        GraphRegionId region;
        RenderGraphEdit edit = rendering.Graph.BeginEdit();
        try
        {
            _color = edit.CreateTextureInput(); _depth = edit.CreateTextureInput();
            region = edit.AddRegion("Hardware ordered reference", layers.CompositeRegion);
            _begin = edit.AddComputeFramePass("Begin hardware ordered reference", PassQueueSelection.AnyCompatible(), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref ComputePassCommandScope _, in int _) => { }, region);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        LayerStencilAttachment attachment = stencil.Export(transfer, _begin, region);
        var extent = new DrawSize((uint)rendering.Width, (uint)rendering.Height, 3, 0, Vector4.Zero);
        LinkedRasterPipeline depthPipeline = Keep(LinkedRasterPipeline.Create(rendering.Pipelines, shader,
            "DepthVertex", "DepthPixel", [], Format.D32FloatS8UInt, rasterizer: new(Cull: CullType.None),
            depthStencil: new(DepthTest: true, DepthWrite: true, DepthComparison: CompareOperation.Always),
            label: "Hardware reference scene depth"));
        VariableLayoutReflection depthLayout = depthPipeline.Reflection.GetGlobalParamsVarLayout()!.Value;
        GraphPassId previous;
        edit = rendering.Graph.BeginEdit();
        try
        {
            _depthPass = AddDepth(ref edit, attachment);
            previous = _depthPass;
            foreach (HardwareOrderedDraw draw in draws.OrderBy(draw => draw.State.DrawOrder))
                previous = AddMaterialDraw(ref edit, draw, attachment.Texture, previous);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        CompletePass = attachment.Import(previous);
        if (roundTripProbe is not null)
        {
            LayerStencilAttachment second = stencil.Export(transfer, CompletePass, region);
            GraphPassId probe;
            edit = rendering.Graph.BeginEdit();
            try
            {
                GraphPassId depth = AddDepth(ref edit, second);
                probe = AddMaterialDraw(ref edit, roundTripProbe, second.Texture, depth);
                edit.Commit();
            }
            finally { edit.Dispose(); }
            CompletePass = second.Import(probe);
        }

        GraphPassId AddDepth(ref RenderGraphEdit graphEdit, LayerStencilAttachment target)
        {
            GraphPassId id = AddDraw(ref graphEdit, depthPipeline, depthLayout, extent, region);
            PassDefinition pass = graphEdit.GetPass(id);
            BindDepth(ref graphEdit, ref pass, depthLayout);
            GraphDepthStencilViewId depthTarget = graphEdit.CreateDepthStencilView(target.Texture,
                new(0, 1, 0, 1, TextureAspects.Depth | TextureAspects.Stencil), Format.D32FloatS8UInt,
                TextureViewDimension.Texture2D, readOnlyStencil: true);
            pass.DepthStencilAttachment(depthTarget, LoadType.Clear, StoreType.Store, WriteCoverage.Complete, 1,
                LoadType.Load, StoreType.Store, WriteCoverage.Partial, 0);
            graphEdit.OrderAfter(id, target.CompletePass);
            return id;
        }

        GraphPassId AddMaterialDraw(ref RenderGraphEdit graphEdit, HardwareOrderedDraw draw, GraphTextureId attachmentTexture,
            GraphPassId before)
        {
            HardwareOrderedState state = draw.State;
            LinkedRasterPipeline pipeline = Keep(LinkedRasterPipeline.Create(rendering.Pipelines, shader,
                "DrawVertex", "DrawPixel", [Format.R16G16B16A16Float], Format.D32FloatS8UInt,
                rasterizer: new(Cull: state.TwoSided ? CullType.None : CullType.Back), depthStencil: state.DepthStencil,
                blendAttachments: [state.Blend], label: "Hardware reference ordered material",
                additionalDynamicStates: DynamicStates.StencilReference | DynamicStates.BlendConstants));
            Buffer uniforms = Keep(rendering.Backend.CreateBuffer(rendering.Device, new(256, BufferUsages.Constant), MemoryType.Upload));
            using (MappedBuffer mapped = rendering.Backend.Map(uniforms, MapType.Write, BufferRange.Whole))
            {
                var values = new Vector4[7];
                Matrix4x4 matrix = view * rendering.Projection;
                for (int vertex = 0; vertex < draw.Positions.Length; vertex++)
                    values[vertex] = Vector4.Transform(new Vector4(draw.Positions[vertex], 1), matrix);
                values[6] = draw.Color;
                MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(mapped.Bytes);
                mapped.Flush(mapped.Range);
            }
            GraphBufferId uniformId = graphEdit.RegisterExternalBuffer(uniforms);
            VariableLayoutReflection layout = pipeline.Reflection.GetGlobalParamsVarLayout()!.Value;
            var size = extent with { Vertices = (uint)draw.Positions.Length, StencilRef = state.StencilRef, BlendConstant = state.BlendConstant };
            GraphPassId id = AddDraw(ref graphEdit, pipeline, layout, size, region);
            _draws.Add((id, draw));
            PassDefinition pass = graphEdit.GetPass(id);
            pass.BindParameterBlock(layout);
            pass.Bind(graphEdit.CreateBufferCbv(uniformId), PipelineSync.VertexShading | PipelineSync.PixelShading);
            pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.TextureSrv));
            GraphColorAttachmentViewId target = graphEdit.CreateColorAttachmentView(_color, new(0, 1, 0, 1, TextureAspects.Color),
                Format.R16G16B16A16Float, TextureViewDimension.Texture2D);
            pass.ColorAttachment(0, target, LoadType.Load, StoreType.Store, WriteCoverage.Partial, Vector4.Zero);
            GraphDepthStencilViewId tests = graphEdit.CreateDepthStencilView(attachmentTexture,
                new(0, 1, 0, 1, TextureAspects.Depth | TextureAspects.Stencil), Format.D32FloatS8UInt, TextureViewDimension.Texture2D,
                readOnlyDepth: !state.DepthStencil.DepthWrite,
                readOnlyStencil: !state.DepthStencil.StencilTest || state.DepthStencil.StencilWriteMask == 0);
            pass.DepthStencilAttachment(tests, LoadType.Load, StoreType.Store, WriteCoverage.Partial, 1,
                LoadType.Load, StoreType.Store, WriteCoverage.Partial, 0);
            graphEdit.OrderAfter(id, before);
            return id;
        }
    }

    public GraphPassId CompletePass { get; }
    public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady) => graph.OrderAfter(_begin, colorReady);
    public GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth)
    {
        graph.SetTextureInput(_color, color); graph.SetTextureInput(_depth, depth);
        foreach ((GraphPassId pass, HardwareOrderedDraw draw) in _draws) graph.SetPassEnabled(pass, draw.Enabled);
        return color;
    }
    public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }

    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }

    private void BindDepth(ref RenderGraphEdit edit, ref PassDefinition pass, VariableLayoutReflection layout)
    {
        pass.BindParameterBlock(layout);
        pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.ConstantBuffer));
        pass.Bind(edit.CreateTextureSrv(_depth, new(0, 1, 0, 1, TextureAspects.Color), Format.R32Float,
            TextureViewDimension.Texture2DArray), PipelineSync.PixelShading);
    }

    private static GraphPassId AddDraw(ref RenderGraphEdit edit, LinkedRasterPipeline pipeline,
        VariableLayoutReflection layout, DrawSize size, GraphRegionId region) =>
        edit.AddRasterFramePass("Hardware ordered image reference", PassQueueSelection.AnyOfType(QueueType.Graphics), size,
            new PassOptions(RepeatCommands: true), pipeline.Pipeline, layout, default,
            static (ref PassDefinition pass, ref DrawSize size) => pass.DefineRasterExtent(size.Width, size.Height),
            static (ref RasterPassCommandScope commands, in DrawSize size) =>
            {
                commands.SetViewports([new(0, 0, size.Width, size.Height)]);
                commands.SetScissors([new(0, 0, (int)size.Width, (int)size.Height)]);
                commands.SetStencilReference(size.StencilRef); commands.SetBlendConstants(size.BlendConstant);
                commands.Draw(new(size.Vertices, 1, 0, 0));
            }, region: region);

    private readonly record struct DrawSize(uint Width, uint Height, uint Vertices, uint StencilRef, Vector4 BlendConstant);

    private const string Source = """
        struct DrawParameters { float4 Positions[6]; float4 Color; };
        ConstantBuffer<DrawParameters> DrawUniforms;
        Texture2DArray<float> ReferenceDepth;
        struct Pixel { float4 Position : SV_Position; };
        [shader("vertex")] Pixel DepthVertex(uint vertex : SV_VertexID)
        {
            Pixel result;
            float2 xy = vertex == 0u ? float2(-1,-1) : vertex == 1u ? float2(-1,3) : float2(3,-1);
            result.Position = float4(xy, 0, 1); return result;
        }
        [shader("fragment")] float DepthPixel(Pixel pixel) : SV_Depth
        { return ReferenceDepth.Load(int4(uint2(pixel.Position.xy),0,0)); }
        [shader("vertex")] Pixel DrawVertex(uint vertex : SV_VertexID)
        { Pixel result; result.Position = DrawUniforms.Positions[vertex]; return result; }
        [shader("fragment")] float4 DrawPixel(Pixel pixel) : SV_Target0
        { return DrawUniforms.Color; }
        """;
}
