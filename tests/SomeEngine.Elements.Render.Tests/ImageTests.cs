using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Layout;
using SomeEngine.Views;
using SomeEngine.Elements.Render;
using SomeEngine.Render.Systems;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Elements.Render.Tests;

public sealed partial class ImageTests(SurfaceExtractionDevice graphics) : IClassFixture<SurfaceExtractionDevice>
{
    [Theory]
    [InlineData(ImageFit.Contain, 80, 40, 0, 25, 100, 50, 0, 0, 1, 1)]
    [InlineData(ImageFit.Stretch, 80, 40, 0, 0, 100, 100, 0, 0, 1, 1)]
    [InlineData(ImageFit.Cover, 80, 40, 0, 0, 100, 100, 0.25f, 0, 0.5f, 1)]
    [InlineData(ImageFit.Contain, 40, 80, 25, 0, 50, 100, 0, 0, 1, 1)]
    [InlineData(ImageFit.Stretch, 40, 80, 0, 0, 100, 100, 0, 0, 1, 1)]
    [InlineData(ImageFit.Cover, 40, 80, 0, 0, 100, 100, 0, 0.25f, 1, 0.5f)]
    public void FittingPreservesTheLayoutBoxAndSuppliesTheDrawingRectangleAndUv(
        ImageFit fit, uint width, uint height, float x, float y, float drawWidth, float drawHeight,
        float u, float v, float uvWidth, float uvHeight)
    {
        using Texture texture = CreateTexture(width, height);
        var source = new AssetId<TextureAsset>(Guid.NewGuid());
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem(new ImageTextures(_ => texture)));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 150) });
        Entity root = Views.Views.Spawn(world, canvas, new Absolute());
        Entity image = Views.Views.Spawn(world, root, new Image(source, fit));
        world.Add(image, new Box { Fixed = new Size(100, 100) });
        systems.Update();

        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new CanvasRenderExtractor());
        extraction.Extract(world);
        graphics.Apply(extraction, render);
        ImageInstance instance = Assert.Single(ReadImages(render, extraction.RequireMirror(canvas)));

        Assert.Equal(new Rect(0, 0, 100, 100), world.Read<LayoutRect>(image).Value);
        Assert.Equal(new Rect(x, y, drawWidth, drawHeight), instance.Rectangle);
        Assert.Equal(new Rect(u, v, uvWidth, uvHeight), instance.Uv);
        Assert.Same(texture, instance.Texture);
    }

    [Fact]
    public void ImageMeasuresTheLoadedTexturesIntrinsicSize()
    {
        using Texture texture = CreateTexture(80, 40);
        var source = new AssetId<TextureAsset>(Guid.NewGuid());
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem(new ImageTextures(id => id == source ? texture : null)));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 150) });
        Entity root = Views.Views.Spawn(world, canvas, new Absolute());
        Entity image = Views.Views.Spawn(world, root, new Image(source));

        systems.Update();

        Assert.Equal(new Rect(0, 0, 80, 40), world.Read<LayoutRect>(image).Value);
    }

    [Fact]
    public void ReadyTexturesUpdateGeneratedImageDeclarationsAndExtractionInTheSameFrame()
    {
        using Texture texture = CreateTexture(80, 40);
        Texture? ready = null;
        var source = new AssetId<TextureAsset>(Guid.NewGuid());
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem(new ImageTextures(_ => ready)));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 150) });
        Entity root = Views.Views.Spawn(world, canvas, new Absolute());
        Entity content = Views.Views.Spawn(world, root, new ImageContent(source));
        systems.Update();
        Entity image = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, content));
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new CanvasRenderExtractor());
        extraction.Extract(world);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        Assert.Equal(new Rect(7, 9, 1, 1), world.Read<LayoutRect>(image).Value);
        Assert.Null(Assert.Single(ReadImages(render, mirror)).Texture);

        ready = texture;
        systems.Update();
        extraction.Extract(world);
        graphics.Apply(extraction, render);
        Assert.Equal(new Rect(7, 9, 80, 40), world.Read<LayoutRect>(image).Value);
        ImageInstance loaded = Assert.Single(ReadImages(render, mirror));
        Assert.Same(texture, loaded.Texture);
        Assert.Equal(new Rect(7, 9, 80, 40), loaded.Rectangle);

        systems.Update();
        extraction.Extract(world);
        graphics.Apply(extraction, render);
        Assert.Equal(loaded, Assert.Single(ReadImages(render, mirror)));
    }

    [Fact]
    public void FittingUsesPaddingAndRespondsToImageParameters()
    {
        using Texture texture = CreateTexture(80, 40);
        var source = new AssetId<TextureAsset>(Guid.NewGuid());
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem(new ImageTextures(_ => texture)));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 150) });
        Entity root = Views.Views.Spawn(world, canvas, new Absolute());
        Entity image = Views.Views.Spawn(world, root, new Image(source));
        world.Add(image, new Box { Fixed = new Size(100, 100), Padding = new Thickness(10, 20, 10, 20) });
        systems.Update();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new CanvasRenderExtractor());
        extraction.Extract(world);
        graphics.Apply(extraction, render);
        Entity mirror = extraction.RequireMirror(canvas);
        Assert.Equal(new Rect(10, 30, 80, 40), Assert.Single(ReadImages(render, mirror)).Rectangle);

        world.Replace(image, new Box { Fixed = new Size(100, 100), Padding = new Thickness(10, 10, 10, 10) });
        world.Replace(image, new Image(source, ImageFit.Cover));
        systems.Update();
        extraction.Extract(world);
        graphics.Apply(extraction, render);
        ImageInstance covered = Assert.Single(ReadImages(render, mirror));
        Assert.Equal(new Rect(0, 0, 100, 100), world.Read<LayoutRect>(image).Value);
        Assert.Equal(new Rect(10, 10, 80, 80), covered.Rectangle);
        Assert.Equal(new Rect(0.25f, 0, 0.5f, 1), covered.Uv);
    }

    [Theory]
    [InlineData(ImageFit.Contain)]
    [InlineData(ImageFit.Stretch)]
    [InlineData(ImageFit.Cover)]
    public void ZeroSizeImagesSupplyFiniteEmptyDrawingData(ImageFit fit)
    {
        using Texture texture = CreateTexture(80, 40);
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem(new ImageTextures(_ => texture)));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 150) });
        Entity root = Views.Views.Spawn(world, canvas, new Absolute());
        Entity image = Views.Views.Spawn(world, root, new Image(default, fit));
        world.Add(image, new Box { Fixed = default(Size) });
        systems.Update();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new CanvasRenderExtractor());
        extraction.Extract(world);
        graphics.Apply(extraction, render);
        ImageInstance instance = Assert.Single(ReadImages(render, extraction.RequireMirror(canvas)));
        Assert.Equal(default(Rect), world.Read<LayoutRect>(image).Value);
        Assert.Equal(default(Rect), instance.Rectangle);
        Assert.Equal(new Rect(0, 0, 1, 1), instance.Uv);
    }

