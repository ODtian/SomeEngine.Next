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
    public void SurfacesGlyphsAndImagesComposeThroughResourceUpdatesAndCanceledFrames(bool vulkan, bool worldSpace)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckComposition(backend, device, worldSpace);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckComposition(graphics.Backend, graphics.Device, worldSpace);
        }
    }

    private static void CheckComposition(IGraphicsBackend backend, Device device, bool worldSpace)
    {
        using var renderDevice = new RenderDevice(backend, device);
        using var graph = new RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)]);
        using var world = new RenderWorld();
        using SurfaceMaterial surfaces = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using GlyphsMaterial glyphs = GlyphsMaterial.Create(device.Capabilities.CompileTarget);
        using ImageMaterial images = ImageMaterial.Create(device.Capabilities.CompileTarget);
        using Texture red = SolidTexture([255, 0, 0, 128]);
        using Texture magenta = SolidTexture([255, 0, 255, 128]);
        using Texture yellow = SolidTexture([255, 255, 0, 128]);
        using var renderer = new CanvasRenderer(backend, device, graph, world, surfaces,
            Format.R8G8B8A8UNorm, glyphs, images, renderDevice);
        Entity atlas = world.CreateEntity(new RenderGlyphAtlas(
            new[] { new GlyphPagePixels(4, 4, Enumerable.Repeat((byte)255, 16).ToArray()) },
            new[] { new AtlasDirtyRegion(0, 0, 0, 4, 4) }, 8, 1));
        Entity view = world.CreateEntity(new RenderView(Matrix4x4.Identity, Matrix4x4.Identity, 64, 64));
        var canvasValue = new RenderCanvas(worldSpace ? CanvasTarget.WorldSpace(Entity.Null) : CanvasTarget.Screen,
            new Size(64, 64), 1)
        {
            Transform = Matrix4x4.CreateScale(1f / 32, 1f / 32, 1) * Matrix4x4.CreateTranslation(-1, 1, 0.25f),
            DepthTest = worldSpace,
        };
        Entity canvas = world.CreateEntity(canvasValue);
        world.Add(canvas, new CanvasGlyphAtlas(atlas));
        world.AddBuffer<SurfaceInstance>(canvas);
        world.AddBuffer<GlyphInstance>(canvas);
        world.AddBuffer<ImageInstance>(canvas);
        var clip = new Rect(0, 0, 64, 64);
        world.ExecuteBufferWrite<SurfaceInstance>(canvas, values =>
        {
            values.Add(new(Entity.Null, clip, new Color(0, 0, 1), 0, default, 0, clip, 0, 1));
            values.Add(new(Entity.Null, new Rect(24, 24, 8, 8), new Color(0, 1, 0), 0, default, 0, clip, 3, 1));
        });
        world.ExecuteBufferWrite<GlyphInstance>(canvas, values =>
        {
            values.Add(new(Entity.Null, 0, new Rect(8, 8, 32, 32), Vector2.Zero, Vector2.One,
                new Color(1, 1, 1, 0.5f), new Rect(0, 0, 40, 64), 0, 1, 1));
            values.Add(new(Entity.Null, 1, new Rect(16, 16, 8, 8), Vector2.Zero, Vector2.One,
                new Color(0, 1, 1, 0.5f), clip, 0, 4, 1));
        });
        world.ExecuteBufferWrite<ImageInstance>(canvas, values =>
        {
            values.Add(new(Entity.Null, red, new Rect(16, 8, 32, 40), new Rect(0, 0, 1, 1), clip, 2, 1));
            values.Add(new(Entity.Null, yellow, new Rect(32, 32, 8, 8), new Rect(0, 0, 1, 1), clip, 5, 1));
        });
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphTextureId target = CreateTarget(graph, readback, out GraphTextureId depth, out GraphPassId copy);
        var worldTarget = new WorldCanvasTarget(target, depth, view);
        GraphPassId drawing = default;
        ulong structure = 0;
        for (int index = 0; index < (worldSpace ? 5 : 4); index++)
        {
            world.AcquireSystemTick();
            if (index == 2)
            {
                world.Replace(atlas, new RenderGlyphAtlas(new[] { new GlyphPagePixels(4, 4, new byte[16]) },
                    new[] { new AtlasDirtyRegion(0, 0, 0, 4, 4) }, 8, 2));
                world.ExecuteBufferWrite<ImageInstance>(canvas, values =>
                    values[0] = values[0] with { Texture = magenta, Version = 2 });
            }
            if (index == 3) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            if (index == 4) world.Replace(canvas, canvasValue with
            {
                Transform = Matrix4x4.CreateScale(1f / 32, 1f / 32, 1) * Matrix4x4.CreateTranslation(-1, 1, 0.75f),
            });
            renderer.ReconcileGraph(target, worldTarget);
            Assert.True(renderer.TryGetPass(canvas, out GraphPassId current));
            if (index == 0)
            {
                drawing = current;
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.OrderAfter(copy, drawing);
                edit.Commit();
                structure = graph.StructureVersion;
            }
            Assert.Equal(drawing, current);
            if (index is 0 or 2)
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
            if (index == 4)
            {
                Pixel(pixels.Bytes, 20, 20, [0, 0, 0, 255]);
                Pixel(pixels.Bytes, 28, 28, [0, 0, 0, 255]);
                Pixel(pixels.Bytes, 36, 36, [0, 0, 0, 255]);
            }
            else
            {
                Pixel(pixels.Bytes, 20, 20, index < 2 ? [96, 160, 191, 255] : [128, 0, 255, 255]);
                Pixel(pixels.Bytes, 36, 36, index < 2 ? [224, 160, 63, 255] : [192, 128, 127, 255]);
                Pixel(pixels.Bytes, 12, 12, index < 2 ? [128, 128, 255, 255] : [0, 0, 255, 255]);
                Pixel(pixels.Bytes, 46, 24, index < 2 ? [128, 0, 127, 255] : [128, 0, 255, 255]);
                Pixel(pixels.Bytes, 28, 28, [0, 255, 0, 255]);
            }
            Assert.Equal(structure, graph.StructureVersion);
        }
    }

    private static Texture SolidTexture(byte[] pixels) => Texture.Create(
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4,
            DecodedLength = 4, ChunkKey = 0 }, pixels)], TextureViewDimension.Texture2D);

    private static GraphTextureId CreateTarget(RenderGraph graph, SomeEngine.Graphics.Buffer readback,
        out GraphTextureId depth, out GraphPassId copy)
    {
        using RenderGraphEdit edit = graph.BeginEdit();
        GraphTextureId target = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
            64, 64, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        depth = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D, 64, 64, 1, 1, 1, 1,
            Format.D32Float, TextureUsages.DepthStencilAttachment));
        var attachments = (Color: edit.CreateColorAttachmentView(target), Depth: edit.CreateDepthStencilView(depth,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2D));
        edit.AddRasterFramePass("Clear composed canvas", PassQueueSelection.AnyOfType(QueueType.Graphics), attachments,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref (GraphColorAttachmentViewId Color, GraphDepthStencilViewId Depth) data) =>
            {
                pass.ColorAttachment(0, data.Color, LoadType.Clear, StoreType.Store, WriteCoverage.Complete, new Vector4(0, 0, 0, 1));
                pass.DepthStencilAttachment(data.Depth, LoadType.Clear, StoreType.Store, WriteCoverage.Complete, 0.5f,
                    LoadType.Discard, StoreType.Discard, WriteCoverage.Partial, 0);
            }, static (ref RasterPassCommandScope _, in (GraphColorAttachmentViewId, GraphDepthStencilViewId) _) => { });
        GraphBufferId output = edit.RegisterExternalBuffer(readback,
            [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
        copy = edit.AddCopyFramePass("Read composed canvas", PassQueueSelection.AnyOfType(QueueType.Graphics),
            (target, output), new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
            {
                pass.Read(data.Texture, new(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                    ResourceAccess.CopySource, TextureLayout.CopySource);
                pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            }, static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                commands.CopyTextureToBuffer(new(commands.GetBuffer(data.Buffer), 0, 256, 64,
                    commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, 64, 64, 1)));
        edit.Commit();
        return target;
    }

    private static void Pixel(ReadOnlySpan<byte> pixels, int x, int y, byte[] expected)
    {
        ReadOnlySpan<byte> actual = pixels.Slice(y * 256 + x * 4, 4);
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(actual[channel] - expected[channel]) <= 1,
                $"Pixel ({x}, {y}): expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual.ToArray())}].");
    }
}
