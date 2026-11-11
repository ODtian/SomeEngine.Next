using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.FlexFunctions;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Consumer.Tests;

[Collection("Text allocations")]
public sealed partial class TextTests
{
    [Fact]
    public void ScopeAndWorldWritesKeepTextParametersAndDisplayedContentTogether() => TextChecks.VerifyTextSources();

    [Fact]
    public void RepeatedNumericDeclarationsAllocateTheSameBytesAsEqualStringDeclarations() =>
        TextChecks.VerifyRepeatedDeclarations();

    [Theory]
    [InlineData("decimal")]
    [InlineData("offset")]
    [InlineData("kind")]
    [InlineData("double")]
    [InlineData("float")]
    [InlineData("half")]
    public void ChangedFormattingDataKeepsParametersAndDisplayTogether(string example) =>
        TextChecks.VerifyChangedFormattingData(example);

    [Fact]
    public void ChangingTextColorKeepsTheShapeAndLayout()
    {
        using Font font = LoadFont();
        using var frames = new TextFrames();
        Entity canvas = frames.World.Create(new Canvas { Size = new Size(320, 240) });
        frames.World.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 20 }));
        Entity node = Views.Views.Spawn(frames.World, canvas, new Text("AV"));
        frames.Frame();
        ShapedText before = frames.World.ReadComponentData<ShapedText>(node);
        var glyphs = before.Shape.Glyphs.ToArray();
        ulong rectangleVersion = frames.World.ComponentDataWriteVersion<LayoutRect>(node);

        frames.World.Add(node, new TextStyle { Color = new Color(0.2f, 0.4f, 0.6f) });
        frames.Frame();

        ShapedText after = frames.World.ReadComponentData<ShapedText>(node);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(glyphs, after.Shape.Glyphs.ToArray());
        Assert.Equal(before.Size, after.Size);
        Assert.Equal(rectangleVersion, frames.World.ComponentDataWriteVersion<LayoutRect>(node));
        Assert.Equal(new Color(0.2f, 0.4f, 0.6f), after.Style.Color);
    }

    [Fact]
    public void SharedTextBehaviorChecksPass() => TextChecks.Run();

    [Fact]
    public void ColumnPlacesTextAfterThePreviousTextsMeasuredHeight()
    {
        using Font font = LoadFont();
        using var frames = new TextFrames();
        Entity canvas = frames.World.Create(new Canvas { Size = new Size(320, 240) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new TextColumn(font, "AV\nAV"));

        frames.Frame();

        Entity column = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity[] children = ElementChildren.Read(frames.World, column);
        ShapedText first = frames.World.ReadComponentData<ShapedText>(children[0]);
        ShapedText second = frames.World.ReadComponentData<ShapedText>(children[1]);
        Assert.Equal(2, first.Shape.LineCount);
        Assert.Equal(23.98f, first.Size.Width, 3);
        Assert.Equal(54.48f, first.Size.Height, 3);
        Assert.Equal(51.90f, second.Size.Width, 3);
        Assert.Equal(27.24f, second.Size.Height, 3);
        Assert.Equal(0, frames.World.ReadComponentData<LayoutRect>(children[0]).Value.Y);
        Assert.Equal(59.48f, frames.World.ReadComponentData<LayoutRect>(children[1]).Value.Y, 3);
        Assert.Equal(54.48f, frames.World.ReadComponentData<LayoutRect>(children[0]).Value.Height, 3);
    }

    [Fact]
    public void TextChangesUpdateShapingAndLayoutWhileEqualTextPreservesVersions()
    {
        using Font font = LoadFont();
        using var frames = new TextFrames();
        Entity canvas = frames.World.Create(new Canvas { Size = new Size(320, 240) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new TextColumn(font, "AV"));
        frames.Frame();
        Entity column = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity[] children = ElementChildren.Read(frames.World, column);
        ulong textVersion = frames.World.ComponentDataWriteVersion<Text>(children[0]);
        ulong shapeVersion = frames.World.ComponentDataWriteVersion<ShapedText>(children[0]);
        uint resultVersion = frames.World.ReadComponentData<ShapedText>(children[0]).Version;
        ulong layoutVersion = frames.World.ComponentDataWriteVersion<LayoutRect>(children[1]);

        frames.World.Set(root, new TextColumn(font, "AV"));
        frames.Frame();

        Assert.Equal(textVersion, frames.World.ComponentDataWriteVersion<Text>(children[0]));
        Assert.Equal(shapeVersion, frames.World.ComponentDataWriteVersion<ShapedText>(children[0]));
        Assert.Equal(resultVersion, frames.World.ReadComponentData<ShapedText>(children[0]).Version);
        Assert.Equal(layoutVersion, frames.World.ComponentDataWriteVersion<LayoutRect>(children[1]));

        frames.World.Set(root, new TextColumn(font, "AV\nAV"));
        frames.Frame();

        Assert.NotEqual(shapeVersion, frames.World.ComponentDataWriteVersion<ShapedText>(children[0]));
        Assert.NotEqual(resultVersion, frames.World.ReadComponentData<ShapedText>(children[0]).Version);
        Assert.NotEqual(layoutVersion, frames.World.ComponentDataWriteVersion<LayoutRect>(children[1]));
        Assert.Equal(2, frames.World.ReadComponentData<ShapedText>(children[0]).Shape.LineCount);
        Assert.Equal(59.48f, frames.World.ReadComponentData<LayoutRect>(children[1]).Value.Y, 3);
    }

    private static Font LoadFont() => Font.Load(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));

    [Fact]
    public void RepeatedFormattedNumberWritesPreserveTextAndAllocateZeroBytes()
    {
        using Font font = LoadFont();
        using var frames = new TextFrames();
        Entity canvas = frames.World.Create(new Canvas { Size = new Size(320, 240) });
        frames.World.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 20 }));
        Entity root = Views.Views.Spawn(frames.World, canvas, new NumberLabel(42, "D4"));
        frames.Frame();
        Entity node = Assert.Single(ElementChildren.Read(frames.World, root));
        Assert.Equal("0042", frames.World.ReadComponentData<Text>(node).Value);
        ulong version = frames.World.ComponentDataWriteVersion<Text>(node);
        for (int index = 0; index < 1000; index++)
            node.SetText(frames.World, 42, "D4");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++)
            node.SetText(frames.World, 42, "D4");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(version, frames.World.ComponentDataWriteVersion<Text>(node));
        frames.World.Set(root, new NumberLabel(42, "D4"));
        frames.Frame();
        Assert.Equal(node, Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.Equal(version, frames.World.ComponentDataWriteVersion<Text>(node));
        node.SetText(frames.World, 43, "D4");
        frames.Frame();
        Assert.Equal("0043", frames.World.ReadComponentData<Text>(node).Value);
        Assert.NotEqual(version, frames.World.ComponentDataWriteVersion<Text>(node));
    }

    private readonly record struct NumberLabel(int Value, string Format) : IView, ITrackedComponent
    {
        public BuildTask Build()
        {
            Text(Value, Format);
            return default;
        }
    }

    [Fact]
    public void TextModifiersUseThemeDefaultsAndUpdateLineHeightAndColor()
    {
        using Font font = LoadFont();
        using Font otherFont = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Cjk.otf")));
        using var frames = new TextFrames();
        Entity canvas = frames.World.Create(new Canvas { Size = new Size(320, 240) });
        frames.World.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 20 }));
        var color = new Color(0.2f, 0.4f, 0.6f, 0.8f);
        Entity root = Views.Views.Spawn(frames.World, canvas, new StyledLabel(10, 24, color));
        frames.Frame();
        Entity node = Assert.Single(ElementChildren.Read(frames.World, root));
        ShapedText shape = frames.World.ReadComponentData<ShapedText>(node);
        Assert.Equal(11.99f, shape.Size.Width, 3);
        Assert.Equal(48, shape.Size.Height);
        Assert.Equal(24, shape.Shape.Lines[1].Baseline - shape.Shape.Lines[0].Baseline, 3);
        Assert.Equal(color, shape.Style.Color);
        Assert.Same(font, shape.Style.Font);
        ulong version = frames.World.ComponentDataWriteVersion<ShapedText>(node);

        frames.World.Set(root, new StyledLabel(20, 32, new Color(1, 0, 0)));
        frames.Frame();
        Assert.Equal(23.98f, frames.World.ReadComponentData<ShapedText>(node).Size.Width, 3);
        Assert.Equal(64, frames.World.ReadComponentData<ShapedText>(node).Size.Height);
        Assert.Equal(new Color(1, 0, 0), frames.World.ReadComponentData<ShapedText>(node).Style.Color);
        Assert.NotEqual(version, frames.World.ComponentDataWriteVersion<ShapedText>(node));

        frames.World.Set(canvas, new TextTheme(new TextStyle { Font = otherFont, Size = 30 }));
        frames.Frame();
        Assert.Same(otherFont, frames.World.ReadComponentData<ShapedText>(node).Style.Font);
        Assert.Equal(20, frames.World.ReadComponentData<ShapedText>(node).Style.Size);
        Assert.Equal(64, frames.World.ReadComponentData<ShapedText>(node).Size.Height);
    }

    private readonly record struct StyledLabel(float Size, float Height, Color Color) : IView, IComponent
    {
        public BuildTask Build()
        {
            Text("AV\nAV").FontSize(Size).TextColor(Color).LineHeight(Height);
            return default;
        }
    }

    [Fact]
    public void RowWrapsTextAtItsFinalWidthAndRetainsVersionsAcrossRepeatedMeasurements()
    {
        using Font font = LoadFont();
        using var frames = new TextFrames();
        Entity canvas = frames.World.Create(new Canvas { Size = new Size(64, 240) });
        frames.World.Add(canvas, new TextTheme(new TextStyle { Font = font, Size = 20 }));
        Entity root = Views.Views.Spawn(frames.World, canvas, new TextRow());
        frames.Frame();
        Entity row = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity text = ElementChildren.Read(frames.World, row)[0];
        Assert.Equal(24, frames.World.ReadComponentData<LayoutRect>(text).Value.Width, 3);
        Assert.Equal(2, frames.World.ReadComponentData<ShapedText>(text).Shape.LineCount);
        Assert.Equal(54.48f, frames.World.ReadComponentData<LayoutRect>(text).Value.Height, 3);
        ulong version = frames.World.ComponentDataWriteVersion<ShapedText>(text);
        uint resultVersion = frames.World.ReadComponentData<ShapedText>(text).Version;
        ulong layoutVersion = frames.World.ComponentDataWriteVersion<LayoutRect>(text);

        frames.Frame();

        Assert.Equal(version, frames.World.ComponentDataWriteVersion<ShapedText>(text));
        Assert.Equal(resultVersion, frames.World.ReadComponentData<ShapedText>(text).Version);
        Assert.Equal(layoutVersion, frames.World.ComponentDataWriteVersion<LayoutRect>(text));

        frames.World.Set(canvas, new Canvas { Size = new Size(88, 240) });
        frames.Frame();

        Assert.Equal(1, frames.World.ReadComponentData<ShapedText>(text).Shape.LineCount);
        Assert.Equal(27.24f, frames.World.ReadComponentData<LayoutRect>(text).Value.Height, 3);
        Assert.NotEqual(version, frames.World.ComponentDataWriteVersion<ShapedText>(text));
    }

    private readonly record struct TextRow : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Row().Align(Align.Start, Align.Start).Content())
            {
                Text("AVAV");
                Scope.View(new Absolute(), 1).Size(40, 20).Shrink(0);
            }
            return default;
        }
    }

    [Fact]
    public void LargeNumericFormatsReuseCharactersAndPreserveTextVersions()
    {
        using var world = new World();
        Entity node = world.Create(new Text(""));
        node.SetText(world, 42, "D700");
        string text = world.ReadComponentData<Text>(node).Value;
        Assert.Equal(700, text.Length);
        Assert.Equal(new string('0', 698) + "42", text);
        ulong version = world.ComponentDataWriteVersion<Text>(node);
        for (int index = 0; index < 1000; index++)
            node.SetText(world, 42, "D700");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
            node.SetText(world, 42, "D700");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(version, world.ComponentDataWriteVersion<Text>(node));
        node.SetText(world, 42, "D4");
        Assert.Equal("0042", world.ReadComponentData<Text>(node).Value);
        Assert.NotEqual(version, world.ComponentDataWriteVersion<Text>(node));
    }

