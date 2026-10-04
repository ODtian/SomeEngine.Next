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
    private const int InstanceByteCount = 64;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    internal Pipeline Pipeline { get; }
    internal VariableLayoutReflection Parameters { get; }
    internal Sampler Sampler { get; }

    internal GlyphDrawing(IGraphicsBackend backend, Device device, GlyphsMaterial material, Format format)
    {
        DevelopmentCheck.Require(material.Shader is not null, "A Glyphs material requires its shader.");
        _backend = backend;
        _device = device;
        Shader shader = material.Shader!;
        int target = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        EntryPointReflection vertex = shader.RequireSlangEntry(target, "vertexMain", SlangStage.Vertex);
        EntryPointReflection pixel = shader.RequireSlangEntry(target, "pixelMain", SlangStage.Fragment);
        Parameters = reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Pipeline = backend.CreateGraphicsPipeline(device, new GraphicsPipelineDesc(
            shader.SlangCode, reflection, vertex, shader.GetSlangEntryBytes(target, vertex),
            pixel, shader.GetSlangEntryBytes(target, pixel), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(),
            new DepthStencilState(), new BlendState([new BlendAttachmentState(
                Enabled: true, SourceColor: BlendFactor.One, DestinationColor: BlendFactor.OneMinusSourceAlpha,
                SourceAlpha: BlendFactor.One, DestinationAlpha: BlendFactor.OneMinusSourceAlpha)]),
            new AttachmentFormatSignature([format], null), DynamicStates.Viewport | DynamicStates.Scissor,
            "Glyphs", vertexCodeBlob: shader.GetSlangEntryBlob(target, vertex),
            pixelCodeBlob: shader.GetSlangEntryBlob(target, pixel)));
        Sampler = backend.CreateSampler(device, new SamplerDesc(FilterType.Linear, FilterType.Linear,
            FilterType.Nearest, AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge,
            Label: "Glyph atlas sampler"));
    }

    internal Buffer CreateInstances(ReadOnlySpan<GlyphInstance> glyphs)
    {
        ulong byteCount = (ulong)Math.Max(1, glyphs.Length) * InstanceByteCount;
        Buffer buffer = _backend.CreateBuffer(_device, new BufferDesc(byteCount, BufferUsages.ShaderRead,
            "Glyph instances"), MemoryType.Upload);
        try
        {
            using MappedBuffer mapped = _backend.Map(buffer, MapType.Write, BufferRange.Whole);
            mapped.Bytes.Clear();
            Span<Vector4> data = MemoryMarshal.Cast<byte, Vector4>(mapped.Bytes);
            for (int index = 0; index < glyphs.Length; index++)
            {
                ref readonly GlyphInstance glyph = ref glyphs[index];
                int start = index * 4;
                data[start] = new(glyph.Rectangle.X, glyph.Rectangle.Y, glyph.Rectangle.Width, glyph.Rectangle.Height);
                data[start + 1] = new(glyph.UV0.X, glyph.UV0.Y, glyph.UV1.X, glyph.UV1.Y);
                data[start + 2] = new(glyph.Color.R, glyph.Color.G, glyph.Color.B, glyph.Color.A);
                data[start + 3] = new(glyph.ClipRectangle.X, glyph.ClipRectangle.Y,
                    glyph.ClipRectangle.Width, glyph.ClipRectangle.Height);
            }
            mapped.Flush(new BufferRange(0, byteCount));
            return buffer;
        }
        catch { buffer.Dispose(); throw; }
    }

    internal GraphBufferSrvId CreateDrawView(ref RenderGraphFrame frame, GraphBufferId instances, CanvasDraw draw) =>
        frame.CreateBufferSrv(instances, new BufferRange((ulong)draw.First * InstanceByteCount,
            (ulong)draw.Count * InstanceByteCount), label: "Glyph draw range");

    public void Dispose()
    {
        Sampler.Dispose();
        Pipeline.Dispose();
    }
}

internal readonly record struct GlyphPassData(Pipeline Pipeline, VariableLayoutReflection Parameters,
    Sampler Sampler, GraphBufferSrvId Instances, GraphBufferId Canvas, GraphTextureSrvId[] Pages, int DistanceSpread)
{
    internal void Draw(ref RasterPassCommandScope commands, CanvasDraw draw)
    {
        commands.SetPipeline(Pipeline);
        ResourceBinding[] resources =
        [
            ResourceBinding.ReadOnlyBuffer(commands.GetBufferSrv(draw.Instances)),
            ResourceBinding.ConstantBuffer(commands.GetBuffer(Canvas), BufferRange.Whole),
            ResourceBinding.SampledTexture(commands.GetTextureSrv(Pages[draw.PageIndex])),
            ResourceBinding.SampledWith(Sampler),
        ];
        commands.SetTransientParameterBindings(new ParameterBlockBindings(Parameters, resources, default));
        commands.Draw(new DrawArguments(6, (uint)draw.Count, 0, 0));
    }
}

internal readonly record struct CanvasDraw(int First, int Count, int PageIndex, GraphBufferSrvId Instances = default);
