using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Graphics.Validation;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CanvasShadowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurfaceShadowDrawsBehindFillAndUpdatesBlurOffsetClipAndColor(bool vulkan)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckShadow(backend, device);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckShadow(graphics.Backend, graphics.Device);
        }
    }

    private static void CheckShadow(IGraphicsBackend backend, Device device)
    {
        using var graph = new RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)]);
        using var world = new RenderWorld();
        using SurfaceMaterial material = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(backend, device, graph, world, material, Format.R8G8B8A8UNorm);
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        Entity canvas = world.CreateEntity(new RenderCanvas(CanvasTarget.Screen, new Size(64, 64), 1));
        world.AddBuffer<SurfaceInstance>(canvas);
        world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer =>
            buffer.Add(new SurfaceInstance(Entity.Null, new Rect(8, 8, 24, 24), new Color(1, 0, 0), 4,
                default, 0, new Rect(0, 0, 38, 64), 0, 1)
            {
                Shadow = new DropShadow(new Color(0, 1, 0, 0.5f), new Vector2(8, 8), 0),
            }));
        GraphTextureId target;
        GraphPassId copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                64, 64, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
            GraphColorAttachmentViewId color = edit.CreateColorAttachmentView(target);
            edit.AddRasterFramePass("Clear shadow target", PassQueueSelection.AnyOfType(QueueType.Graphics), color,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphColorAttachmentViewId view) =>
                    pass.ColorAttachment(0, view, LoadType.Clear, StoreType.Store, WriteCoverage.Complete,
                        new Vector4(0, 0, 0, 1)),
                static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _) => { });
            GraphBufferId output = edit.RegisterExternalBuffer(readback,
                [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            copy = edit.AddCopyFramePass("Read shadow pixels", PassQueueSelection.AnyOfType(QueueType.Graphics),
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

        for (int index = 0; index < 4; index++)
        {
            world.AcquireSystemTick();
            if (index == 1)
                world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer => buffer[0] = buffer[0] with
                {
                    Shadow = new DropShadow(new Color(0, 1, 0, 0.5f), new Vector2(8, 8), 4),
                    ClipRectangle = new Rect(0, 0, 64, 64), Version = 2,
                });
            if (index == 2)
                world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer => buffer[0] = buffer[0] with
                {
                    Shadow = new DropShadow(new Color(0, 0, 1), new Vector2(-4, 8), 0), Version = 3,
                });
            if (index == 3)
                world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer => buffer[0] = buffer[0] with
                {
                    Shadow = default, Version = 4,
                });

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
            AssertPixel(pixels.Bytes, 20, 20, [255, 0, 0, 255]);
            switch (index)
            {
                case 0:
                    AssertGreenShadow(pixels.Bytes, 35, 24, 127, 128);
                    AssertPixel(pixels.Bytes, 38, 24, [0, 0, 0, 255]);
                    break;
                case 1:
                    AssertGreenShadow(pixels.Bytes, 41, 24, 27, 33);
                    AssertGreenShadow(pixels.Bytes, 39, 24, 74, 78);
                    break;
                case 2:
                    AssertPixel(pixels.Bytes, 5, 24, [0, 0, 255, 255]);
                    AssertPixel(pixels.Bytes, 35, 24, [0, 0, 0, 255]);
                    break;
                case 3:
                    AssertPixel(pixels.Bytes, 5, 24, [0, 0, 0, 255]);
                    AssertPixel(pixels.Bytes, 35, 24, [0, 0, 0, 255]);
                    break;
            }
        }
    }

    private static void AssertPixel(ReadOnlySpan<byte> pixels, int x, int y, byte[] expected) =>
        Assert.Equal(expected, pixels.Slice(y * 256 + x * 4, 4).ToArray());

    private static void AssertGreenShadow(ReadOnlySpan<byte> pixels, int x, int y, byte minimum, byte maximum)
    {
        ReadOnlySpan<byte> color = pixels.Slice(y * 256 + x * 4, 4);
        Assert.Equal(0, color[0]);
        Assert.InRange(color[1], minimum, maximum);
        Assert.Equal(0, color[2]);
        Assert.Equal(255, color[3]);
    }
}
