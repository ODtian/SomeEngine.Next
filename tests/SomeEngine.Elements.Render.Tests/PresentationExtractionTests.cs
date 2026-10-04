using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements.Render;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Systems;

namespace SomeEngine.Elements.Render.Tests;

public sealed partial class SurfaceExtractionTests
{
    [Fact]
    public void ExtractionCombinesLayoutUserTransformAndPresentationOffset()
    {
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new SurfaceRenderExtractor());
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity panel = AddPanel(game, canvas, new Rect(10, 20, 120, 80), new Color(1, 0, 0));
        game.Add(panel, new ElementTransform { Matrix = Matrix3x2.CreateScale(2), Alpha = 0.8f });
        game.Add(panel, new PresentationOffset { Matrix = Matrix3x2.CreateTranslation(-4, 6), Alpha = 0.5f });
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance before = Assert.Single(ReadInstances(render, mirror));

        Assert.Equal(new Rect(10, 20, 120, 80), before.Rectangle);
        Assert.Equal(new Vector2(6, 26), Vector2.Transform(new Vector2(10, 20), before.Matrix));
        Assert.Equal(new Vector2(246, 186), Vector2.Transform(new Vector2(130, 100), before.Matrix));
        Assert.Equal(0.4f, before.Alpha);
        Assert.Equal(new Color(1, 0, 0), before.Fill);

        game.Replace(panel, new PresentationOffset { Matrix = Matrix3x2.CreateTranslation(0, 2), Alpha = 1 });
        extraction.Extract(game);
        game.Replace(panel, PresentationOffset.Identity);
        graphics.Apply(extraction, render);
        SurfaceInstance after = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(new Vector2(10, 22), Vector2.Transform(new Vector2(10, 20), after.Matrix));
        Assert.Equal(0.8f, after.Alpha);
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(new ElementTransform { Matrix = Matrix3x2.CreateScale(2), Alpha = 0.8f }, game.Read<ElementTransform>(panel));

        extraction.Extract(game);
        graphics.Apply(extraction, render);
        SurfaceInstance completed = Assert.Single(ReadInstances(render, mirror));
        Assert.Equal(new Vector2(10, 20), Vector2.Transform(new Vector2(10, 20), completed.Matrix));
        Assert.NotEqual(after.Version, completed.Version);

        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(completed, Assert.Single(ReadInstances(render, mirror)));
    }
}
