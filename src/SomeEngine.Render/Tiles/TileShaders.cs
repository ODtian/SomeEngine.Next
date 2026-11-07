using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tiles;

public readonly record struct TileComputeShader(Shader Shader, LinkedComputePipeline Pipeline);

public readonly record struct TilePassInputs(
    IndirectCommandLayout DispatchLayout, IndirectCommandLayout DrawLayout,
    GraphTextureId Depth, int Width, int Height);

/// <summary>Linked shader entries shared by successive Tile graph installations.</summary>
public sealed class TileShaders(IGraphicsBackend backend, Device device, Shader hardwareShader) : IDisposable
{
    private readonly Dictionary<string, TileComputeShader> _compute = new(StringComparer.Ordinal);
    private Pipeline? _hardware;

    public Shader HardwareShader => hardwareShader;
    public TileComputeShader this[string entry] => _compute[entry];
    public bool ContainsKey(string entry) => _compute.ContainsKey(entry);

    public void Add(string entry, Shader shader) => _compute.Add(entry,
        new TileComputeShader(shader, LinkedComputePipeline.Create(backend, device, shader, entry, entry)));

    public Pipeline GetHardwarePipeline(bool pixelLists)
    {
        if (_hardware is not null) return _hardware;
        int target = hardwareShader.RequireSlangTarget(device.Capabilities.CompileTarget);
        ShaderReflection reflection = hardwareShader.GetSlangReflection(target);
        EntryPointReflection vertex = hardwareShader.RequireSlangEntry(target,
            pixelLists ? "DrawTransparencyReference" : "DrawTransparentCoverage", SlangStage.Vertex);
        EntryPointReflection pixel = hardwareShader.RequireSlangEntry(target,
            pixelLists ? "StoreTransparencyReference" : "StoreTransparentCoverage", SlangStage.Fragment);
        _hardware = backend.CreateGraphicsPipeline(device, new GraphicsPipelineDesc(
            hardwareShader.SlangCode, reflection, vertex, hardwareShader.GetSlangEntryBytes(target, vertex),
            pixel, hardwareShader.GetSlangEntryBytes(target, pixel), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(SampleCount: 1),
            new DepthStencilState(DepthTest: true, DepthComparison: CompareOperation.LessOrEqual),
            new BlendState([]), new AttachmentFormatSignature([], Format.D32Float),
            DynamicStates.Viewport | DynamicStates.Scissor, "Tile hardware coverage",
            vertexCodeBlob: hardwareShader.GetSlangEntryBlob(target, vertex),
            pixelCodeBlob: hardwareShader.GetSlangEntryBlob(target, pixel)));
        return _hardware;
    }

    public void Dispose()
    {
        _hardware?.Dispose();
        foreach (TileComputeShader shader in _compute.Values) shader.Pipeline.Dispose();
        _compute.Clear();
    }
}
