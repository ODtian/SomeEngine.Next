using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Layout;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.RenderGraph.Tests;

public sealed partial class CanvasDrawingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WorldCanvasProjectsItsTransformAndCanUseSceneDepth(bool vulkan, bool reversedDepth)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckWorldCanvas(backend, device, reversedDepth);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckWorldCanvas(graphics.Backend, graphics.Device, reversedDepth);
        }
    }

    private static void CheckWorldCanvas(IGraphicsBackend backend, Device device, bool reversedDepth)
    {
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using var graph = new RenderGraph(backend, device, [queue]);
        using var world = new RenderWorld();
        using SurfaceMaterial material = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(backend, device, graph, world, material, Format.R8G8B8A8UNorm);
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        Entity view = world.CreateEntity(new RenderView(Matrix4x4.Identity, Matrix4x4.Identity, 64, 64));
        var canvasData = new RenderCanvas(CanvasTarget.WorldSpace(Entity.Null), new Size(0.5f, 0.5f), 1)
        {
            Transform = Matrix4x4.CreateTranslation(-0.75f, 0.75f, reversedDepth ? 0.25f : 0.75f),
        };
        Entity canvas = world.CreateEntity(canvasData);
        world.AddBuffer<SurfaceInstance>(canvas);
        world.ExecuteBufferWrite<SurfaceInstance>(canvas, buffer =>
            buffer.Add(new(Entity.Null, new Rect(0, 0, 0.5f, 0.5f), new Color(1, 0, 0), 0,
                default, 0, new Rect(0, 0, 0.5f, 0.5f), 0, 1)));

        GraphTextureId target;
        GraphTextureId depth;
        GraphPassId copy;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                64, 64, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
            depth = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                64, 64, 1, 1, 1, 1, Format.D32Float, TextureUsages.DepthStencilAttachment));
            var attachments = (Color: edit.CreateColorAttachmentView(target), Depth: edit.CreateDepthStencilView(depth,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
                Format.D32Float, TextureViewDimension.Texture2D));
            edit.AddRasterFramePass("Clear world canvas targets", PassQueueSelection.AnyOfType(QueueType.Graphics), attachments,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphColorAttachmentViewId Color, GraphDepthStencilViewId Depth) data) =>
                {
                    pass.ColorAttachment(0, data.Color, LoadType.Clear, StoreType.Store, WriteCoverage.Complete,
                        new Vector4(0, 0, 0, 1));
                    pass.DepthStencilAttachment(data.Depth, LoadType.Clear, StoreType.Store, WriteCoverage.Complete,
                        0.5f, LoadType.Discard, StoreType.Discard, WriteCoverage.Partial, 0);
                },
                static (ref RasterPassCommandScope _, in (GraphColorAttachmentViewId, GraphDepthStencilViewId) _) => { });
            GraphBufferId output = edit.RegisterExternalBuffer(readback,
                [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            copy = edit.AddCopyFramePass("Read world canvas pixels", PassQueueSelection.AnyOfType(QueueType.Graphics),
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

        var worldTarget = new WorldCanvasTarget(target, depth, view);
        if (reversedDepth) worldTarget = worldTarget with { DepthComparison = CompareOperation.GreaterOrEqual };
        GraphPassId previousPass = default;
        ulong structure = 0;
        for (int index = 0; index < 6; index++)
        {
            world.AcquireSystemTick();
            if (index == 1) canvasData = canvasData with { DepthTest = true };
            if (index == 2) canvasData = canvasData with { Transform = Matrix4x4.CreateTranslation(-0.75f, 0.75f, reversedDepth ? 0.75f : 0.25f) };
            if (index == 3) canvasData = canvasData with { Transform = Matrix4x4.CreateTranslation(0.25f, 0.25f, reversedDepth ? 0.75f : 0.25f) };
            if (index == 5) canvasData = canvasData with
            {
                Transform = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(-1.5f, 1.5f, -2), DepthTest = false,
            };
            world.Replace(canvas, canvasData);
            if (index == 4) world.Replace(view, new RenderView(Matrix4x4.CreateTranslation(-0.5f, 0, 0), Matrix4x4.Identity, 64, 64));
            if (index == 5) world.Replace(view, new RenderView(Matrix4x4.Identity,
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 1, 10), 64, 64));
            renderer.ReconcileGraph(target, worldTarget);
            Assert.True(renderer.TryGetPass(canvas, out GraphPassId drawing));
            if (drawing != previousPass)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.OrderAfter(copy, drawing);
                edit.Commit();
                previousPass = drawing;
                structure = graph.StructureVersion;
            }
            else Assert.Equal(structure, graph.StructureVersion);
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
            AssertPixel(pixels.Bytes, 16, 16, index is 0 or 2 or 5 ? [255, 0, 0, 255] : [0, 0, 0, 255]);
            AssertPixel(pixels.Bytes, 48, 32, index == 3 ? [255, 0, 0, 255] : [0, 0, 0, 255]);
            AssertPixel(pixels.Bytes, 32, 32, index == 4 ? [255, 0, 0, 255] : [0, 0, 0, 255]);
        }
    }
}
