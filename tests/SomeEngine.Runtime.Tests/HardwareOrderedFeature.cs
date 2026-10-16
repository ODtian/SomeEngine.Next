using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Runtime.Tests;

internal sealed record HardwareOrderedDraw(Vector3[] Positions, Vector4 Color, MaterialState State)
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

    internal HardwareOrderedFeature(ApplicationRendering rendering, IReadOnlyList<HardwareOrderedDraw> draws, Matrix4x4 view,
        HardwareOrderedDraw? roundTripProbe = null)
    {
        ClusterStencil stencil = rendering.Stencil!;
        Shader shader = CreateShader(rendering.Device.Capabilities.CompileTarget);
        using RenderGraphEdit edit = rendering.Graph.BeginEdit();
        _color = edit.CreateTextureInput(); _depth = edit.CreateTextureInput();
        RenderGraphEdit mutable = edit;
        ClusterStencilAttachment attachment = stencil.Export(ref mutable);
        GraphRegionId region = stencil.Region;
        var extent = new DrawSize((uint)rendering.Width, (uint)rendering.Height, 3, 0, Vector4.Zero);
        LinkedRasterPipeline depthPipeline = Keep(LinkedRasterPipeline.Create(rendering.Backend, rendering.Device, shader,
            "DepthVertex", "DepthPixel", [], Format.D32FloatS8UInt, rasterizer: new(Cull: CullType.None),
            depthStencil: new(DepthTest: true, DepthWrite: true, DepthComparison: CompareOperation.Always),
            label: "Hardware reference scene depth"));
        VariableLayoutReflection depthLayout = depthPipeline.Reflection.GetGlobalParamsVarLayout()!.Value;
        _depthPass = AddDepth(ref mutable, attachment);
        GraphPassId previous = _depthPass;
        foreach (HardwareOrderedDraw draw in draws.OrderBy(draw => draw.State.DrawOrder))
            previous = AddMaterialDraw(ref mutable, draw, attachment.Texture, previous);
        CompletePass = attachment.Import(ref mutable, previous);
        if (roundTripProbe is not null)
        {
            ClusterStencilAttachment second = stencil.Export(ref mutable);
            GraphPassId depth = AddDepth(ref mutable, second);
            GraphPassId probe = AddMaterialDraw(ref mutable, roundTripProbe, second.Texture, depth);
            CompletePass = second.Import(ref mutable, probe);
        }
        edit.Commit();

        GraphPassId AddDepth(ref RenderGraphEdit graphEdit, ClusterStencilAttachment target)
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
            MaterialState state = draw.State;
            LinkedRasterPipeline pipeline = Keep(LinkedRasterPipeline.Create(rendering.Backend, rendering.Device, shader,
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
            GraphBufferId uniformId = graphEdit.RegisterExternalBuffer(uniforms,
                [new(BufferRange.Whole, PipelineSync.AllShading, ResourceAccess.ConstantBuffer, ResourceContentState.Defined)]);
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
    public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady) => graph.OrderAfter(_depthPass, colorReady);
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
        pass.Bind(edit.CreateTextureSrv(_depth, new(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float,
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

    private static Shader CreateShader(SlangCompileTarget compileTarget)
    {
        IGlobalSession global = Shader.CreateSlangGlobalSession();
        TargetDesc target = new() { Format = compileTarget,
            Profile = global.FindProfile(compileTarget == SlangCompileTarget.Dxil ? "sm_6_6" : "spirv_1_5") };
        global.CreateSession(new SessionDesc { Targets = [target], DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries = [new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0))] }, out ISession session);
        IModule unit = session.LoadModuleFromSource("ordered_hardware_reference", "ordered_hardware_reference.slang",
            Slang.CreateBlob(Source), out ISlangBlob? diagnostics) ?? throw new InvalidDataException(diagnostics?.AsString);
        var entries = new IEntryPoint[4];
        for (int index = 0; index < entries.Length; index++) unit.GetDefinedEntryPoint(index, out entries[index]);
        session.CreateCompositeComponentType([unit, .. entries], out IComponentType combined, out diagnostics);
        Assert.True(combined.Link(out IComponentType linked, out diagnostics).Succeeded, diagnostics?.AsString);
        return Shader.CreateFromSlang("Ordered hardware reference", global, session, [unit], entries, linked, [target], generateLibraryCode: false);
    }

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
