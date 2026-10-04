using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
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

    /// <summary>Compiles the built-in Slang shader once for the requested graphics target.</summary>
    public static SurfaceMaterial Create(SlangCompileTarget target)
    {
        Require(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global), null);
        var targetDescription = new TargetDesc
        {
            Format = target,
            Profile = global.FindProfile(target == SlangCompileTarget.Spirv ? "glsl_460" : "sm_6_0"),
            Flags = target == SlangCompileTarget.Spirv ? SlangTargetFlags.GenerateSpirvDirectly : 0,
        };
        var description = new SessionDesc
        {
            Targets = [targetDescription],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries =
            [
                new(CompilerOptionName.VulkanUseEntryPointName, CompilerOptionValue.FromInt(1)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0)),
            ],
        };
        Require(global.CreateSession(description, out ISession session), null);
        using Stream source = typeof(SurfaceMaterial).Assembly.GetManifestResourceStream(
            "SomeEngine.Elements.Render.Shaders.Surface.slang")!;
        using var reader = new StreamReader(source);
        IModule? shaderSource = session.LoadModuleFromSource("Surface", "Surface.slang",
            Slang.CreateBlob(reader.ReadToEnd()), out ISlangBlob? diagnostics);
        if (shaderSource is null) throw new InvalidDataException(DiagnosticText(diagnostics));
        Require(shaderSource.FindEntryPointByName("vertexMain", out IEntryPoint vertex), null);
        Require(shaderSource.FindEntryPointByName("pixelMain", out IEntryPoint pixel), null);
        Require(session.CreateCompositeComponentType([shaderSource, vertex, pixel],
            out IComponentType combined, out diagnostics), diagnostics);
        Require(combined.Link(out IComponentType linked, out diagnostics), diagnostics);
        return new SurfaceMaterial
        {
            Shader = Shader.CreateFromSlang("Surface", global, session, [shaderSource], [vertex, pixel],
                linked, [targetDescription]),
        };
    }

    public override void ApplyParameter(MaterialValue parameter, Texture? texture) =>
        DevelopmentCheck.Fail("Surface drawing values are supplied by the canvas instance buffer.");

    protected override void CopyProperties(Material source) { }
    protected override Material CreateEmpty() => new SurfaceMaterial();

    private static void Require(SlangResult result, ISlangBlob? diagnostics)
    {
        if (result.Failed) throw new InvalidDataException($"Surface shader compilation failed: {result}. {DiagnosticText(diagnostics)}");
    }

    private static string DiagnosticText(ISlangBlob? diagnostics) =>
        diagnostics is null ? string.Empty : diagnostics.AsString;
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

    internal SurfaceDrawing(IGraphicsBackend backend, Device device, SurfaceMaterial material, Format format)
    {
        DevelopmentCheck.Require(material.Shader is not null, "A Surface material requires its shader.");
        _backend = backend;
        _device = device;
        _shader = material.Shader!;
        int target = _shader.RequireSlangTarget(device.Capabilities.CompileTarget);
        ShaderReflection reflection = _shader.GetSlangReflection(target);
        EntryPointReflection vertex = _shader.RequireSlangEntry(target, "vertexMain", SlangStage.Vertex);
        EntryPointReflection pixel = _shader.RequireSlangEntry(target, "pixelMain", SlangStage.Fragment);
        _parameters = reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _pipeline = backend.CreateGraphicsPipeline(device, new GraphicsPipelineDesc(
            _shader.SlangCode, reflection, vertex, _shader.GetSlangEntryBytes(target, vertex),
            pixel, _shader.GetSlangEntryBytes(target, pixel), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(),
            new DepthStencilState(), new BlendState([new BlendAttachmentState(
                Enabled: true, SourceColor: BlendFactor.One, DestinationColor: BlendFactor.OneMinusSourceAlpha,
                SourceAlpha: BlendFactor.One, DestinationAlpha: BlendFactor.OneMinusSourceAlpha)]),
            new AttachmentFormatSignature([format], null), DynamicStates.Viewport | DynamicStates.Scissor,
            "Surface", vertexCodeBlob: _shader.GetSlangEntryBlob(target, vertex),
            pixelCodeBlob: _shader.GetSlangEntryBlob(target, pixel)));
    }

    internal CanvasBuffers AddPass(RenderGraphEdit edit, GraphTextureId target)
    {
        GraphBufferInput instances = edit.CreateBufferInput(BufferUsages.ShaderRead);
        GraphBufferId canvas = edit.DeclareExternalBuffer(new BufferDesc(256, BufferUsages.Constant,
            "Canvas size and scale"), MemoryType.Upload);
        GraphBufferCbvId canvasView = edit.CreateBufferCbv(canvas);
        GraphBufferSrvId instancesView = edit.CreateBufferSrv(instances, label: "Surface instances");
        GraphColorAttachmentViewId targetView = edit.CreateColorAttachmentView(target, label: "Canvas color");
        var data = new SurfacePassData(_pipeline, _parameters, instancesView, canvasView, targetView, 0, 0, 0);
        GraphPassId pass = edit.AddRasterFramePass("Canvas surfaces", PassQueueSelection.AnyOfType(QueueType.Graphics),
            data, default, SurfacePassData.Declare, SurfacePassData.Draw);
        return new CanvasBuffers(pass, instances, instancesView, canvas, canvasView, targetView, data);
    }

    internal void SetCanvasData(ref RenderGraphFrame frame, CanvasBuffers buffers)
    {
        Span<Vector4> values = stackalloc Vector4[16];
        values.Clear();
        values[0] = new(buffers.Canvas.Size.Width, buffers.Canvas.Size.Height, buffers.Canvas.Scale, 0);
        frame.SetBufferData(buffers.CanvasBuffer, MemoryMarshal.AsBytes(values));
    }

    internal Buffer CreateInstances(ReadOnlySpan<SurfaceInstance> instances)
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
            return buffer;
        }
        catch { buffer.Dispose(); throw; }
    }

    public void Dispose() => _pipeline.Dispose();
}

internal readonly record struct SurfacePassData(Pipeline Pipeline, VariableLayoutReflection Parameters,
    GraphBufferSrvId Instances, GraphBufferCbvId Canvas, GraphColorAttachmentViewId Target,
    int Width, int Height, int Count)
{
    internal static void Declare(ref PassDefinition pass, ref SurfacePassData data)
    {
        pass.SetPipeline(data.Pipeline);
        pass.SetParameterBlock(data.Parameters);
        pass.Bind(data.Instances, PipelineSync.VertexShading | PipelineSync.PixelShading);
        pass.Bind(data.Canvas, PipelineSync.VertexShading | PipelineSync.PixelShading);
        pass.ColorAttachment(0, data.Target, LoadType.Load, StoreType.Store, WriteCoverage.Partial, default);
    }

    internal static void Draw(ref RasterPassCommandScope commands, in SurfacePassData data)
    {
        commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
        commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
        commands.Draw(new DrawArguments(6, (uint)data.Count, 0, 0));
    }
}