#if DEBUG
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidFontSizesProduceABuildError(float size)
    {
        using var frames = new TextFrames();
        Entity root = Views.Views.Spawn(frames.World, Entity.Null, new StyledLabel(size, 0, default));

        frames.Frame();

        Assert.IsType<InvalidOperationException>(frames.World.ReadComponentData<BuildError>(root).Exception);
    }

    [Fact]
    public void TextMeasurementReportsTheRequiredShapingSystem()
    {
        using Font font = LoadFont();
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity canvas = world.Create(new Canvas { Size = new Size(320, 240) });
        world.Add(canvas, new TextTheme(new TextStyle { Font = font }));
        Views.Views.Spawn(world, canvas, new Text("AV"));

        Assert.Throws<InvalidOperationException>(() => systems.Update());
    }
#endif

    private readonly record struct TextColumn(Font Font, string First) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Column(5).Align(Align.Start, Align.Start)
                .Provide(new TextTheme(new TextStyle { Font = Font, Size = 20, Color = new Color(1, 1, 1) }))
                .Content())
            {
                Text(First);
                Text("office");
            }
            return default;
        }
    }
}

[CollectionDefinition("Text allocations", DisableParallelization = true)]
public sealed class TextAllocationCollection { }

internal sealed class TextFrames : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    public World World { get; } = new();

    public TextFrames()
    {
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        var shaping = new TextShapingSystem();
        _systems.Add(new ViewRebuildSystem());
        _systems.Add(shaping);
        _systems.Add(new LayoutSystem(shaping));
    }

    public void Frame() => _systems.Update();

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
    }
}
