using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Typography;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.RenderGraph.Tests;

public sealed class GlyphDrawingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlyphCoverageMatchesTheOutlineReferenceAndColorUpdatesKeepItsMask(bool vulkan)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckGlyph(backend, device);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckGlyph(graphics.Backend, graphics.Device);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewAtlasRegionsAndPagesDrawInCanvasTreeOrderWithClipping(bool vulkan)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckGlyph(backend, device, regionsAndTree: true);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckGlyph(graphics.Backend, graphics.Device, regionsAndTree: true);
        }
    }

    private static void CheckGlyph(IGraphicsBackend backend, Device device, bool regionsAndTree = false)
    {
        using Font font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
        var atlas = new GlyphAtlas(regionsAndTree ? 96 : 128, regionsAndTree ? 64 : 128);
        GlyphLocation glyph = atlas.Get(font, 34);
        using var graph = new RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)]);
        using var world = new RenderWorld();
        using SurfaceMaterial surface = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using GlyphsMaterial glyphs = GlyphsMaterial.Create(device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(backend, device, graph, world, surface, Format.R8G8B8A8UNorm, glyphs);
        Entity atlasEntity = world.CreateEntity(new RenderGlyphAtlas(
            new[] { new GlyphPagePixels(atlas.Pages[0].Width, atlas.Pages[0].Height, atlas.Pages[0].Pixels.ToArray()) },
            new[] { glyph.Region }, atlas.DistanceSpread, 1));
        Entity canvas = world.CreateEntity(new RenderCanvas(CanvasTarget.Screen, new Size(64, 64), 1));
        world.AddBuffer<SurfaceInstance>(canvas);
        world.AddBuffer<GlyphInstance>(canvas);
        world.Add(canvas, new CanvasGlyphAtlas(atlasEntity));
        world.ExecuteBufferWrite<GlyphInstance>(canvas, buffer => buffer.Add(new(Entity.Null, 0,
            new Rect(8, 5, 47, 51), glyph.UV0, glyph.UV1, new Color(1, 1, 1),
            new Rect(0, 0, 64, 64), 0, 0, 1)));
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphTextureId target;
        GraphPassId copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                64, 64, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
            GraphColorAttachmentViewId color = edit.CreateColorAttachmentView(target);
            edit.AddRasterFramePass("Clear glyph target", PassQueueSelection.AnyOfType(QueueType.Graphics), color,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphColorAttachmentViewId view) =>
                    pass.ColorAttachment(0, view, LoadType.Clear, StoreType.Store, WriteCoverage.Complete, default),
                static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _) => { });
            GraphBufferId output = edit.RegisterExternalBuffer(readback,
                [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            copy = edit.AddCopyFramePass("Read glyph pixels", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (target, output), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                        WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                    commands.CopyTextureToBuffer(new(commands.GetBuffer(data.Buffer), 0, 256, 64,
                        commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, 64, 64, 1)));
            edit.Commit();
        }
        renderer.ReconcileGraph(target);
        Assert.True(renderer.TryGetPass(canvas, out GraphPassId drawing));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.OrderAfter(copy, drawing);
            edit.Commit();
        }
        ulong structure = graph.StructureVersion;
        RenderGraphFrame cancelled = graph.BeginFrame();
        try { renderer.UpdateFrame(ref cancelled); }
        finally { cancelled.Dispose(); }

        byte[] reference = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Images", "Latin-A-48.pgm"))[^4096..];
        byte[]? firstMask = null;
        for (int frameIndex = 0; frameIndex < (regionsAndTree ? 1 : 3); frameIndex++)
        {
            world.AcquireSystemTick();
            if (frameIndex == 2)
                world.ExecuteBufferWrite<GlyphInstance>(canvas, buffer =>
                    buffer[0] = buffer[0] with { Color = new Color(0, 1, 0), Version = 2 });
            renderer.ReconcileGraph(target);
            RenderGraphFrame frame = graph.BeginFrame();
            try
            {
                renderer.UpdateFrame(ref frame);
                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                int count = frame.Execute(completions);
                foreach (QueueCompletion completion in completions.AsSpan(0, count))
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            }
            finally { frame.Dispose(); }
            using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
            pixels.Invalidate(new BufferRange(0, 64 * 256));
            var mask = new byte[4096];
            int error = 0;
            for (int index = 0; index < mask.Length; index++)
            {
                mask[index] = pixels.Bytes[index * 4 + 3];
                int difference = Math.Abs(mask[index] - reference[index]);
                Assert.InRange(difference, 0, 80);
                error += difference;
                Assert.Equal(mask[index], pixels.Bytes[index * 4 + 1]);
                Assert.Equal(frameIndex == 2 ? (byte)0 : mask[index], pixels.Bytes[index * 4]);
            }
            Assert.True(error <= 4096 * 3, $"Mean coverage error: {error / 4096.0:F3}/255.");
            if (firstMask is null) firstMask = mask;
            else Assert.Equal(firstMask, mask);
            Assert.Equal(structure, graph.StructureVersion);
        }
        if (!regionsAndTree) return;
        world.AcquireSystemTick();
        atlas.ClearDirtyRegions();
        GlyphLocation second = atlas.Get(font, 55);
        GlyphLocation third = atlas.Get(font, 35);
        Assert.Equal(0, second.Region.PageIndex);
        Assert.Equal(1, third.Region.PageIndex);
        world.Replace(atlasEntity, new RenderGlyphAtlas(atlas.Pages.ToArray()
            .Select(page => new GlyphPagePixels(page.Width, page.Height, page.Pixels.ToArray())).ToArray(),
            atlas.DirtyRegions.ToArray(), atlas.DistanceSpread, 2));
        world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer =>
        {
            buffer.Add(new(Entity.Null, new Rect(0, 0, 64, 64), new Color(1, 0, 0), 0,
                default, 0, new Rect(0, 0, 64, 64), 0, 1));
            buffer.Add(new(Entity.Null, new Rect(20, 16, 16, 20), new Color(0, 0, 1), 0,
                default, 0, new Rect(0, 0, 64, 64), 2, 1));
        });
        world.ExecuteBufferWrite<GlyphInstance>(canvas, buffer =>
        {
            buffer[0] = buffer[0] with { Color = new Color(1, 1, 1, 0.5f),
                ClipRectangle = new Rect(0, 0, 32, 64), DrawOrder = 1, Version = 2 };
            buffer.Add(new(Entity.Null, 1,
                new Rect(16 + second.Offset.X, 48 + second.Offset.Y, second.Region.Width, second.Region.Height),
                second.UV0, second.UV1, new Color(0, 1, 0), new Rect(40, 0, 24, 64), second.Region.PageIndex, 3, 1));
            buffer.Add(new(Entity.Null, 2,
                new Rect(third.Offset.X, 48 + third.Offset.Y, third.Region.Width, third.Region.Height),
                third.UV0, third.UV1, new Color(0, 0, 1), new Rect(0, 0, 12, 64), third.Region.PageIndex, 4, 1));
        });
        renderer.ReconcileGraph(target);
        RenderGraphFrame updated = graph.BeginFrame();
        try
        {
            renderer.UpdateFrame(ref updated);
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            int count = updated.Execute(completions);
            foreach (QueueCompletion completion in completions.AsSpan(0, count))
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        finally { updated.Dispose(); }
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(new BufferRange(0, 64 * 256));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, result.Bytes.Slice(22 * 256 + 27 * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, result.Bytes.Slice(44 * 256 + 40 * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, result.Bytes.Slice(20 * 256 + 41 * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, result.Bytes.Slice(30 * 256 + 5 * 4, 4).ToArray());
        Assert.Equal(255, result.Bytes[42 * 256 + 19 * 4]);
        Assert.InRange(result.Bytes[42 * 256 + 19 * 4 + 1], (byte)127, (byte)128);
        Assert.Equal(structure, graph.StructureVersion);
    }
}
