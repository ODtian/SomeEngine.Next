using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Elements.Render;

/// <summary>The built-in material for filled, rounded and bordered surfaces.</summary>
public sealed class SurfaceMaterial : Material
{
    public static Guid TypeId { get; } = new("8bac28e5-6a6d-42cc-83d2-94e784d89508");
    public override Guid MaterialTypeId => TypeId;
    public override IReadOnlyList<MaterialPropertyDefinition> Properties => [];

    /// <summary>Uses the shared built-in shader compiled for the requested graphics target.</summary>
    public static SurfaceMaterial Create(SlangCompileTarget target) => new()
    {
        Shader = ElementShaders.Get("Surface", target),
    };

    public override void ApplyParameter(MaterialValue parameter, Texture? texture) =>
        DevelopmentCheck.Fail("Surface drawing values are supplied by the canvas instance buffer.");

    protected override void CopyProperties(Material source) { }
    protected override Material CreateEmpty() => new SurfaceMaterial();
}

/// <summary>Surface's shader-specific pipeline and instance encoding.</summary>
internal sealed class SurfaceDrawing : IDisposable
{
    private const int InstanceByteCount = 80;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Shader _shader;
    private readonly Pipeline _pipeline;
    private readonly VariableLayoutReflection _parameters;
    private readonly Format _format;
    private readonly Dictionary<(Format Format, CompareOperation Comparison), Pipeline> _depthPipelines = [];

    internal Pipeline Pipeline => _pipeline;
    internal VariableLayoutReflection Parameters => _parameters;

    internal SurfaceDrawing(IGraphicsBackend backend, Device device, SurfaceMaterial material, Format format)
    {
        DevelopmentCheck.Require(material.Shader is not null, "A Surface material requires its shader.");
        _backend = backend;
        _device = device;
        _shader = material.Shader!;
        _format = format;
        _parameters = _shader.GetSlangReflection(_shader.RequireSlangTarget(device.Capabilities.CompileTarget))
            .GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _pipeline = CreatePipeline();
    }

    internal Pipeline GetPipeline(in CanvasPassTarget target)
    {
        if (target.Depth == default) return _pipeline;
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
            "Surface", vertexCodeBlob: _shader.GetSlangEntryBlob(target, vertex),
            pixelCodeBlob: _shader.GetSlangEntryBlob(target, pixel)));
    }

    internal InstanceBuffer CreateInstances(ReadOnlySpan<SurfaceInstance> instances)
    {
        ulong byteCount = (ulong)Math.Max(1, instances.Length) * InstanceByteCount;
        Buffer buffer = _backend.CreateBuffer(_device, new BufferDesc(byteCount, BufferUsages.ShaderRead,
            "Surface instances"), MemoryType.Upload);
        try
        {
            using MappedBuffer mapped = _backend.Map(buffer, MapType.Write, BufferRange.Whole);
            mapped.Bytes.Clear();
            Span<Vector4> data = MemoryMarshal.Cast<byte, Vector4>(mapped.Bytes);
            for (int index = 0; index < instances.Length; index++)
            {
                ref readonly SurfaceInstance instance = ref instances[index];
                int start = index * 5;
                data[start] = new(instance.Rectangle.X, instance.Rectangle.Y, instance.Rectangle.Width, instance.Rectangle.Height);
                data[start + 1] = new(instance.Fill.R, instance.Fill.G, instance.Fill.B, instance.Fill.A);
                data[start + 2] = new(instance.BorderColor.R, instance.BorderColor.G, instance.BorderColor.B, instance.BorderColor.A);
                data[start + 3] = new(instance.ClipRectangle.X, instance.ClipRectangle.Y,
                    instance.ClipRectangle.Width, instance.ClipRectangle.Height);
                data[start + 4] = new(instance.CornerRadius, instance.BorderWidth, 0, 0);
            }
            mapped.Flush(new BufferRange(0, byteCount));
            return InstanceBuffer.Create(_backend, _device, buffer, instances.Length, InstanceByteCount);
        }
        catch { buffer.Dispose(); throw; }
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        foreach (Pipeline pipeline in _depthPipelines.Values) pipeline.Dispose();
    }
}
