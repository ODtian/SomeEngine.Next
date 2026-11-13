using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

public sealed class ShaderLibraryFeatureTests
{
    [Fact]
    public void SharedShaderLibraryCreatesPoseAndCanvasPipelines()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(options, adapters, out _);
        using Device device = backend.CreateDevice(new DeviceDesc(adapters.First(adapter => !adapter.HardwareAccelerated).Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));
        using var graph = new RenderGraph.RenderGraph(backend, device,
            [backend.GetQueue(device, QueueType.Graphics)]);
        using var shaders = new ShaderLibrary(device.Capabilities.CompileTarget);
        using var shaderFiles = new SomeEngine.Tests.EngineShaderFiles();
        using var pipelines = new SomeEngine.Render.RenderPipelines(backend, device);
        using var renderDevice = new RenderDevice(graph, pipelines);
        using var skinning = new SkinningFeature(backend, device, pipelines, graph, shaders, shaderFiles.Files);
        using var bounds = new SkinBoundsFeature(backend, device, pipelines, graph, skinning, shaders, shaderFiles.Files);
        using var world = new RenderWorld();
        SurfaceMaterial surfaces = SurfaceMaterial.Create(shaders, shaderFiles.Files);
        GlyphsMaterial glyphs = GlyphsMaterial.Create(shaders, shaderFiles.Files);
        ImageMaterial images = ImageMaterial.Create(shaders, shaderFiles.Files);
        using var renderer = new CanvasRenderer(backend, device, pipelines, graph, world,
            surfaces, Format.R8G8B8A8UNorm, glyphs, images, renderDevice);

        Assert.NotEqual(default, skinning.CompletePass);
        Assert.NotEqual(default, bounds.CompletePass);
        Assert.NotNull(surfaces.Shader);
        Assert.NotNull(glyphs.Shader);
        Assert.NotNull(images.Shader);
    }
}
