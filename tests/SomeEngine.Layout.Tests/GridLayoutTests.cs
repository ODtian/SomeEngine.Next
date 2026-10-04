using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Layout.Tests;

public sealed partial class GridLayoutTests
{
    [Fact]
    public void FractionCellSpansCoverWeightedTracksAndTheirInternalGaps()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(420, 310) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridContent(new Grid(
            [GridTrack.Fraction(), GridTrack.Fraction(2)],
            [GridTrack.Fraction(), GridTrack.Fraction(2), GridTrack.Fraction()], Gap: 10),
            [new Item(default, Column: 1, RowSpan: 2, ColumnSpan: 2), new Item(default)]));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();

        Assert.Equal(new Rect(0, 0, 420, 310), frames.World.Read<LayoutRect>(grid).Value);
        Assert.Equal(new Rect(110, 0, 310, 310), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(0, 0, 100, 100), frames.World.Read<LayoutRect>(cells[1]).Value);
    }

    [Fact]
    public void FractionTrackUsesZeroLengthWhenFixedTracksAndGapConsumeTheAvailableSpace()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(100, 40) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridContent(new Grid(
            [GridTrack.Fixed(30)], [GridTrack.Fixed(80), GridTrack.Fraction()], Gap: 20),
            [new Item(default), new Item(new Size(20, 10), Column: 1)]));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();

        Assert.Equal(new Rect(0, 0, 100, 30), frames.World.Read<LayoutRect>(grid).Value);
        Assert.Equal(new Rect(0, 0, 80, 30), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(100, 0, 0, 30), frames.World.Read<LayoutRect>(cells[1]).Value);
    }

    [Fact]
    public void GridTracksCopiesDefinitionsAndComparesGridParametersByValue()
    {
        GridTrack[] source = [GridTrack.Fixed(40), GridTrack.Auto, GridTrack.Fraction(2)];
        var tracks = new GridTracks(source);
        source[0] = GridTrack.Fixed(80);
        GridTracks expected = [GridTrack.Fixed(40), GridTrack.Auto, GridTrack.Fraction(2)];

        Assert.Equal(expected, tracks);
        Assert.Equal(new Grid(expected, expected), new Grid(tracks, tracks));
        Assert.Equal(expected.GetHashCode(), tracks.GetHashCode());
        Assert.False(tracks.Equals(new GridTracks(source)));
    }

    [Fact]
    public void TrackGapAndCellChangesUpdateTheExistingChildrenInTheSameFrame()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(320, 200) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridContent(new Grid(
            [GridTrack.Fixed(40), GridTrack.Fraction()], [GridTrack.Fixed(60), GridTrack.Fraction()], Gap: 10),
            [new Item(default), new Item(default, Row: 1, Column: 1)]));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();
        Assert.Equal(new Rect(0, 0, 60, 40), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(70, 50, 250, 150), frames.World.Read<LayoutRect>(cells[1]).Value);

        Grid changed = frames.World.Read<Grid>(grid);
        changed.Columns = [GridTrack.Fixed(80), GridTrack.Fraction(2)];
        changed.Gap = 20;
        frames.World.Replace(grid, changed);
        frames.World.Replace(cells[0], new GridCell { Row = 1, Column = 1 });
        frames.World.Replace(cells[1], new GridCell { Row = 1 });
        frames.Frame();

        Assert.Equal(new Rect(100, 60, 220, 140), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(0, 60, 80, 140), frames.World.Read<LayoutRect>(cells[1]).Value);
    }

