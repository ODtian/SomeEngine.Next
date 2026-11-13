using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Render;
using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Elements.Render;

/// <summary>The built-in material for filled, rounded, bordered, and shadowed surfaces.</summary>
public sealed class SurfaceMaterial : Material
{
    public static Guid TypeId { get; } = new("8bac28e5-6a6d-42cc-83d2-94e784d89508");
    public override IReadOnlyList<MaterialPropertyDefinition> Properties => [];

    /// <summary>Uses the shared built-in shader held by the supplied shader library.</summary>
    public static SurfaceMaterial Create(ShaderLibrary shaders, AssetLoader files) => new()
    {
        Shader = shaders.GetAsync(files, EngineShaderAssets.CanvasSurface).AsTask().GetAwaiter().GetResult(),
    };

}

/// <summary>Surface's shader-specific pipeline and instance encoding.</summary>
internal sealed class SurfaceDrawing : IDisposable
{

    private readonly RenderPipelines _renderPipelines;
    private const int InstanceByteCount = 144;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private Shader _shader;
    private readonly Material _material;
    private Pipeline _pipeline;
    private VariableLayoutReflection _parameters;
    private readonly Format _format;
    private readonly Dictionary<(Format Format, CompareOperation Comparison), Pipeline> _depthPipelines = [];

    internal Pipeline Pipeline => _pipeline;
    internal VariableLayoutReflection Parameters => _parameters;

    internal SurfaceDrawing(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        SurfaceMaterial material,
        Format format)    {

        _renderPipelines = pipelines;
        _material = material;
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

    internal bool ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_shader, previous)) return false;
        _shader = next;
        _material.Shader = next;
        _pipeline = CreatePipeline();
        _parameters = next.GetSlangReflection(next.RequireSlangTarget(_device.Capabilities.CompileTarget))
            .GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _depthPipelines.Clear();
        return true;
    }

    private Pipeline CreatePipeline(Format? depthFormat = null, CompareOperation comparison = CompareOperation.LessOrEqual)
    {
        int target = _shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        ShaderReflection reflection = _shader.GetSlangReflection(target);
        EntryPointReflection vertex = _shader.RequireSlangEntry(target, "vertexMain", SlangStage.Vertex);
        EntryPointReflection pixel = _shader.RequireSlangEntry(target, "pixelMain", SlangStage.Fragment);
        return _renderPipelines.CreateGraphics(new GraphicsPipelineDesc(
            _shader.SlangCode, reflection, vertex, _shader.GetSlangEntryBytes(target, vertex),
            pixel, _shader.GetSlangEntryBytes(target, pixel), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(),
            new DepthStencilState(DepthTest: depthFormat.HasValue, DepthWrite: false, DepthComparison: comparison),
            new BlendState([new BlendAttachmentState(
                Enabled: true, SourceColor: BlendFactor.One, DestinationColor: BlendFactor.OneMinusSourceAlpha,
                SourceAlpha: BlendFactor.One, DestinationAlpha: BlendFactor.OneMinusSourceAlpha)]),
            new AttachmentFormatSignature([_format], depthFormat), DynamicStates.Viewport | DynamicStates.Scissor,
            "Surface", vertexCodeBlob: _shader.GetSlangEntryBlob(target, vertex),
            pixelCodeBlob: _shader.GetSlangEntryBlob(target, pixel))).GetAwaiter().GetResult();
    }

    internal InstanceBuffer CreateInstances(global::SomeEngine.RenderGraph.RenderGraph graph, ReadOnlySpan<SurfaceInstance> instances, InstanceBuffer? previous = null)
    {
        int byteCount = Math.Max(1, instances.Length) * InstanceByteCount;
        var bytes = new byte[byteCount];
        Span<Vector4> data = MemoryMarshal.Cast<byte, Vector4>(bytes.AsSpan());
        for (int index = 0; index < instances.Length; index++)
        {
            ref readonly SurfaceInstance instance = ref instances[index];
            int start = index * 9;
            data[start] = new(instance.Rectangle.X, instance.Rectangle.Y, instance.Rectangle.Width, instance.Rectangle.Height);
            data[start + 1] = new(instance.Fill.R, instance.Fill.G, instance.Fill.B, instance.Fill.A);
            data[start + 2] = new(instance.BorderColor.R, instance.BorderColor.G, instance.BorderColor.B, instance.BorderColor.A);
            data[start + 3] = new(instance.ClipRectangle.X, instance.ClipRectangle.Y,
                instance.ClipRectangle.Width, instance.ClipRectangle.Height);
            data[start + 4] = new(instance.CornerRadius, instance.BorderWidth, 0, 0);
            data[start + 5] = new(instance.Shadow.Color.R, instance.Shadow.Color.G,
                instance.Shadow.Color.B, instance.Shadow.Color.A);
            data[start + 6] = new(instance.Shadow.Offset, instance.Shadow.BlurRadius, 0);
            data[start + 7] = new(instance.Matrix.M11, instance.Matrix.M12, instance.Matrix.M21, instance.Matrix.M22);
            data[start + 8] = new(instance.Matrix.M31, instance.Matrix.M32, instance.Alpha, 0);
        }
        return InstanceBuffer.Create(_backend, _device, graph, bytes, instances.Length, InstanceByteCount, "Surface instances", previous);
    }

    public void Dispose()
    {
        
        _depthPipelines.Clear();
    }
}