#if DEBUG
    [Fact]
    public void ImageMeasurementRequiresTheTextureDataStage()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 150) });
        Views.Views.Spawn(world, canvas, new Image(default));
        Assert.Contains("ImageTextures", Assert.Throws<InvalidOperationException>(systems.Update).Message);
    }

    [Fact]
    public void InvalidImageFittingIsReportedThroughDevelopmentChecks()
    {
        using Texture texture = CreateTexture(80, 40);
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem(new ImageTextures(_ => texture)));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(200, 150) });
        Views.Views.Spawn(world, canvas, new Image(default, (ImageFit)99));
        Assert.Contains("Image fitting", Assert.Throws<InvalidOperationException>(systems.Update).Message);
    }
#endif

    private record struct ImageContent(AssetId<TextureAsset> Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            ImageFunctions.Image(Source).Position(7, 9);
            return default;
        }
    }

    private static Texture CreateTexture(uint width, uint height)
    {
        var description = new TextureDesc(TextureDimension.Texture2D, width, height, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination);
        var tile = new SomeEngine.Resources.TextureMipTile
        {
            Width = width, Height = height, ChunkKey = 0, DecodedLength = width * height * 4,
            RowPitch = width * 4, SlicePitch = width * height * 4,
        };
        return Texture.Create(description, [new SomeEngine.Resources.TexturePixels(tile, new byte[width * height * 4])],
            TextureViewDimension.Texture2D);
    }

    private static ImageInstance[] ReadImages(RenderWorld world, Entity canvas)
    {
        ImageInstance[] result = [];
        world.ExecuteBufferRead<ImageInstance>(canvas, buffer => result = buffer.AsSpan().ToArray());
        return result;
    }
}