#if DEBUG
    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidGapIsReportedByDevelopmentChecks(float gap)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(100, 100) });
        Views.Views.Spawn(frames.World, canvas, new Grid([GridTrack.Fixed(50)], [GridTrack.Fixed(50)], gap));

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);
        Assert.Contains("Grid.Gap", error.Message);
    }

    [Theory]
    [InlineData(GridTrackKind.Fixed, -1)]
    [InlineData(GridTrackKind.Fixed, float.NaN)]
    [InlineData(GridTrackKind.Fixed, float.PositiveInfinity)]
    [InlineData(GridTrackKind.Fraction, 0)]
    [InlineData(GridTrackKind.Fraction, -1)]
    [InlineData((GridTrackKind)99, 1)]
    public void InvalidTrackDefinitionsAreReportedByDevelopmentChecks(GridTrackKind kind, float value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new GridTracks(new GridTrack(kind, value)));
        Assert.Contains("GridTrack", error.Message);
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, 1, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 2)]
    [InlineData(0, 0, int.MaxValue, 1)]
    public void InvalidCellsAreReportedByDevelopmentChecks(int row, int column, int rowSpan, int columnSpan)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(100, 100) });
        Views.Views.Spawn(frames.World, canvas, new GridContent(new Grid(
            [GridTrack.Fixed(50)], [GridTrack.Fixed(50)]),
            [new Item(default, row, column, rowSpan, columnSpan)]));

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);
        Assert.Contains("GridCell", error.Message);
    }
