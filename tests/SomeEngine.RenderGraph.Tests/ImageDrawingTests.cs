using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;
using SomeEngine.Views;
using Texture = SomeEngine.Resources.Texture;
using Surface = SomeEngine.Elements.Surface;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ImageDrawingTests
{
    [Theory]
    [InlineData(false, ImageFit.Stretch)]
    [InlineData(false, ImageFit.Contain)]
    [InlineData(false, ImageFit.Cover)]
    [InlineData(true, ImageFit.Stretch)]
    [InlineData(true, ImageFit.Contain)]
    [InlineData(true, ImageFit.Cover)]
    public void ImagePixelsFollowFittingAndResourceReadiness(bool vulkan, ImageFit fit)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckImage(backend, device, fit);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckImage(graphics.Backend, graphics.Device, fit);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedMaterialsRespectTreeOrderAndPremultipliedAlpha(bool vulkan)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckImage(backend, device, ImageFit.Stretch, mixed: true);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckImage(graphics.Backend, graphics.Device, ImageFit.Stretch, mixed: true);
        }
    }

    private static void CheckImage(IGraphicsBackend backend, Device device, ImageFit fit, bool mixed = false)
    {
        using var renderDevice = new RenderDevice(backend, device);
        using var frames = new RenderFrameCoordinator(renderDevice);
        using var graph = new RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)]);
        using var game = new World();
        using var render = new RenderWorld();
        using Texture texture = CreateTexture(mixed ? [255, 0, 0, 128] : null);
        using Texture secondTexture = CreateTexture([0, 255, 0, 128]);
        Texture? ready = null;
        var source = new AssetId<TextureAsset>(Guid.NewGuid());
        var secondSource = new AssetId<TextureAsset>(Guid.NewGuid());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem(new ImageTextures(id => id == source ? ready : secondTexture)));
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(64, 64) });
        Entity root = Views.Views.Spawn(game, canvas, new Absolute());
        Entity image = Views.Views.Spawn(game, root, new Image(source, fit));
        game.Add(image, new Box { Fixed = new Size(32, 32) });
        game.Add(image, new AbsolutePosition { Value = new Vector2(8, 8) });
        if (mixed)
        {
            game.Add(image, new Surface { Fill = new Color(0, 0, 1) });
            Entity second = Views.Views.Spawn(game, root, new Image(secondSource, ImageFit.Stretch));
            game.Add(second, new Box { Fixed = new Size(32, 32) });
            game.Add(second, new AbsolutePosition { Value = new Vector2(24, 8) });
            Entity overlay = Views.Views.Spawn(game, root, new Absolute());
            game.Add(overlay, new Box { Fixed = new Size(16, 16) });
            game.Add(overlay, new AbsolutePosition { Value = new Vector2(32, 16) });
            game.Add(overlay, new Surface { Fill = new Color(1, 1, 1, 0.5f) });
        }
        using var extraction = new RenderExtractionSystems(render, new CanvasRenderExtractor());
        using SurfaceMaterial surfaces = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using ImageMaterial images = ImageMaterial.Create(device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(backend, device, graph, render, surfaces,
            Format.R8G8B8A8UNorm, images, renderDevice);
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphTextureId target = AddReadback(graph, readback, out GraphPassId copy);
        ulong structure = 0;
        for (int index = 0; index < 3; index++)
        {
            if (index == 1) ready = texture;
            systems.Update();
            Assert.Equal(new Rect(8, 8, 32, 32), game.Read<LayoutRect>(image).Value);
            extraction.Extract(game);
            using var prepare = new RenderPrepareSystems(render, extraction);
            Assert.True(frames.TryBeginPrepare(out var scope));
            using (scope)
            {
                try { Assert.True(prepare.ApplyExtraction(scope)); }
                finally { prepare.Shutdown(scope); }
                scope.Commit();
            }
            renderer.ReconcileGraph(target);
            Assert.True(renderer.TryGetPass(extraction.RequireMirror(canvas), out GraphPassId drawing));
            if (index == 0)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.OrderAfter(copy, drawing);
                edit.Commit();
                structure = graph.StructureVersion;
            }
            if (index == 1)
            {
                RenderGraphFrame cancelled = graph.BeginFrame();
                try { renderer.UpdateFrame(ref cancelled); }
                finally { cancelled.Dispose(); }
            }
            if (index == 2)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
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
            if (index == 0)
                AssertPixel(pixels.Bytes, 16, 16, [255, 0, 255, 255]);
            else if (mixed)
            {
                AssertPixel(pixels.Bytes, 16, 16, [128, 0, 127, 255], tolerance: 1);
                AssertPixel(pixels.Bytes, 28, 12, [64, 128, 63, 255], tolerance: 1);
                AssertPixel(pixels.Bytes, 36, 24, [160, 192, 159, 255], tolerance: 1);
                AssertPixel(pixels.Bytes, 48, 12, [0, 128, 0, 255], tolerance: 1);
            }
            else
            {
                int upper = fit == ImageFit.Contain ? 19 : 14;
                int lower = fit == ImageFit.Contain ? 28 : 34;
                AssertPixel(pixels.Bytes, 14, upper, [255, 0, 0, 255]);
                AssertPixel(pixels.Bytes, 34, upper, [0, 255, 0, 255]);
                AssertPixel(pixels.Bytes, 14, lower, [0, 0, 255, 255]);
                AssertPixel(pixels.Bytes, 34, lower, [255, 255, 0, 255]);
                AssertPixel(pixels.Bytes, 14, 10,
                    fit == ImageFit.Contain ? [0, 0, 0, 255] : [255, 0, 0, 255]);
            }
            AssertPixel(pixels.Bytes, 7, 24, [0, 0, 0, 255]);
            AssertPixel(pixels.Bytes, mixed ? 56 : 40, 24, [0, 0, 0, 255]);
            Assert.Equal(structure, graph.StructureVersion);
        }
    }

    private static Texture CreateTexture(byte[]? color = null)
    {
        var pixels = new byte[8 * 4 * 4];
        byte[][] colors = [[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 0, 255]];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                (color ?? colors[(y < 2 ? 0 : 2) + (x < 4 ? 0 : 1)]).CopyTo(pixels, (y * 8 + x) * 4);
        var tile = new TextureMipTile
        {
            Width = 8, Height = 4, RowPitch = 32, SlicePitch = 128, ChunkKey = 0, DecodedLength = 128,
        };
        return Texture.Create(new TextureDesc(TextureDimension.Texture2D, 8, 4, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination),
            [new TexturePixels(tile, pixels)], TextureViewDimension.Texture2D);
    }

    private static GraphTextureId AddReadback(RenderGraph graph, SomeEngine.Graphics.Buffer readback, out GraphPassId copy)
    {
        using RenderGraphEdit edit = graph.BeginEdit();
        GraphTextureId target = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
            64, 64, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        GraphColorAttachmentViewId color = edit.CreateColorAttachmentView(target);
        edit.AddRasterFramePass("Clear image target", PassQueueSelection.AnyOfType(QueueType.Graphics), color,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref GraphColorAttachmentViewId view) =>
                pass.ColorAttachment(0, view, LoadType.Clear, StoreType.Store, WriteCoverage.Complete,
                    new Vector4(0, 0, 0, 1)),
            static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _) => { });
        GraphBufferId output = edit.RegisterExternalBuffer(readback,
            [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
        copy = edit.AddCopyFramePass("Read image pixels", PassQueueSelection.AnyOfType(QueueType.Graphics),
            (target, output), new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
            {
                pass.Read(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                    ResourceAccess.CopySource, TextureLayout.CopySource);
                pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0,
                    256, 64, commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, 64, 64, 1)));
        edit.Commit();
        return target;
    }

    private static void AssertPixel(ReadOnlySpan<byte> pixels, int x, int y, byte[] expected, int tolerance = 0)
    {
        ReadOnlySpan<byte> actual = pixels.Slice(y * 256 + x * 4, 4);
        for (int channel = 0; channel < 4; channel++)
            Assert.True(actual[channel] >= Math.Max(0, expected[channel] - tolerance) &&
                actual[channel] <= Math.Min(255, expected[channel] + tolerance),
                $"Pixel ({x}, {y}) channel {channel}: expected {expected[channel]} ± {tolerance}, actual [{string.Join(", ", actual.ToArray())}].");
    }
}
