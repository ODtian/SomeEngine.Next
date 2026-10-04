using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Elements.Render.Tests;

public sealed partial class SurfaceExtractionTests(SurfaceExtractionDevice graphics) : IClassFixture<SurfaceExtractionDevice>
{
    [Fact]
    public void RecoloringOneNodeChangesOnlyItsRenderedInstance()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity first = AddPanel(game, canvas, new Rect(10, 20, 120, 80), new Color(1, 0, 0));
        Entity second = AddPanel(game, canvas, new Rect(200, 30, 50, 40), new Color(0, 1, 0));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance[] before = ReadInstances(render, mirror);

        Surface surface = game.Read<Surface>(first);
        surface.Fill = new Color(0, 0, 1);
        game.Replace(first, surface);
        extraction.Extract(game);
        Assert.Equal(before, ReadInstances(render, mirror));
        graphics.Apply(extraction, render);
        SurfaceInstance[] after = ReadInstances(render, mirror);

        Assert.Equal(2, after.Length);
        Assert.Equal(first, after[0].Node);
        Assert.Equal(new Rect(10, 20, 120, 80), after[0].Rectangle);
        Assert.Equal(new Color(0, 0, 1), after[0].Fill);
        Assert.Equal(4, after[0].CornerRadius);
        Assert.Equal(new Color(0.2f, 0.3f, 0.4f), after[0].BorderColor);
        Assert.Equal(2, after[0].BorderWidth);
        Assert.Equal(new Rect(0, 0, 800, 600), after[0].ClipRectangle);
        Assert.NotEqual(before[0].Version, after[0].Version);
        Assert.Equal(second, after[1].Node);
        Assert.Equal(before[1], after[1]);
    }

    [Fact]
    public void UnchangedFramesPreserveTheCanvasBufferAndInstanceVersions()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        AddPanel(game, canvas, new Rect(10, 20, 120, 80), new Color(1, 0, 0));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance[] before = ReadInstances(render, mirror);
        uint version = render.CurrentTick;

        for (int frame = 0; frame < 3; frame++)
        {
            extraction.Extract(game);
            graphics.Apply(extraction, render);
            Assert.Equal(mirror, extraction.RequireMirror(canvas));
            Assert.Equal(before, ReadInstances(render, mirror));
            Assert.False(BufferChanged(render, mirror, version));
        }
    }

    [Fact]
    public void ReleasedNodesLeaveTheirCanvasBufferInTreeOrder()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity later = AddPanel(game, canvas, new Rect(40, 0, 20, 20), new Color(0, 0, 1));
        Entity earlier = AddPanel(game, canvas, new Rect(0, 0, 20, 20), new Color(1, 0, 0));
        Entity child = AddPanel(game, earlier, new Rect(5, 5, 10, 10), new Color(0, 1, 0));
        Hierarchy<ElementTree>.Reorder(game, earlier, 0);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance[] before = ReadInstances(render, mirror);
        Assert.Equal(new[] { earlier, child, later }, before.Select(value => value.Node));
        Assert.Equal(new[] { 1, 2, 3 }, before.Select(value => value.DrawOrder));

        Hierarchy<ElementTree>.DestroySubtree(game, earlier);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance remaining = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(later, remaining.Node);
        Assert.Equal(1, remaining.DrawOrder);

        game.DestroyEntity(later);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Empty(ReadInstances(render, mirror));
    }

    [Fact]
    public void RemovingCanvasDataReleasesItsBufferWhileKeepingOtherRenderData()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor(), new LightRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        game.Add(canvas, new SomeEngine.Render.Components.PointLight());
        AddPanel(game, canvas, new Rect(0, 0, 20, 20), new Color(1, 0, 0));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);

        game.Remove<Canvas>(canvas);
        extraction.Extract(game);
        graphics.Apply(extraction, render);

        Assert.True(render.IsAlive(mirror));
        Assert.True(render.Has<SomeEngine.Render.Components.RenderPointLight>(mirror));
        Assert.False(render.Has<RenderCanvas>(mirror));
        Assert.False(render.HasBuffer<SurfaceInstance>(mirror));
    }

    [Fact]
    public void MovingAndReorderingNodesUpdatesTheirCanvasTreeOrder()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity firstCanvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity secondCanvas = game.CreateEntity(new Canvas { Size = new Size(400, 300), Scale = 2 });
        Entity first = AddPanel(game, firstCanvas, new Rect(0, 0, 20, 20), new Color(1, 0, 0));
        Entity second = AddPanel(game, firstCanvas, new Rect(30, 0, 20, 20), new Color(0, 1, 0));
        Entity third = AddPanel(game, secondCanvas, new Rect(0, 0, 20, 20), new Color(0, 0, 1));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity firstMirror = extraction.RequireMirror(firstCanvas);
        Entity secondMirror = extraction.RequireMirror(secondCanvas);
        SurfaceInstance thirdBefore = Assert.Single(ReadInstances(render, secondMirror));
        uint version = render.CurrentTick;

        Hierarchy<ElementTree>.Reorder(game, second, 0);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(new[] { second, first }, ReadInstances(render, firstMirror).Select(value => value.Node));
        Assert.Equal(thirdBefore, Assert.Single(ReadInstances(render, secondMirror)));
        Assert.False(BufferChanged(render, secondMirror, version));

        Hierarchy<ElementTree>.SetParent(game, first, secondCanvas, 0);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(second, Assert.Single(ReadInstances(render, firstMirror)).Node);
        SurfaceInstance[] moved = ReadInstances(render, secondMirror);
        Assert.Equal(new[] { first, third }, moved.Select(value => value.Node));
        Assert.Equal(new[] { 1, 2 }, moved.Select(value => value.DrawOrder));
        Assert.Equal(new Rect(0, 0, 200, 150), moved[0].ClipRectangle);
        Assert.Equal(new Rect(0, 0, 200, 150), moved[1].ClipRectangle);
    }

    [Fact]
    public void LayoutAndCanvasScaleChangesUpdateDrawingDataAndClipping()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity first = AddPanel(game, canvas, new Rect(0, 0, 20, 20), new Color(1, 0, 0));
        AddPanel(game, canvas, new Rect(30, 0, 20, 20), new Color(0, 1, 0));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance[] before = ReadInstances(render, mirror);

        game.Replace(first, new LayoutRect { Value = new Rect(5, 10, 25, 30) });
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance[] moved = ReadInstances(render, mirror);
        Assert.Equal(new Rect(5, 10, 25, 30), moved[0].Rectangle);
        Assert.NotEqual(before[0].Version, moved[0].Version);
        Assert.Equal(before[1], moved[1]);

        game.Replace(canvas, new Canvas { Size = new Size(640, 480), Scale = 2 });
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance[] resized = ReadInstances(render, mirror);
        Assert.Equal(new RenderCanvas(CanvasTarget.Screen, new Size(640, 480), 2), render.Read<RenderCanvas>(mirror));
        Assert.Equal(new Rect(0, 0, 320, 240), resized[0].ClipRectangle);
        Assert.Equal(new Rect(0, 0, 320, 240), resized[1].ClipRectangle);
        Assert.NotEqual(moved[0].Version, resized[0].Version);
        Assert.NotEqual(moved[1].Version, resized[1].Version);
    }

    [Fact]
    public void ViewModifiersAndLayoutReachRenderingThroughExtraction()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity view = Views.Views.Spawn(game, canvas, new ExtractionPanel(new Color(1, 0, 0)));
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance before = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(new Rect(0, 0, 120, 80), before.Rectangle);
        Assert.Equal(new Color(1, 0, 0), before.Fill);
        Assert.Equal(4, before.CornerRadius);
        uint version = render.CurrentTick;

        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(before, Assert.Single(ReadInstances(render, mirror)));
        Assert.False(BufferChanged(render, mirror, version));

        game.Replace(view, new ExtractionPanel(new Color(0, 0, 1)));
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance after = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(before.Node, after.Node);
        Assert.Equal(new Color(0, 0, 1), after.Fill);
        Assert.NotEqual(before.Version, after.Version);
    }

    [Fact]
    public void RenderPreparationUsesTheCollectedCanvasValues()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600), Scale = 2 });
        AddPanel(game, canvas, new Rect(0, 0, 20, 20), new Color(1, 0, 0));
        extraction.Extract(game);
        game.Replace(canvas, new Canvas { Size = new Size(800, 600), Scale = 4 });
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        Assert.Equal(2, render.Read<RenderCanvas>(mirror).Scale);
        Assert.Equal(new Rect(0, 0, 400, 300), Assert.Single(ReadInstances(render, mirror)).ClipRectangle);

        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(4, render.Read<RenderCanvas>(mirror).Scale);
        Assert.Equal(new Rect(0, 0, 200, 150), Assert.Single(ReadInstances(render, mirror)).ClipRectangle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemovingSurfaceOrLayoutDataRemovesTheInstance(bool removeSurface)
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity panel = AddPanel(game, canvas, new Rect(0, 0, 20, 20), new Color(1, 0, 0));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        Assert.Single(ReadInstances(render, mirror));

        if (removeSurface) game.Remove<Surface>(panel);
        else game.Remove<LayoutRect>(panel);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Empty(ReadInstances(render, mirror));

        if (removeSurface) game.Add(panel, new Surface { Fill = new Color(0, 1, 0) });
        else game.Add(panel, new LayoutRect { Value = new Rect(5, 10, 20, 25) });
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance restored = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(panel, restored.Node);
        Assert.Equal(removeSurface ? new Color(0, 1, 0) : new Color(1, 0, 0), restored.Fill);
        Assert.Equal(removeSurface ? new Rect(0, 0, 20, 20) : new Rect(5, 10, 20, 25), restored.Rectangle);
    }

    private readonly record struct ExtractionPanel(Color Fill) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Column().Size(120, 80).Background(Fill).Corner(4);
            return default;
        }
    }

    private static Entity AddPanel(World world, Entity parent, Rect rectangle, Color fill)
    {
        Hierarchy<ElementTree>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Ordered);
        Entity panel = world.CreateEntity(new Surface
        {
            Fill = fill, CornerRadius = 4, BorderColor = new Color(0.2f, 0.3f, 0.4f), BorderWidth = 2,
        });
        world.Add(panel, new LayoutRect { Value = rectangle });
        Hierarchy<ElementTree>.SetParent(world, panel, parent);
        return panel;
    }

    private static SurfaceInstance[] ReadInstances(RenderWorld world, Entity canvas)
    {
        SurfaceInstance[] result = [];
        world.ExecuteBufferRead<SurfaceInstance>(canvas, buffer => result = buffer.AsSpan().ToArray());
        return result;
    }

    private static bool BufferChanged(RenderWorld world, Entity canvas, uint version)
    {
        QueryHandle query = world.Query(new QueryDefinitionBuilder().ReadBuffer<SurfaceInstance>());
        bool changed = false;
        try
        {
            world.ExecuteQuery(query, version, rows =>
            {
                foreach (QueryChunkView chunk in rows.Chunks)
                    for (int row = 0; row < chunk.Count; row++)
                        if (chunk.Entities[row] == canvas)
                            changed = chunk.RowBufferChangedSinceLastSystemVersion<SurfaceInstance>(row);
            });
        }
        finally { world.ReleaseQuery(query); }
        return changed;
    }
}

public sealed class SurfaceExtractionDevice : IDisposable
{
    private readonly IGraphicsBackend _backend = VulkanGraphicsBackend.Create();
    private readonly Device _device;
    private readonly RenderDevice _renderDevice;
    private readonly RenderFrameCoordinator _frames;

    public SurfaceExtractionDevice()
    {
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        _backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        _backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo adapter = adapters[0];
        _device = _backend.CreateDevice(new DeviceDesc(adapter.Id, [new DeviceQueueDesc(QueueType.Graphics)]));
        _renderDevice = new RenderDevice(_backend, _device);
        _frames = new RenderFrameCoordinator(_renderDevice);
    }

    public void Apply(RenderExtractionSystems extraction, RenderWorld world)
    {
        using var systems = new RenderPrepareSystems(world, extraction);
        Assert.True(_frames.TryBeginPrepare(out var scope));
        using (scope)
        {
            try { Assert.True(systems.ApplyExtraction(scope)); }
            finally { systems.Shutdown(scope); }
            scope.Commit();
        }
    }

    public void Dispose()
    {
        _frames.Dispose();
        _renderDevice.Dispose();
        _device.Dispose();
        _backend.Dispose();
    }
}
