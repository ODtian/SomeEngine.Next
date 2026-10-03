using System.Numerics;
using SomeEngine.Typography;

namespace SomeEngine.Typography.Tests;

public sealed class GlyphAtlasTests
{
    [Theory]
    [InlineData(2, 1024, 48, 8)]
    [InlineData(-1, 1024, 48, 8)]
    [InlineData(1024, 2, 48, 8)]
    [InlineData(1024, -1, 48, 8)]
    [InlineData(1024, 1024, 0, 8)]
    [InlineData(1024, 1024, -1, 8)]
    [InlineData(1024, 1024, 48, 1)]
    [InlineData(1024, 1024, 48, 33)]
    public void LoadingInvalidFileConfigurationReportsInvalidData(int width, int height, int fontSize, int spread)
    {
        var atlas = new GlyphAtlas();
        using var content = new MemoryStream();
        atlas.Save(content);
        content.Position = 2 * sizeof(int); // Signature and version precede the saved configuration.
        using (var writer = new BinaryWriter(content, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(width);
            writer.Write(height);
            writer.Write(fontSize);
            writer.Write(spread);
        }
        content.Position = 0;

        Assert.Throws<InvalidDataException>(() => GlyphAtlas.Load(content, []));
        Assert.True(content.CanRead);
    }

    [Fact]
    public async Task TextAndGlyphPixelsRemainReadableDuringGarbageCollection()
    {
        using Font font = LoadFont("Cjk.otf");
        using var output = new ShapeBuffer();
        var atlas = new GlyphAtlas();
        Task collection = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 20; iteration++) GC.Collect();
        });

        for (int iteration = 0; iteration < 20; iteration++)
        {
            Shaper.Shape("Hello世界", new TextStyleData(font, 20), 0, output);
            Assert.Equal(7, output.Shape.Glyphs.Length);
            Assert.Equal(89.12f, output.Shape.Size.Width, 3);
            foreach (Glyph glyph in output.Shape.Glyphs)
            {
                GlyphLocation location = atlas.Get(glyph.Font, glyph.Index);
                Assert.Contains(ReadPixels(atlas, location.Region), pixel => pixel > 128);
            }
        }
        await collection;
    }

    [Fact]
    public async Task DifferentAtlasSizesCanRasterizeASharedFontOnDifferentThreads()
    {
        using Font font = LoadFont("Latin.ttf");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            bool smaller = index % 2 == 0;
            var atlas = new GlyphAtlas(fontSize: smaller ? 32 : 48, distanceSpread: smaller ? 4 : 8);

            GlyphLocation location = atlas.Get(font, 34);

            Assert.Equal(smaller ? 29 : 47, location.Region.Width);
            Assert.Equal(smaller ? 31 : 51, location.Region.Height);
            Assert.Equal(smaller ? new Vector2(-4, -27) : new Vector2(-8, -43), location.Offset);
            byte[] pixels = ReadPixels(atlas, location.Region);
            Assert.Contains(pixels, pixel => pixel > 128);
            Assert.Contains(pixels, pixel => pixel is > 0 and < 128);
        })));
    }

#if DEBUG
    [Fact]
    public void InvalidAtlasArgumentsUseDevelopmentChecks()
    {
        Assert.Throws<InvalidOperationException>(() => new GlyphAtlas(pageWidth: 2));
        Assert.Throws<InvalidOperationException>(() => new GlyphAtlas(pageHeight: 0));
        Assert.Throws<InvalidOperationException>(() => new GlyphAtlas(fontSize: 0));
        Assert.Throws<InvalidOperationException>(() => new GlyphAtlas(distanceSpread: 1));
        Assert.Throws<InvalidOperationException>(() => new GlyphAtlas(distanceSpread: 33));
        using Font font = LoadFont("Latin.ttf");
        var atlas = new GlyphAtlas();
        Assert.Throws<InvalidOperationException>(() => atlas.Get(null!, 34));
        Assert.Throws<InvalidOperationException>(() => atlas.Get(font, uint.MaxValue));
        Assert.Throws<InvalidOperationException>(() => atlas.Save(null!));
        Assert.Throws<InvalidOperationException>(() => GlyphAtlas.Load(null!, [font]));
        font.Dispose();
        Assert.Throws<InvalidOperationException>(() => atlas.Get(font, 34));
    }

    [Fact]
    public void LoadingNeedsFontsFromTheBakedFontAssets()
    {
        using Font latin = LoadFont("Latin.ttf");
        using Font cjk = LoadFont("Cjk.otf");
        var atlas = new GlyphAtlas();
        atlas.Get(latin, 34);
        using var content = new MemoryStream();
        atlas.Save(content);
        content.Position = 0;

        Assert.Throws<InvalidOperationException>(() => GlyphAtlas.Load(content, [cjk]));
    }
