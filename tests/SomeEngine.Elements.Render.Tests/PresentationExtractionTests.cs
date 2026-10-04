using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Elements.Render;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Systems;
using SomeEngine.Typography;
using SomeEngine.Views;
using Size = SomeEngine.Layout.Size;

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

public sealed partial class GlyphExtractionTests
{
    [Fact]
    public void SurfaceImageAndGlyphPresentationUpdatesShareTheExistingExtractionBoundary()
    {
        using Font font = LoadFont();
        using var game = new World();
        using var render = new RenderWorld();
        using var systems = TextSystems(game);
        Entity atlasSource = game.CreateEntity(new TextAtlas(new GlyphAtlas(128, 128)));
        using var extraction = new RenderExtractionSystems(render,
            new CanvasRenderExtractor(), new GlyphRenderExtractor(atlasSource));
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(320, 240) });
        game.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 48 }));
        Entity text = Views.Views.Spawn(game, canvas, new Text("A"));
        systems.Update();
        var rectangle = new Rect(10, 20, 120, 80);
        game.Replace(text, new LayoutRect { Value = rectangle });
        game.Add(text, new Surface { Fill = new Color(1, 0, 0) });
        Entity image = game.CreateEntity(new Image(default, ImageFit.Stretch));
        game.Add(image, new LayoutRect { Value = rectangle });
        Hierarchy<ElementTree>.SetParent(game, image, canvas);
        foreach (Entity node in new[] { text, image })
        {
            game.Add(node, new ElementTransform { Matrix = Matrix3x2.CreateScale(2), Alpha = 0.8f });
            game.Add(node, new PresentationOffset { Matrix = Matrix3x2.CreateTranslation(-4, 6), Alpha = 0.5f });
        }
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        SurfaceInstance surface = ReadSurface();
        GlyphInstance glyph = Assert.Single(ReadGlyphs(render, mirror));
        ImageInstance picture = ReadImage();
        uint atlasVersion = render.Read<RenderGlyphAtlas>(extraction.RequireMirror(atlasSource)).Version;
        foreach (var value in new[] { (surface.Matrix, surface.Alpha), (glyph.Matrix, glyph.Alpha), (picture.Matrix, picture.Alpha) })
        {
            Assert.Equal(new Vector2(6, 26), Vector2.Transform(new Vector2(10, 20), value.Matrix));
            Assert.Equal(new Vector2(246, 186), Vector2.Transform(new Vector2(130, 100), value.Matrix));
            Assert.Equal(0.4f, value.Alpha);
        }
        Assert.Equal(rectangle, picture.Rectangle);
        Assert.Equal(new Rect(0, 0, 1, 1), picture.Uv);
        Assert.Equal(new Color(1, 1, 1), glyph.Color);

        foreach (Entity node in new[] { text, image })
            game.Replace(node, new PresentationOffset { Matrix = Matrix3x2.CreateTranslation(0, 2), Alpha = 1 });
        extraction.Extract(game);
        foreach (Entity node in new[] { text, image }) game.Replace(node, PresentationOffset.Identity);
        graphics.Apply(extraction, render);
        SurfaceInstance nextSurface = ReadSurface();
        GlyphInstance nextGlyph = Assert.Single(ReadGlyphs(render, mirror));
        ImageInstance nextPicture = ReadImage();
        foreach (var value in new[] { (nextSurface.Matrix, nextSurface.Alpha), (nextGlyph.Matrix, nextGlyph.Alpha), (nextPicture.Matrix, nextPicture.Alpha) })
        {
            Assert.Equal(new Vector2(10, 22), Vector2.Transform(new Vector2(10, 20), value.Matrix));
            Assert.Equal(0.8f, value.Alpha);
        }
        Assert.NotEqual(surface.Version, nextSurface.Version);
        Assert.NotEqual(glyph.Version, nextGlyph.Version);
        Assert.NotEqual(picture.Version, nextPicture.Version);
        Assert.Equal(glyph.Rectangle, nextGlyph.Rectangle);
        Assert.Equal(glyph.UV0, nextGlyph.UV0);
        Assert.Equal(picture.Rectangle, nextPicture.Rectangle);
        Assert.Equal(picture.Uv, nextPicture.Uv);
        Assert.Equal(atlasVersion, render.Read<RenderGlyphAtlas>(extraction.RequireMirror(atlasSource)).Version);

        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(new Matrix3x2(2, 0, 0, 2, -10, -20), ReadSurface().Matrix);
        Assert.Equal(ReadSurface().Matrix, Assert.Single(ReadGlyphs(render, mirror)).Matrix);
        Assert.Equal(ReadSurface().Matrix, ReadImage().Matrix);

        SurfaceInstance ReadSurface()
        {
            SurfaceInstance[] values = [];
            render.ExecuteBufferRead<SurfaceInstance>(mirror, buffer => values = buffer.AsSpan().ToArray());
            return Assert.Single(values);
        }
        ImageInstance ReadImage()
        {
            ImageInstance[] values = [];
            render.ExecuteBufferRead<ImageInstance>(mirror, buffer => values = buffer.AsSpan().ToArray());
            return Assert.Single(values);
        }
    }
}