#endif

    [Theory]
    [InlineData(Axis.Horizontal)]
    [InlineData(Axis.Vertical)]
    public void FlexMeasuresGridFractionContentThenGrowsAndStretchesTheGrid(Axis direction)
    {
        using var frames = new LayoutFrames();
        bool horizontal = direction == Axis.Horizontal;
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = horizontal ? new Size(440, 200) : new Size(200, 440) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridInFlex(direction));
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] items = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, items[0]).ToArray();

        Assert.Equal(horizontal ? new Rect(10, 10, 310, 180) : new Rect(10, 10, 180, 310), frames.World.Read<LayoutRect>(items[0]).Value);
        Assert.Equal(horizontal ? new Rect(10, 10, 100, 30) : new Rect(10, 10, 30, 100), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(horizontal ? new Rect(120, 10, 200, 30) : new Rect(10, 120, 30, 200), frames.World.Read<LayoutRect>(cells[1]).Value);
        Assert.Equal(horizontal ? new Rect(330, 10, 100, 40) : new Rect(10, 330, 40, 100), frames.World.Read<LayoutRect>(items[1]).Value);
    }

    [Fact]
    public void FixedCellSpansIncludeInternalGapsAndRespectChildMaximumSize()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(500, 400) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridContent(new Grid(
            [GridTrack.Fixed(40), GridTrack.Fixed(50), GridTrack.Fixed(60)],
            [GridTrack.Fixed(50), GridTrack.Fixed(70), GridTrack.Fixed(80)], Gap: 10),
            [new Item(default, Column: 1, RowSpan: 2, ColumnSpan: 2),
             new Item(default, Row: 2, ColumnSpan: 3, Maximum: new Size(120, 30))]));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();

        Assert.Equal(new Rect(0, 0, 220, 170), frames.World.Read<LayoutRect>(grid).Value);
        Assert.Equal(new Rect(60, 0, 160, 100), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(0, 110, 120, 30), frames.World.Read<LayoutRect>(cells[1]).Value);
    }

    [Fact]
    public void SpanningContentAddsItsExtraSizeToAutoTracksAfterSingleCells()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(500, 400) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridContent(new Grid(
            [GridTrack.Auto, GridTrack.Auto], [GridTrack.Auto, GridTrack.Auto], Gap: 10),
            [new Item(new Size(150, 120), RowSpan: 2, ColumnSpan: 2),
             new Item(new Size(40, 20)), new Item(new Size(60, 40), Row: 1, Column: 1)]));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();

        Assert.Equal(new Rect(0, 0, 150, 120), frames.World.Read<LayoutRect>(grid).Value);
        Assert.Equal(new Rect(0, 0, 150, 120), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(0, 0, 60, 45), frames.World.Read<LayoutRect>(cells[1]).Value);
        Assert.Equal(new Rect(70, 55, 80, 65), frames.World.Read<LayoutRect>(cells[2]).Value);
    }

    [Fact]
    public void GridMeasuresWrappedFlexAtItsColumnWidthAndDistributesItsFractionRow()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(500, 300) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexInGrid());
        frames.Frame();
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();
        Entity[] wrapped = Hierarchy<ElementTree>.GetChildren(frames.World, cells[0]).ToArray();
        Entity[] grown = Hierarchy<ElementTree>.GetChildren(frames.World, cells[1]).ToArray();

        Assert.Equal(new Rect(10, 10, 200, 50), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(10, 10, 90, 20), frames.World.Read<LayoutRect>(wrapped[0]).Value);
        Assert.Equal(new Rect(110, 10, 90, 20), frames.World.Read<LayoutRect>(wrapped[1]).Value);
        Assert.Equal(new Rect(10, 40, 90, 20), frames.World.Read<LayoutRect>(wrapped[2]).Value);
        Assert.Equal(new Rect(220, 70, 270, 220), frames.World.Read<LayoutRect>(cells[1]).Value);
        Assert.Equal(new Rect(220, 70, 270, 105), frames.World.Read<LayoutRect>(grown[0]).Value);
        Assert.Equal(new Rect(220, 185, 270, 105), frames.World.Read<LayoutRect>(grown[1]).Value);
    }

    [Fact]
    public void FractionTracksShareSpaceAfterFixedAutoAndGapOnBothAxes()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(540, 400) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridContent(new Grid(
            [GridTrack.Fixed(40), GridTrack.Auto, GridTrack.Fraction(), GridTrack.Fraction(2)],
            [GridTrack.Fixed(100), GridTrack.Auto, GridTrack.Fraction(), GridTrack.Fraction(2)], Gap: 10),
            [new Item(new Size(20, 20)), new Item(new Size(80, 30), Row: 1, Column: 1),
             new Item(new Size(10, 10), Row: 2, Column: 2), new Item(new Size(10, 10), Row: 3, Column: 3)]));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();

        Assert.Equal(new Rect(0, 0, 540, 400), frames.World.Read<LayoutRect>(grid).Value);
        Assert.Equal(new Rect(0, 0, 100, 40), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(110, 50, 80, 30), frames.World.Read<LayoutRect>(cells[1]).Value);
        Assert.Equal(new Rect(200, 90, 110, 100), frames.World.Read<LayoutRect>(cells[2]).Value);
        Assert.Equal(new Rect(320, 200, 220, 200), frames.World.Read<LayoutRect>(cells[3]).Value);
    }

    [Fact]
    public void AutoTracksUseTheLargestChildOuterSizeInEachRowAndColumn()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(500, 400) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GridContent(
            new Grid([GridTrack.Auto, GridTrack.Auto], [GridTrack.Auto, GridTrack.Auto], Gap: 10),
            [new Item(new Size(30, 20), Padding: 5), new Item(new Size(80, 10), Column: 1),
             new Item(new Size(50, 40), Row: 1)]));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();

        Assert.Equal(new Rect(0, 0, 140, 80), frames.World.Read<LayoutRect>(grid).Value);
        Assert.Equal(new Rect(0, 0, 50, 30), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(60, 0, 80, 30), frames.World.Read<LayoutRect>(cells[1]).Value);
        Assert.Equal(new Rect(0, 40, 50, 40), frames.World.Read<LayoutRect>(cells[2]).Value);
    }

    [Fact]
    public void FixedTracksPlaceCellsWithGapPaddingAndCallerWrittenDimensions()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(500, 400) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FixedCells());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity grid = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] cells = Hierarchy<ElementTree>.GetChildren(frames.World, grid).ToArray();

        Assert.Equal(new Rect(20, 30, 270, 120), frames.World.Read<LayoutRect>(grid).Value);
        Assert.Equal(new Rect(25, 35, 100, 40), frames.World.Read<LayoutRect>(cells[0]).Value);
        Assert.Equal(new Rect(135, 85, 150, 60), frames.World.Read<LayoutRect>(cells[1]).Value);
        Assert.Equal(new Rect(25, 85, 30, 20), frames.World.Read<LayoutRect>(cells[2]).Value);
    }

    private readonly record struct FixedCells : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            using (Scope.View(new Grid(new GridTracks(GridTrack.Fixed(40), GridTrack.Fixed(60)),
                       new GridTracks(GridTrack.Fixed(100), GridTrack.Fixed(150)), Gap: 10), 2)
                       .Padding(5).Position(20, 30).Content())
            {
                Scope.View(new Absolute(), 3);
                Scope.View(new Absolute(), 4).Cell(1, 1);
                Scope.View(new Absolute(), 5).Size(30, 20).Cell(1, 0);
            }
            return default;
        }
    }

    private readonly record struct Item(Size Natural, int Row = 0, int Column = 0,
        int RowSpan = 1, int ColumnSpan = 1, float Padding = 0, Size? Fixed = null, Size? Maximum = null);

    private readonly record struct GridContent(Grid Layout, Item[] Items, Size? Fixed = null) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            {
                Entity grid = Scope.View(Layout, 2);
                if (Fixed is { } size)
                    grid.Size(size.Width, size.Height);
                using (grid.Content())
                foreach (Item item in Items)
                {
                    Entity child = Scope.View(new NaturalSize(item.Natural), 3)
                        .Cell(item.Row, item.Column, item.RowSpan, item.ColumnSpan).Padding(item.Padding);
                    if (item.Fixed is { } fixedSize)
                        child.Size(fixedSize.Width, fixedSize.Height);
                    if (item.Maximum is { } maximum)
                        child.MaxSize(maximum.Width, maximum.Height);
                }
            }
            return default;
        }
    }

    private readonly record struct GridInFlex(Axis Direction) : IView, IComponent
    {
        public BuildTask Build()
        {
            bool horizontal = Direction == Axis.Horizontal;
            GridTracks fractions = [GridTrack.Fraction(), GridTrack.Fraction(2)];
            GridTracks automatic = [GridTrack.Auto];
            using (Scope.View(new Flex(Direction, Gap: 10), 1).Padding(10).Content())
            {
                using (Scope.View(new Grid(horizontal ? automatic : fractions, horizontal ? fractions : automatic, Gap: 10), 2)
                    .Grow().Content())
                {
                    Scope.View(new NaturalSize(horizontal ? new Size(40, 20) : new Size(20, 40)), 3);
                    Scope.View(new NaturalSize(horizontal ? new Size(50, 30) : new Size(30, 50)), 4)
                        .Cell(horizontal ? 0 : 1, horizontal ? 1 : 0);
                }
                Scope.View(new Absolute(), 5).Size(horizontal ? 100 : 40, horizontal ? 40 : 100);
            }
            return default;
        }
    }

    private readonly record struct FlexInGrid : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Grid([GridTrack.Auto, GridTrack.Fraction()],
                [GridTrack.Fraction(), GridTrack.Fixed(270)], Gap: 10), 1).Padding(10).Content())
            {
                using (Scope.View(new Flex(Axis.Horizontal, Gap: 10, Wrap: true), 2).Content())
                {
                    Scope.View(new Absolute(), 3).Size(90, 20);
                    Scope.View(new Absolute(), 4).Size(90, 20);
                    Scope.View(new Absolute(), 5).Size(90, 20);
                }
                using (Scope.View(new Flex(Axis.Vertical, Gap: 10), 6).Cell(1, 1).Content())
                {
                    Scope.View(new Absolute(), 7).Size(float.NaN, 0).Grow();
                    Scope.View(new Absolute(), 8).Size(float.NaN, 0).Grow();
                }
            }
            return default;
        }
    }

    private readonly record struct NaturalSize(Size Value) : ILayout, IComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) => Value;
    }
}