#endif

    [Theory]
    [InlineData("Latin.ttf")]
    [InlineData("Cjk.otf")]
    public void GlyphsWithDifferentHeightsKeepSeparateUvsAcrossRowsAndPages(string fontName)
    {
        using Font font = LoadFont(fontName);
        using var output = new ShapeBuffer();
        Shaper.Shape("AgjVpQ", new TextStyleData(font, 20), 0, output);
        var atlas = new GlyphAtlas(pageWidth: 128, pageHeight: 160);
        var glyphs = new List<(GlyphLocation Location, byte[] Pixels)>();

        foreach (Glyph glyph in output.Shape.Glyphs)
        {
            GlyphLocation location = atlas.Get(glyph.Font, glyph.Index);
            glyphs.Add((location, ReadPixels(atlas, location.Region)));
        }

        foreach (var glyph in glyphs)
            Assert.Equal(glyph.Pixels, ReadPixels(atlas, glyph.Location.Region));
        for (int first = 0; first < glyphs.Count; first++)
        for (int second = first + 1; second < glyphs.Count; second++)
        {
            GlyphLocation left = glyphs[first].Location;
            GlyphLocation right = glyphs[second].Location;
            Assert.True(left.Region.PageIndex != right.Region.PageIndex ||
                left.UV1.X <= right.UV0.X || right.UV1.X <= left.UV0.X ||
                left.UV1.Y <= right.UV0.Y || right.UV1.Y <= left.UV0.Y);
        }
    }

    [Fact]
    public void BakedFontsBindByAssetBytesWithAnyInputOrder()
    {
        using Font latin = LoadFont("Latin.ttf");
        using Font cjk = LoadFont("Cjk.otf");
        var baked = new GlyphAtlas();
        GlyphLocation latinLocation = baked.Get(latin, 34);
        GlyphLocation cjkLocation = baked.Get(cjk, 34);
        using var content = new MemoryStream();
        baked.Save(content);
        content.Position = 0;
        using Font loadedCjk = LoadFont("Cjk.otf");
        using Font loadedLatin = LoadFont("Latin.ttf");
        using Font anotherLatin = LoadFont("Latin.ttf");

        GlyphAtlas loaded = GlyphAtlas.Load(content, [loadedCjk, loadedLatin, anotherLatin]);
        loaded.ClearDirtyRegions();

        Assert.True(latinLocation.UV1.X <= cjkLocation.UV0.X);
        Assert.Equal(latinLocation, loaded.Get(loadedLatin, 34));
        Assert.Equal(cjkLocation, loaded.Get(loadedCjk, 34));
        Assert.Equal(latinLocation, loaded.Get(anotherLatin, 34));
        Assert.Empty(loaded.DirtyRegions.ToArray());
    }

    [Fact]
    public void LoadedAtlasContinuesAddingGlyphsAndPreservesBakedLocations()
    {
        using Font font = LoadFont("Latin.ttf");
        var baked = new GlyphAtlas(pageWidth: 160, pageHeight: 120);
        GlyphLocation first = baked.Get(font, 34);
        byte[] firstPixels = ReadPixels(baked, first.Region);
        using var content = new MemoryStream();
        baked.Save(content);
        content.Position = 0;
        GlyphAtlas loaded = GlyphAtlas.Load(content, [font]);
        loaded.ClearDirtyRegions();

        GlyphLocation added = loaded.Get(font, 55);

        Assert.Single(loaded.Pages.ToArray());
        Assert.True(first.UV1.X <= added.UV0.X || first.UV1.Y <= added.UV0.Y);
        Assert.Equal(first, loaded.Get(font, 34));
        Assert.Equal(firstPixels, ReadPixels(loaded, first.Region));
        Assert.Equal(added.Region, Assert.Single(loaded.DirtyRegions.ToArray()));
    }

    [Fact]
    public void BakedChineseGlyphsLoadWithTheirPixelsAndLocationsReadyForExtraction()
    {
        using Font font = LoadFont("Cjk.otf");
        var baked = new GlyphAtlas(pageWidth: 60, pageHeight: 60, fontSize: 32, distanceSpread: 4);
        uint[] indices = [207, 208, 214, 216, 1];
        baked.Bake(font, indices);
        GlyphLocation[] locations = indices.Select(index => baked.Get(font, index)).ToArray();
        byte[][] pixels = baked.Pages.ToArray().Select(page => page.Pixels.ToArray()).ToArray();
        using var content = new MemoryStream();
        baked.Save(content);
        content.Position = 0;
        using Font loadedFont = LoadFont("Cjk.otf");

        GlyphAtlas loaded = GlyphAtlas.Load(content, [loadedFont]);

        Assert.True(content.CanRead);
        Assert.Equal(60, loaded.PageWidth);
        Assert.Equal(60, loaded.PageHeight);
        Assert.Equal(32, loaded.FontSize);
        Assert.Equal(4, loaded.DistanceSpread);
        Assert.True(loaded.Pages.Length > 1);
        Assert.Equal(pixels.Length, loaded.Pages.Length);
        for (int pageIndex = 0; pageIndex < pixels.Length; pageIndex++)
        {
            AtlasPage page = loaded.Pages[pageIndex];
            Assert.Equal(pixels[pageIndex], page.Pixels.ToArray());
            Assert.Contains(new AtlasDirtyRegion(pageIndex, 0, 0, page.Width, page.Height), loaded.DirtyRegions.ToArray());
        }
        loaded.ClearDirtyRegions();

        Assert.Equal(locations, indices.Select(index => loaded.Get(loadedFont, index)));
        Assert.Empty(loaded.DirtyRegions.ToArray());
        for (int pageIndex = 0; pageIndex < pixels.Length; pageIndex++)
            Assert.Equal(pixels[pageIndex], loaded.Pages[pageIndex].Pixels.ToArray());
    }

    [Fact]
    public void SpaceKeepsItsAdvanceAndUsesAnEmptyAtlasLocation()
    {
        using Font font = LoadFont("Latin.ttf");
        using var output = new ShapeBuffer();
        Shaper.Shape(" ", new TextStyleData(font, 20), 0, output);
        Glyph space = Assert.Single(output.Shape.Glyphs.ToArray());
        var atlas = new GlyphAtlas();

        GlyphLocation location = atlas.Get(space.Font, space.Index);

        Assert.True(space.Advance.X > 0);
        Assert.Equal(-1, location.Region.PageIndex);
        Assert.Equal(0, location.Region.Width);
        Assert.Equal(0, location.Region.Height);
        Assert.Equal(location.UV0, location.UV1);
        Assert.Empty(atlas.Pages.ToArray());
        Assert.Empty(atlas.DirtyRegions.ToArray());
        Assert.Equal(location, atlas.Get(space.Font, space.Index));
    }

    [Fact]
    public void OversizedGlyphGetsAPageThatContainsItsWholeDistanceField()
    {
        using Font font = LoadFont("Latin.ttf");
        var atlas = new GlyphAtlas(pageWidth: 24, pageHeight: 24);

        GlyphLocation location = atlas.Get(font, 34);

        AtlasPage page = Assert.Single(atlas.Pages.ToArray());
        Assert.True(location.Region.Width > 24 && location.Region.Height > 24);
        Assert.True(location.Region.X + location.Region.Width < page.Width);
        Assert.True(location.Region.Y + location.Region.Height < page.Height);
        Assert.True(location.UV1.X <= 1 && location.UV1.Y <= 1);
        Assert.Contains(ReadPixels(atlas, location.Region), pixel => pixel > 128);
    }

    [Fact]
    public void FullPageAddsAnotherPageAndKeepsEarlierLocations()
    {
        using Font font = LoadFont("Latin.ttf");
        var atlas = new GlyphAtlas(pageWidth: 60, pageHeight: 60, fontSize: 32);
        GlyphLocation first = atlas.Get(font, 34);
        byte[] pixels = atlas.Pages[first.Region.PageIndex].Pixels.ToArray();
        atlas.ClearDirtyRegions();

        GlyphLocation second = atlas.Get(font, 55);

        Assert.Equal(2, atlas.Pages.Length);
        Assert.Equal(0, first.Region.PageIndex);
        Assert.Equal(1, second.Region.PageIndex);
        Assert.Equal(first, atlas.Get(font, 34));
        Assert.Equal(pixels, atlas.Pages[0].Pixels.ToArray());
        Assert.Equal(second.Region, Assert.Single(atlas.DirtyRegions.ToArray()));
        Assert.All(atlas.Pages.ToArray(), page =>
        {
            Assert.Equal(60, page.Width);
            Assert.Equal(60, page.Height);
        });
    }

    [Fact]
    public void DifferentGlyphsOccupySeparateRegionsAndPreserveEarlierPixels()
    {
        using Font font = LoadFont("Latin.ttf");
        var atlas = new GlyphAtlas();
        GlyphLocation first = atlas.Get(font, 34);
        byte[] firstPixels = ReadPixels(atlas, first.Region);

        GlyphLocation second = atlas.Get(font, 55);

        Assert.Equal(first.Region.PageIndex, second.Region.PageIndex);
        Assert.True(first.UV1.X <= second.UV0.X || second.UV1.X <= first.UV0.X ||
            first.UV1.Y <= second.UV0.Y || second.UV1.Y <= first.UV0.Y);
        Assert.Equal(firstPixels, ReadPixels(atlas, first.Region));
        Assert.Equal(new[] { first.Region, second.Region }, atlas.DirtyRegions.ToArray());
    }

    [Fact]
    public void RepeatedGlyphKeepsItsDistanceFieldAndLocationAfterExtraction()
    {
        using Font font = LoadFont("Latin.ttf");
        var atlas = new GlyphAtlas();

        GlyphLocation first = atlas.Get(font, 34);

        AtlasPage page = atlas.Pages[first.Region.PageIndex];
        Assert.Equal(47, first.Region.Width);
        Assert.Equal(51, first.Region.Height);
        Assert.Equal(new Vector2(-8, -43), first.Offset);
        byte[] pixels = page.Pixels.ToArray();
        Assert.Contains(pixels, pixel => pixel > 128);
        Assert.Contains(pixels, pixel => pixel is > 0 and < 128);
        Assert.True(first.UV0.X >= 0 && first.UV0.Y >= 0);
        Assert.True(first.UV1.X > first.UV0.X && first.UV1.Y > first.UV0.Y);
        Assert.True(first.UV1.X <= 1 && first.UV1.Y <= 1);
        Assert.Equal(first.Region, Assert.Single(atlas.DirtyRegions.ToArray()));
        atlas.ClearDirtyRegions();

        GlyphLocation second = atlas.Get(font, 34);

        Assert.Equal(first, second);
        Assert.Empty(atlas.DirtyRegions.ToArray());
        Assert.Equal(pixels, page.Pixels.ToArray());
    }

    private static Font LoadFont(string name) =>
        Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name)));

    private static byte[] ReadPixels(GlyphAtlas atlas, AtlasDirtyRegion region)
    {
        AtlasPage page = atlas.Pages[region.PageIndex];
        byte[] pixels = new byte[region.Width * region.Height];
        for (int row = 0; row < region.Height; row++)
            page.Pixels.Slice((region.Y + row) * page.Width + region.X, region.Width)
                .CopyTo(pixels.AsSpan(row * region.Width, region.Width));
        return pixels;
    }
}
