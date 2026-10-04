using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.Views;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.RenderGraph.Tests;

public sealed partial class CanvasCompositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnimatedSurfaceAndShadowFollowFlipThroughExtractionClipAndDepth(bool vulkan, bool worldSpace)
    {
        if (vulkan)
        {
            using IGraphicsBackend backend = new ValidationLayer(VulkanGraphicsBackend.Create(),
                new ValidationOptions(NativeValidationDisabled: true));
            using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
            CheckAnimatedShadow(backend, device, worldSpace);
        }
        else
        {
            using WarpGraphTestSupport graphics = WarpGraphTestSupport.Create(enableValidation: true);
            CheckAnimatedShadow(graphics.Backend, graphics.Device, worldSpace);
        }
    }

    private static void CheckAnimatedShadow(IGraphicsBackend backend, Device device, bool worldSpace)
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var renderDevice = new RenderDevice(backend, device);
        using var frames = new RenderFrameCoordinator(renderDevice);
        using var graph = new RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)]);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        Entity time = game.CreateEntity(new AnimationTime(0));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new TimelineSystem(time));
        systems.Add(new TrackSystem<Color, ColorOps>());
        systems.Add(new TrackSystem<Matrix3x2, Matrix3x2Ops>());
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());
        systems.Add(new LayoutSystem());
        systems.Add(new FlipSystem());
        Entity canvasTransform = game.CreateEntity(new WorldTransform
        {
            Qvvs = new TransformQvvs(new Vector3(-1, 1, 0.25f), Quaternion.Identity, 1f / 32),
        });
        Entity canvas = game.CreateEntity(new Canvas
        {
            Target = worldSpace ? CanvasTarget.WorldSpace(canvasTransform) : CanvasTarget.Screen,
            Size = new Size(64, 64), DepthTest = worldSpace,
        });
        Entity root = Views.Views.Spawn(game, canvas, new AnimatedShadow(false));
        using var extraction = new RenderExtractionSystems(render, new CanvasRenderExtractor());
        using SurfaceMaterial material = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(backend, device, graph, render, material, Format.R8G8B8A8UNorm);
        Entity view = render.CreateEntity(new RenderView(Matrix4x4.Identity, Matrix4x4.Identity, 64, 64));
        using SomeEngine.Graphics.Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(64 * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphTextureId target = CreateTarget(graph, readback, out GraphTextureId depth, out GraphPassId copy);
        var worldTarget = new WorldCanvasTarget(target, depth, view);
        GraphPassId drawing = default;
        ulong structure = 0;
        for (int index = 0; index < (worldSpace ? 5 : 4); index++)
        {
            if (index == 1) game.Replace(root, new AnimatedShadow(true));
            if (index == 4) game.Replace(canvasTransform, new WorldTransform
            {
                Qvvs = new TransformQvvs(new Vector3(-1, 1, 0.75f), Quaternion.Identity, 1f / 32),
            });
            game.Replace(time, new AnimationTime(index is 2 or 3 ? 0.5f : 0, gamePaused: true));
            systems.Update();
            extraction.Extract(game);
            using (var prepare = new RenderPrepareSystems(render, extraction))
            {
                Assert.True(frames.TryBeginPrepare(out var scope));
                using (scope)
                {
                    try { Assert.True(prepare.ApplyExtraction(scope)); }
                    finally { prepare.Shutdown(scope); }
                    scope.Commit();
                }
            }
            renderer.ReconcileGraph(target, worldTarget);
            Entity mirror = extraction.RequireMirror(canvas);
            Entity container = Hierarchy<ElementTree>.GetChildren(game, root)[0];
            Entity viewport = Hierarchy<ElementTree>.GetChildren(game, container)[0];
            Assert.Equal(new Rect(0, 0, 64, 27), game.Read<LayoutRect>(viewport).Value);
            render.ExecuteBufferRead<SurfaceInstance>(mirror, values =>
                Assert.Equal(new Rect(0, 0, 64, 27), Assert.Single(values.AsSpan().ToArray()).ClipRectangle));
            Assert.True(renderer.TryGetPass(mirror, out GraphPassId current));
            if (index == 0)
            {
                drawing = current;
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.OrderAfter(copy, drawing);
                edit.Commit();
                structure = graph.StructureVersion;
            }
            Assert.Equal(drawing, current);
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
            int center = index < 2 ? 16 : index == 2 ? 24 : 32;
            byte[] fill = index < 2 ? [128, 0, 0, 255] : index == 2 ? [96, 0, 96, 255]
                : index == 3 ? [0, 0, 255, 255] : [0, 0, 0, 255];
            byte[] shadow = index < 2 ? [0, 64, 0, 255] : index == 2 ? [0, 96, 0, 255]
                : index == 3 ? [0, 128, 0, 255] : [0, 0, 0, 255];
            Pixel(pixels.Bytes, center, 16, fill);
            Pixel(pixels.Bytes, center, 24, shadow);
            Pixel(pixels.Bytes, center, 28, [0, 0, 0, 255]);
            if (index >= 2) Pixel(pixels.Bytes, 16, 16, [0, 0, 0, 255]);
            Assert.Equal(structure, graph.StructureVersion);
        }
    }

    private readonly record struct AnimatedShadow(bool Moved) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 4).Content())
            using (Scope.View(new ScrollView(), 3).Size(64, 27).Content())
            using (Scope.View(new Absolute(), 1).Content())
            {
                Entity panel = Scope.View(new Absolute(), 2).Size(8, 8).Position(Moved ? 24 : 8, 8)
                    .Background(Moved ? new Color(0, 0, 1) : new Color(1, 0, 0))
                    .Shadow(new DropShadow(new Color(0, 1, 0, 0.5f), new Vector2(0, 8), 2))
                    .Animate(SurfaceChannels.Fill, Spec.Tween(1));
                Scope.Write<ElementTransform>(panel) = new ElementTransform
                {
                    Matrix = Matrix3x2.CreateTranslation(4, 4), Alpha = Moved ? 1 : 0.5f,
                };
                Flip.Enable(panel, Spec.Tween(1));
            }
            return default;
        }
    }
}
