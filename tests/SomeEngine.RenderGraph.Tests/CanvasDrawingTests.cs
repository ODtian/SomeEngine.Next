using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Graphics.Validation;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CanvasDrawingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundedSurfaceDrawsFillBorderAndClipPixels(bool vulkan)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckRoundedSurface(backend, device);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckRoundedSurface(graphics.Backend, graphics.Device);
        }
    }

    private static void CheckRoundedSurface(IGraphicsBackend backend, Device device)
    {
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using var graph = new RenderGraph(backend, device, [queue]);
        using var world = new RenderWorld();
        using SurfaceMaterial material = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(backend, device, graph, world, material, Format.R8G8B8A8UNorm);
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        Entity canvas = AddCanvas(world);
        world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer =>
            buffer.Add(new(Entity.Null, new Rect(8, 8, 40, 40), new Color(1, 0, 0), 10,
                new Color(0, 1, 0), 3, new Rect(0, 0, 40, 64), 0, 1)));
        GraphTextureId target;
        GraphPassId copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                64, 64, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
            GraphColorAttachmentViewId color = edit.CreateColorAttachmentView(target);
            edit.AddRasterFramePass("Clear canvas target", PassQueueSelection.AnyOfType(QueueType.Graphics), color,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphColorAttachmentViewId view) =>
                    pass.ColorAttachment(0, view, LoadType.Clear, StoreType.Store, WriteCoverage.Complete,
                        new System.Numerics.Vector4(0, 0, 0, 1)),
                static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _) => { });
            GraphBufferId output = edit.RegisterExternalBuffer(readback,
                [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            copy = edit.AddCopyFramePass("Read canvas pixels", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (target, output), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                {
                    pass.Read(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                        ResourceAccess.CopySource, TextureLayout.CopySource);
                    pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer) data) =>
                    commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0,
                        256, 64, commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color,
                        0, 0, 0, 64, 64, 1)));
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
        for (int index = 0; index < 5; index++)
        {
            world.AcquireSystemTick();
            if (index == 2)
            {
                world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer =>
                    buffer[0] = buffer[0] with { Fill = new Color(0, 0, 1), Version = 2 });
                renderer.ReconcileGraph(target);
                RenderGraphFrame cancelled = graph.BeginFrame();
                try { renderer.UpdateFrame(ref cancelled); }
                finally { cancelled.Dispose(); }
            }
            if (index == 3)
                world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer =>
                    buffer.Add(new(Entity.Null, new Rect(20, 20, 16, 16), new Color(1, 1, 1, 0.5f), 2,
                        default, 0, new Rect(0, 0, 64, 64), 1, 1)));
            if (index == 4) world.Remove<RenderCanvas>(canvas);

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
            if (index == 4)
            {
                AssertPixel(pixels.Bytes, 24, 24, [0, 0, 0, 255]);
                AssertPixel(pixels.Bytes, 8, 28, [0, 0, 0, 255]);
                Assert.Equal(structure + 1, graph.StructureVersion);
                continue;
            }
            byte[] fill = index < 2 ? [255, 0, 0, 255] : [0, 0, 255, 255];
            if (index == 3)
            {
                ReadOnlySpan<byte> blended = pixels.Bytes.Slice(24 * 256 + 24 * 4, 4);
                Assert.InRange(blended[0], (byte)127, (byte)128);
                Assert.InRange(blended[1], (byte)127, (byte)128);
                Assert.Equal(255, blended[2]);
                Assert.Equal(255, blended[3]);
            }
            else AssertPixel(pixels.Bytes, 24, 24, fill);
            AssertPixel(pixels.Bytes, 8, 8, [0, 0, 0, 255]);
            AssertPixel(pixels.Bytes, 8, 28, [0, 255, 0, 255]);
            AssertPixel(pixels.Bytes, 11, 28, fill);
            AssertPixel(pixels.Bytes, 39, 28, fill);
            AssertPixel(pixels.Bytes, 40, 28, [0, 0, 0, 255]);
            Assert.InRange(pixels.Bytes[11 * 256 + 10 * 4 + 1], (byte)140, (byte)155);
            Assert.Equal(structure, graph.StructureVersion);
        }
    }

    private static void AssertPixel(ReadOnlySpan<byte> pixels, int x, int y, byte[] expected) =>
        Assert.Equal(expected, pixels.Slice(y * 256 + x * 4, 4).ToArray());

    [Fact]
    public void CanvasChangesKeepOtherPassesAndFrameDataKeepsGraphStructure()
    {
        using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(graphics.Backend, graphics.Device, [graphics.GraphicsQueue]);
        using var world = new RenderWorld();
        using SurfaceMaterial material = SurfaceMaterial.Create(graphics.Device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(graphics.Backend, graphics.Device, graph, world, material,
            Format.R8G8B8A8UNorm);
        GraphTextureId target;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                64, 64, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment));
            edit.Commit();
        }

        Entity first = AddCanvas(world);
        ulong before = graph.StructureVersion;
        renderer.ReconcileGraph(target);
        Assert.Equal(before + 1, graph.StructureVersion);
        Assert.True(renderer.TryGetPass(first, out GraphPassId firstPass));

        ulong unchanged = graph.StructureVersion;
        for (int index = 0; index < 3; index++)
        {
            renderer.ReconcileGraph(target);
            RenderGraphFrame frame = graph.BeginFrame();
            try { renderer.UpdateFrame(ref frame); }
            finally { frame.Dispose(); }
            Assert.Equal(unchanged, graph.StructureVersion);
            Assert.True(renderer.TryGetPass(first, out GraphPassId retained));
            Assert.Equal(firstPass, retained);
        }

        world.AcquireSystemTick();
        world.Replace(first, new RenderCanvas(CanvasTarget.Screen, new Size(64, 64), 2));
        world.ExecuteBufferWrite<SurfaceInstance>(first, buffer =>
            buffer.Add(new(Entity.Null, new Rect(4, 4, 16, 16), new Color(1, 0, 0), 4,
                default, 0, new Rect(0, 0, 32, 32), 0, 1)));
        renderer.ReconcileGraph(target);
        RenderGraphFrame changed = graph.BeginFrame();
        try { renderer.UpdateFrame(ref changed); }
        finally { changed.Dispose(); }
        Assert.Equal(unchanged, graph.StructureVersion);

        Entity second = AddCanvas(world);
        renderer.ReconcileGraph(target);
        Assert.Equal(unchanged + 1, graph.StructureVersion);
        Assert.True(renderer.TryGetPass(second, out GraphPassId secondPass));
        Assert.True(renderer.TryGetPass(first, out GraphPassId sameFirst));
        Assert.Equal(firstPass, sameFirst);

        world.Remove<RenderCanvas>(first);
        renderer.ReconcileGraph(target);
        Assert.Equal(unchanged + 2, graph.StructureVersion);
        Assert.False(renderer.TryGetPass(first, out _));
        Assert.True(renderer.TryGetPass(second, out GraphPassId sameSecond));
        Assert.Equal(secondPass, sameSecond);
    }

    private static Entity AddCanvas(RenderWorld world)
    {
        Entity canvas = world.CreateEntity(new RenderCanvas(CanvasTarget.Screen, new Size(64, 64), 1));
        world.AddBuffer<SurfaceInstance>(canvas);
        return canvas;
    }
}
