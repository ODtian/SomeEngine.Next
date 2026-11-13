using SomeEngine.Render;
using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Elements.Render;

/// <summary>Glyphs' shader-specific pipeline and instance bytes.</summary>
internal sealed class GlyphDrawing : IDisposable
{

    private readonly RenderPipelines _renderPipelines;
    private const int InstanceByteCount = 96;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private Shader _shader;
    private readonly Material _material;
    private readonly Format _format;
    private readonly Dictionary<(Format Format, CompareOperation Comparison), Pipeline> _depthPipelines = [];
    internal Pipeline Pipeline { get; private set; }
    internal VariableLayoutReflection Parameters { get; private set; }
    internal Sampler Sampler { get; }

    internal GlyphDrawing(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        GlyphsMaterial material,
        Format format)    {

        _renderPipelines = pipelines;
        _material = material;
        DevelopmentCheck.Require(material.Shader is not null, "A Glyphs material requires its shader.");
        _backend = backend;
        _device = device;
        _shader = material.Shader!;
        _format = format;
        Parameters = _shader.GetSlangReflection(_shader.RequireSlangTarget(device.Capabilities.CompileTarget))
            .GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Pipeline = CreatePipeline();
        try
        {
            Sampler = backend.CreateSampler(device, new SamplerDesc(FilterType.Linear, FilterType.Linear,
                FilterType.Nearest, AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge,
                Label: "Glyph atlas sampler"));
        }
        catch
        {
            
            throw;
        }
    }

    internal bool ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_shader, previous)) return false;
        _shader = next;
        _material.Shader = next;
        Pipeline = CreatePipeline();
        Parameters = next.GetSlangReflection(next.RequireSlangTarget(_device.Capabilities.CompileTarget))
            .GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _depthPipelines.Clear();
        return true;
    }

    private Pipeline CreatePipeline(Format? depthFormat = null, CompareOperation comparison = CompareOperation.LessOrEqual)
    {
        Shader shader = _shader;
        int target = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        EntryPointReflection vertex = shader.RequireSlangEntry(target, "vertexMain", SlangStage.Vertex);
        EntryPointReflection pixel = shader.RequireSlangEntry(target, "pixelMain", SlangStage.Fragment);
        return _renderPipelines.CreateGraphics(new GraphicsPipelineDesc(
            shader.SlangCode, reflection, vertex, shader.GetSlangEntryBytes(target, vertex),
            pixel, shader.GetSlangEntryBytes(target, pixel), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(),
            new DepthStencilState(DepthTest: depthFormat.HasValue, DepthWrite: false, DepthComparison: comparison),
            new BlendState([new BlendAttachmentState(
                Enabled: true, SourceColor: BlendFactor.One, DestinationColor: BlendFactor.OneMinusSourceAlpha,
                SourceAlpha: BlendFactor.One, DestinationAlpha: BlendFactor.OneMinusSourceAlpha)]),
            new AttachmentFormatSignature([_format], depthFormat), DynamicStates.Viewport | DynamicStates.Scissor,
            "Glyphs", vertexCodeBlob: shader.GetSlangEntryBlob(target, vertex),
            pixelCodeBlob: shader.GetSlangEntryBlob(target, pixel))).GetAwaiter().GetResult();
    }

    internal Pipeline GetPipeline(CanvasPassTarget target)
    {
        if (target.Depth == default) return Pipeline;
        var key = (target.DepthFormat, target.DepthComparison);
        if (!_depthPipelines.TryGetValue(key, out Pipeline? pipeline))
            _depthPipelines.Add(key, pipeline = CreatePipeline(target.DepthFormat, target.DepthComparison));
        return pipeline;
    }

    internal InstanceBuffer CreateInstances(global::SomeEngine.RenderGraph.RenderGraph graph, ReadOnlySpan<GlyphInstance> glyphs, InstanceBuffer? previous = null)
    {
        int byteCount = Math.Max(1, glyphs.Length) * InstanceByteCount;
        var bytes = new byte[byteCount];
        Span<Vector4> data = MemoryMarshal.Cast<byte, Vector4>(bytes.AsSpan());
        for (int index = 0; index < glyphs.Length; index++)
        {
            ref readonly GlyphInstance glyph = ref glyphs[index];
            int start = index * 6;
            data[start] = new(glyph.Rectangle.X, glyph.Rectangle.Y, glyph.Rectangle.Width, glyph.Rectangle.Height);
            data[start + 1] = new(glyph.UV0.X, glyph.UV0.Y, glyph.UV1.X, glyph.UV1.Y);
            data[start + 2] = new(glyph.Color.R, glyph.Color.G, glyph.Color.B, glyph.Color.A);
            data[start + 3] = new(glyph.ClipRectangle.X, glyph.ClipRectangle.Y,
                glyph.ClipRectangle.Width, glyph.ClipRectangle.Height);
            data[start + 4] = new(glyph.Matrix.M11, glyph.Matrix.M12, glyph.Matrix.M21, glyph.Matrix.M22);
            data[start + 5] = new(glyph.Matrix.M31, glyph.Matrix.M32, glyph.Alpha, 0);
        }
        return InstanceBuffer.Create(_backend, _device, graph, bytes, glyphs.Length, InstanceByteCount, "Glyph instances", previous);
    }

    public void Dispose()
    {
        Sampler.Dispose();
        
        _depthPipelines.Clear();
    }
}
