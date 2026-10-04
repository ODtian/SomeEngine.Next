using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Elements.Render;

/// <summary>The built-in material for sampled images.</summary>
public sealed class ImageMaterial : Material
{
    public static Guid TypeId { get; } = new("ad789793-f54a-472c-8a16-14d4e147b6cc");
    public override Guid MaterialTypeId => TypeId;
    public override IReadOnlyList<MaterialPropertyDefinition> Properties => [];

    /// <summary>Uses the shared built-in shader compiled for the requested graphics target.</summary>
    public static ImageMaterial Create(SlangCompileTarget target) => new()
    {
        Shader = ElementShaders.Get("Image", target),
    };

    public override void ApplyParameter(MaterialValue parameter, Texture? texture) =>
        DevelopmentCheck.Fail("Image drawing values are supplied by the canvas instance buffer.");

    protected override void CopyProperties(Material source) { }
    protected override Material CreateEmpty() => new ImageMaterial();
}

/// <summary>Image's shader-specific pipeline, sampler and instance bytes.</summary>
internal sealed class ImageDrawing : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Shader _shader;
    private readonly Format _format;
    private readonly Dictionary<(Format Format, CompareOperation Comparison), Pipeline> _depthPipelines = [];
    internal Pipeline Pipeline { get; }
    internal VariableLayoutReflection Parameters { get; }
    internal Sampler Sampler { get; }

    internal ImageDrawing(IGraphicsBackend backend, Device device, ImageMaterial material, Format format)
    {
        DevelopmentCheck.Require(material.Shader is not null, "An Image material requires its shader.");
        _backend = backend;
        _device = device;
        Shader shader = _shader = material.Shader!;
        _format = format;
        int target = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        EntryPointReflection vertex = shader.RequireSlangEntry(target, "vertexMain", SlangStage.Vertex);
        EntryPointReflection pixel = shader.RequireSlangEntry(target, "pixelMain", SlangStage.Fragment);
        Parameters = reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Pipeline = CreatePipeline();
        try
        {
            Sampler = backend.CreateSampler(device, new SamplerDesc(FilterType.Linear, FilterType.Linear,
                FilterType.Linear, AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge,
                Label: "Image sampler"));
        }
        catch { Pipeline.Dispose(); throw; }
    }

    internal Pipeline GetPipeline(in CanvasPassTarget target)
    {
        if (target.Depth == default) return Pipeline;
        var key = (target.DepthFormat, target.DepthComparison);
        if (!_depthPipelines.TryGetValue(key, out Pipeline? pipeline))
            _depthPipelines.Add(key, pipeline = CreatePipeline(target.DepthFormat, target.DepthComparison));
        return pipeline;
    }

    private Pipeline CreatePipeline(Format? depthFormat = null, CompareOperation comparison = CompareOperation.LessOrEqual)
    {
        int target = _shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        ShaderReflection reflection = _shader.GetSlangReflection(target);
        EntryPointReflection vertex = _shader.RequireSlangEntry(target, "vertexMain", SlangStage.Vertex);
        EntryPointReflection pixel = _shader.RequireSlangEntry(target, "pixelMain", SlangStage.Fragment);
        return _backend.CreateGraphicsPipeline(_device, new GraphicsPipelineDesc(
            _shader.SlangCode, reflection, vertex, _shader.GetSlangEntryBytes(target, vertex),
            pixel, _shader.GetSlangEntryBytes(target, pixel), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(),
            new DepthStencilState(DepthTest: depthFormat.HasValue, DepthWrite: false, DepthComparison: comparison),
            new BlendState([new BlendAttachmentState(
                Enabled: true, SourceColor: BlendFactor.One, DestinationColor: BlendFactor.OneMinusSourceAlpha,
                SourceAlpha: BlendFactor.One, DestinationAlpha: BlendFactor.OneMinusSourceAlpha)]),
            new AttachmentFormatSignature([_format], depthFormat), DynamicStates.Viewport | DynamicStates.Scissor,
            "Image", vertexCodeBlob: _shader.GetSlangEntryBlob(target, vertex),
            pixelCodeBlob: _shader.GetSlangEntryBlob(target, pixel)));
    }

    internal InstanceBuffer CreateInstances(ReadOnlySpan<ImageInstance> instances)
    {
        ulong byteCount = (ulong)Math.Max(1, instances.Length) * 48;
        Buffer buffer = _backend.CreateBuffer(_device, new BufferDesc(byteCount, BufferUsages.ShaderRead,
            "Image instances"), MemoryType.Upload);
        try
        {
            using MappedBuffer mapped = _backend.Map(buffer, MapType.Write, BufferRange.Whole);
            mapped.Bytes.Clear();
            Span<Vector4> data = MemoryMarshal.Cast<byte, Vector4>(mapped.Bytes);
            for (int index = 0; index < instances.Length; index++)
            {
                ref readonly ImageInstance image = ref instances[index];
                data[index * 3] = new(image.Rectangle.X, image.Rectangle.Y, image.Rectangle.Width, image.Rectangle.Height);
                data[index * 3 + 1] = new(image.Uv.X, image.Uv.Y, image.Uv.Width, image.Uv.Height);
                data[index * 3 + 2] = new(image.ClipRectangle.X, image.ClipRectangle.Y,
                    image.ClipRectangle.Width, image.ClipRectangle.Height);
            }
            mapped.Flush(new BufferRange(0, byteCount));
            return InstanceBuffer.Create(_backend, _device, buffer, instances.Length, 48);
        }
        catch { buffer.Dispose(); throw; }
    }

    public void Dispose()
    {
        Sampler.Dispose();
        Pipeline.Dispose();
        foreach (Pipeline pipeline in _depthPipelines.Values) pipeline.Dispose();
    }
}

