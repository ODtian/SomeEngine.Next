using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;
using SomeEngine.Typography;
using Texture = SomeEngine.Resources.Texture;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.RenderGraph.Tests;

public sealed partial class CanvasCompositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PresentationTransformsAndOpacityReachAllCanvasMaterials(bool vulkan, bool worldSpace)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckPresentation(backend, device, worldSpace);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckPresentation(graphics.Backend, graphics.Device, worldSpace);
        }
    }

    private static void CheckPresentation(IGraphicsBackend backend, Device device, bool worldSpace)
    {
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = new RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)]);
        using var world = new RenderWorld();
        using SurfaceMaterial surfaces = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using GlyphsMaterial glyphs = GlyphsMaterial.Create(device.Capabilities.CompileTarget);
        using ImageMaterial images = ImageMaterial.Create(device.Capabilities.CompileTarget);
        using Texture blue = SolidTexture([0, 0, 255, 255]);
        using var renderer = new CanvasRenderer(backend, device, graph, world, surfaces,
            Format.R8G8B8A8UNorm, glyphs, images, renderDevice);
        Entity atlas = world.CreateEntity(new RenderGlyphAtlas(
            new[] { new GlyphPagePixels(4, 4, Enumerable.Repeat((byte)255, 16).ToArray()) },
            new[] { new AtlasDirtyRegion(0, 0, 0, 4, 4) }, 8, 1));
        Entity view = world.CreateEntity(new RenderView(Matrix4x4.Identity, Matrix4x4.Identity, 64, 64));
        Entity canvas = world.CreateEntity(new RenderCanvas(
            worldSpace ? CanvasTarget.WorldSpace(Entity.Null) : CanvasTarget.Screen, new Size(64, 64), 1)
        {
            Transform = Matrix4x4.CreateScale(1f / 32, 1f / 32, 1) * Matrix4x4.CreateTranslation(-1, 1, 0.25f),
            DepthTest = worldSpace,
        });
        world.Add(canvas, new CanvasGlyphAtlas(atlas));
        world.AddBuffer<SurfaceInstance>(canvas);
        world.AddBuffer<GlyphInstance>(canvas);
        world.AddBuffer<ImageInstance>(canvas);
        var clip = new Rect(0, 0, 64, 64);
        var matrix = new Matrix3x2(0, 1, -1, 0, 48, 0);
        world.ExecuteBufferWrite<SurfaceInstance>(canvas, values =>
            values.Add(new(Entity.Null, new Rect(4, 4, 8, 8), new Color(1, 0, 0), 0, default, 0, clip, 0, 1)
            { Matrix = matrix, Alpha = 0.5f }));
        world.ExecuteBufferWrite<GlyphInstance>(canvas, values =>
            values.Add(new(Entity.Null, 0, new Rect(4, 20, 8, 8), Vector2.Zero, Vector2.One,
                new Color(0, 1, 0), new Rect(0, 0, 26, 64), 0, 1, 1)
            { Matrix = matrix, Alpha = 0.5f }));
        world.ExecuteBufferWrite<ImageInstance>(canvas, values =>
            values.Add(new(Entity.Null, blue, new Rect(4, 36, 8, 8), new Rect(0, 0, 1, 1), clip, 2, 1)
            { Matrix = matrix, Alpha = 0.5f }));
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphTextureId target = CreateTarget(graph, readback, out GraphTextureId depth, out GraphPassId copy);
        var worldTarget = new WorldCanvasTarget(target, depth, view);
        renderer.ReconcileGraph(target, worldTarget);
        Assert.True(renderer.TryGetPass(canvas, out GraphPassId drawing));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(copy, drawing);
            edit.Commit();
        }
        ulong structure = graph.StructureVersion;
        for (int index = 0; index < 3; index++)
        {
            world.AcquireSystemTick();
            if (index == 2)
            {
                world.ExecuteBufferWrite<SurfaceInstance>(canvas, values =>
                    values[0] = values[0] with { Matrix = Matrix3x2.Identity, Alpha = 1, Version = 2 });
                world.ExecuteBufferWrite<GlyphInstance>(canvas, values =>
                    values[0] = values[0] with { Matrix = Matrix3x2.Identity, Alpha = 1, Version = 2 });
                world.ExecuteBufferWrite<ImageInstance>(canvas, values =>
                    values[0] = values[0] with { Matrix = Matrix3x2.Identity, Alpha = 1, Version = 2 });
            }
            renderer.ReconcileGraph(target, worldTarget);
            if (index == 2)
            {
                RenderGraphFrame canceled = graph.BeginFrame();
                try { renderer.UpdateFrame(ref canceled); }
                finally { canceled.Dispose(); }
            }
            RenderGraphFrame frame = graph.BeginFrame();
            try
            {
                renderer.UpdateFrame(ref frame);
                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int count = frame.Execute(completions);
                renderDevice.TrackSubmission(completions.AsSpan(0, count));
                renderDevice.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
            }
            finally { frame.Dispose(); }
            using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
            pixels.Invalidate(pixels.Range);
            Pixel(pixels.Bytes, 40, 8, index < 2 ? [128, 0, 0, 255] : [0, 0, 0, 255]);
            Pixel(pixels.Bytes, 24, 8, index < 2 ? [0, 128, 0, 255] : [0, 0, 0, 255]);
            Pixel(pixels.Bytes, 8, 8, index < 2 ? [0, 0, 128, 255] : [255, 0, 0, 255]);
            Pixel(pixels.Bytes, 8, 24, index < 2 ? [0, 0, 0, 255] : [0, 255, 0, 255]);
            Pixel(pixels.Bytes, 8, 40, index < 2 ? [0, 0, 0, 255] : [0, 0, 255, 255]);
            Pixel(pixels.Bytes, 27, 8, [0, 0, 0, 255]);
            Assert.Equal(structure, graph.StructureVersion);
            Assert.True(renderer.TryGetPass(canvas, out GraphPassId current));
            Assert.Equal(drawing, current);
        }
    }
}
