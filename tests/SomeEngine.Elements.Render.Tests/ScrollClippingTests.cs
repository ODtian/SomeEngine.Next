using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements.Render;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Systems;
using SomeEngine.Views;
using static SomeEngine.Elements.ScrollViewFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Render.Tests;

public sealed partial class SurfaceExtractionTests
{
    [Fact]
    public void NestedScrollViewsClipDrawnContentAndRefreshAfterViewportChanges()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(game, canvas, new ScrollClipScene());
        systems.Update();
        Entity panel = Hierarchy<ElementTree>.GetChildren(game, root)[0];
        Entity outer = Hierarchy<ElementTree>.GetChildren(game, panel)[0];
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance before = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(new Rect(90, 40, 30, 30), before.ClipRectangle);

        Box box = game.Read<Box>(outer);
        box.Fixed = new Size(120, 80);
        game.Replace(outer, box);
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance after = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(before.Node, after.Node);
        Assert.Equal(before.Rectangle, after.Rectangle);
        Assert.Equal(new Rect(90, 40, 50, 50), after.ClipRectangle);
        Assert.NotEqual(before.Version, after.Version);
    }

    [Fact]
    public void AddingAndRemovingAScrollViewportUpdatesItsExistingDescendantClip()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity viewport = game.CreateEntity(new LayoutRect { Value = new Rect(20, 10, 100, 60) });
        Hierarchy<ElementTree>.SetParent(game, viewport, canvas);
        AddPanel(game, viewport, new Rect(20, 10, 180, 100), new Color(1, 0, 0));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance before = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(new Rect(0, 0, 800, 600), before.ClipRectangle);

        game.Add(viewport, new ScrollOffset());
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance clipped = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(before.Node, clipped.Node);
        Assert.Equal(new Rect(20, 10, 100, 60), clipped.ClipRectangle);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(clipped, Assert.Single(ReadInstances(render, mirror)));

        game.Remove<ScrollOffset>(viewport);
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance restored = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(before.Node, restored.Node);
        Assert.Equal(before.ClipRectangle, restored.ClipRectangle);
    }

    private readonly record struct ScrollClipScene() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollView().Size(100, 60).Position(20, 10).Content())
            using (Absolute().Size(240, 180).Content())
            using (ScrollView().Size(80, 50).Position(70, 30).Content())
                Absolute().Size(160, 100).Background(new Color(1, 0, 0));
            return default;
        }
    }
}
