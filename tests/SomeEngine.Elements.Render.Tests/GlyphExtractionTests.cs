using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Render.Systems;
using SomeEngine.Typography;
using SomeEngine.Views;
using Size = SomeEngine.Layout.Size;
using static SomeEngine.Elements.ScrollViewFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Elements.TextFunctions;

namespace SomeEngine.Elements.Render.Tests;

public sealed partial class GlyphExtractionTests(SurfaceExtractionDevice graphics) : IClassFixture<SurfaceExtractionDevice>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanvasViewportClipsAllMaterialsAcrossFullAndColorOnlyExtraction(bool jobWorld)
    {
        using Font font = LoadFont();
        using var game = new World(execution: jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null);
        using var render = new RenderWorld(execution: jobWorld ? new WorldJobs(SomeEngine.Job.JobSystem.Default) : null);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        var shaping = new TextShapingSystem();
        systems.Add(new ViewRebuildSystem());
        systems.Add(shaping);
        int layout = systems.Add(new LayoutSystem(shaping));
        Entity atlasSource = game.CreateEntity(new TextAtlas(new GlyphAtlas(128, 128)));
        using var extraction = new RenderExtractionSystems(render,
            new SurfaceRenderExtractor(), new GlyphRenderExtractor(atlasSource));
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(320, 240) });
        var clip = new Rect(10, 5, 100, 60);
        game.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 48 }));
        Entity node = Views.Views.Spawn(game, canvas, new Text("A"));
        game.Add(node, new TextStyle { Color = new Color(1, 1, 1) });
        systems.Update();
        systems.Disable(layout);
        game.Add(canvas, new ScrollOffset());
        game.Replace(canvas, new LayoutRect { Value = clip });
        game.Replace(node, new LayoutRect { Value = clip });
        game.Add(node, new Surface { Fill = new Color(1, 1, 1) });
        game.Add(node, new Image(default, ImageFit.Stretch));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        GlyphInstance glyph = Assert.Single(ReadGlyphs(render, mirror));
        SurfaceInstance[] surfaces = [];
        ImageInstance[] images = [];
        render.ExecuteBufferRead<SurfaceInstance>(mirror, buffer => surfaces = buffer.AsSpan().ToArray());
        render.ExecuteBufferRead<ImageInstance>(mirror, buffer => images = buffer.AsSpan().ToArray());
        Assert.Equal(clip, glyph.ClipRectangle);
        Assert.Equal(glyph.ClipRectangle, Assert.Single(surfaces).ClipRectangle);
        Assert.Equal(glyph.ClipRectangle, Assert.Single(images).ClipRectangle);
        Assert.Equal(glyph.DrawOrder, surfaces[0].DrawOrder);
        Assert.Equal(glyph.DrawOrder, images[0].DrawOrder);

        game.Replace(node, new TextStyle { Color = new Color(0, 1, 0) });
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        GlyphInstance changed = Assert.Single(ReadGlyphs(render, mirror));
        Assert.Equal(new Color(0, 1, 0), changed.Color);
        Assert.Equal(clip, changed.ClipRectangle);
        Assert.Equal(glyph.DrawOrder, changed.DrawOrder);
    }

    [Fact]
    public void TextReachesRenderingWithGlyphGeometryAndAtlasPixels()
    {
        using Font font = LoadFont();
        using var game = new World();
        using var render = new RenderWorld();
        using var systems = TextSystems(game);
        var atlas = new GlyphAtlas(128, 128);
        Entity atlasSource = game.CreateEntity(new TextAtlas(atlas));
        using var extraction = new RenderExtractionSystems(render,
            new SurfaceRenderExtractor(), new GlyphRenderExtractor(atlasSource));
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(320, 240) });
        game.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 48 }));
        Entity node = Views.Views.Spawn(game, canvas, new Text("A V"));

        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);

        Entity mirror = extraction.RequireMirror(canvas);
        GlyphInstance[] glyphs = ReadGlyphs(render, mirror);
        Assert.Equal(2, glyphs.Length);
        Assert.All(glyphs, glyph => Assert.Equal(node, glyph.Node));
        Assert.Equal(new[] { 0, 2 }, glyphs.Select(glyph => glyph.GlyphIndex));
        Assert.Equal(-8, glyphs[0].Rectangle.X);
        Assert.Equal(8.312f, glyphs[0].Rectangle.Y, 3);
        Assert.Equal(47, glyphs[0].Rectangle.Width);
        Assert.Equal(51, glyphs[0].Rectangle.Height);
        Assert.Equal(new Rect(0, 0, 320, 240), glyphs[0].ClipRectangle);
        Assert.Equal(new Color(1, 1, 1), glyphs[0].Color);
        Assert.Equal(1, glyphs[0].DrawOrder);
        GlyphLocation location = atlas.Get(font, 34);
        Assert.Equal(location.UV0, glyphs[0].UV0);
        Assert.Equal(location.UV1, glyphs[0].UV1);
        Assert.Equal(location.Region.PageIndex, glyphs[0].PageIndex);
        Entity atlasMirror = extraction.RequireMirror(atlasSource);
        Assert.Equal(atlasMirror, render.Read<CanvasGlyphAtlas>(mirror).Atlas);
        RenderGlyphAtlas pixels = render.Read<RenderGlyphAtlas>(atlasMirror);
        Assert.Equal(atlas.Pages[0].Pixels.ToArray(), pixels.Pages.Span[0].Pixels.ToArray());
        Assert.Empty(atlas.DirtyRegions.ToArray());
    }

    private static Font LoadFont() => Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));

    [Fact]
    public void NestedScrollViewportsClipGlyphsAndRefreshTheirIntersections()
    {
        using Font font = LoadFont();
        using var game = new World();
        using var render = new RenderWorld();
        using var systems = TextSystems(game);
        Entity atlasSource = game.CreateEntity(new TextAtlas(new GlyphAtlas(128, 128)));
        using var extraction = new RenderExtractionSystems(render,
            new SurfaceRenderExtractor(), new GlyphRenderExtractor(atlasSource));
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(320, 240) });
        game.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 48 }));
        Entity root = Views.Views.Spawn(game, canvas, new ScrollText());
        systems.Update();
        Entity panel = Hierarchy<ElementTree>.GetChildren(game, root)[0];
        Entity outer = Hierarchy<ElementTree>.GetChildren(game, panel)[0];
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        GlyphInstance before = Assert.Single(ReadGlyphs(render, mirror));
        Assert.Equal(new Rect(90, 40, 30, 30), before.ClipRectangle);

        Box box = game.Read<Box>(outer);
        box.Fixed = new Size(120, 80);
        game.Replace(outer, box);
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);

        GlyphInstance after = Assert.Single(ReadGlyphs(render, mirror));
        Assert.Equal(before.Rectangle, after.Rectangle);
        Assert.Equal(new Rect(90, 40, 50, 50), after.ClipRectangle);
    }

    private readonly record struct ScrollText() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollView().Size(100, 60).Position(20, 10).Content())
            using (Absolute().Size(240, 180).Content())
            using (ScrollView().Size(80, 50).Position(70, 30).Content())
                Text("A");
            return default;
        }
    }

    [Fact]
    public void ColorChangesKeepTheShapeAndAtlasAndUpdateOnlyGlyphColors()
    {
        using Font font = LoadFont();
        using var game = new World();
        using var render = new RenderWorld();
        using var systems = TextSystems(game);
        var atlas = new GlyphAtlas(128, 128);
        Entity atlasSource = game.CreateEntity(new TextAtlas(atlas));
        using var extraction = new RenderExtractionSystems(render,
            new SurfaceRenderExtractor(), new GlyphRenderExtractor(atlasSource));
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(320, 240) });
        game.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 48 }));
        Entity node = Views.Views.Spawn(game, canvas, new Text("A"));
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        Entity atlasMirror = extraction.RequireMirror(atlasSource);
        ShapedText shape = game.Read<ShapedText>(node);
        GlyphInstance before = Assert.Single(ReadGlyphs(render, mirror));
        RenderGlyphAtlas pixels = render.Read<RenderGlyphAtlas>(atlasMirror);
        uint pixelsVersion = render.WriteVersion<RenderGlyphAtlas>(atlasMirror);
        uint layoutVersion = game.WriteVersion<LayoutRect>(node);

        game.Add(node, new TextStyle { Color = new Color(0.2f, 0.4f, 0.6f) });
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);

        GlyphInstance after = Assert.Single(ReadGlyphs(render, mirror));
        Assert.Equal(before with { Color = after.Color, Version = after.Version }, after);
        Assert.Equal(new Color(0.2f, 0.4f, 0.6f), after.Color);
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal(shape.Version, game.Read<ShapedText>(node).Version);
        Assert.Equal(layoutVersion, game.WriteVersion<LayoutRect>(node));
        Assert.Equal(pixels, render.Read<RenderGlyphAtlas>(atlasMirror));
        Assert.Equal(pixelsVersion, render.WriteVersion<RenderGlyphAtlas>(atlasMirror));
        Assert.Empty(atlas.DirtyRegions.ToArray());
    }

    [Fact]
    public void EmptyTextBecomesVisibleAgainInItsTreePosition()
    {
        using Font font = LoadFont();
        using var game = new World();
        using var render = new RenderWorld();
        using var systems = TextSystems(game);
        Entity atlasSource = game.CreateEntity(new TextAtlas(new GlyphAtlas(128, 128)));
        using var extraction = new RenderExtractionSystems(render,
            new SurfaceRenderExtractor(), new GlyphRenderExtractor(atlasSource));
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(320, 240) });
        game.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 48 }));
        Entity node = Views.Views.Spawn(game, canvas, new Text("A"));
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        GlyphInstance before = Assert.Single(ReadGlyphs(render, mirror));

        node.SetText(game, "");
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Empty(ReadGlyphs(render, mirror));

        node.SetText(game, "A");
        systems.Update();
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        GlyphInstance after = Assert.Single(ReadGlyphs(render, mirror));
        Assert.Equal(before.DrawOrder, after.DrawOrder);
        Assert.Equal(before.Rectangle, after.Rectangle);
    }

    private static SystemGroup<ImmediateSystemContext> TextSystems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        var shaping = new TextShapingSystem();
        systems.Add(new ViewRebuildSystem());
        systems.Add(shaping);
        systems.Add(new LayoutSystem(shaping));
        return systems;
    }

    private static GlyphInstance[] ReadGlyphs(RenderWorld world, Entity canvas)
    {
        GlyphInstance[] glyphs = [];
        world.ExecuteBufferRead<GlyphInstance>(canvas, buffer => glyphs = buffer.AsSpan().ToArray());
        return glyphs;
    }
}
