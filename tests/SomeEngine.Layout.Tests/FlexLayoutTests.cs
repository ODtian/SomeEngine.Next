using static SomeEngine.Layout.FlexFunctions;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Layout.Tests;

public sealed partial class FlexLayoutTests
{
    [Fact]
    public void ExplicitBasisProvidesFiniteConstraintsToAUserLayout()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new BasisFillRow());
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();

        Assert.Equal(new Rect(0, 0, 300, 100), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(300, 0, 100, 50), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Fact]
    public void ShrinkingAChildUpdatesItsContentHeightAndTheAutomaticRowHeight()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new ReflowRow());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();

        Assert.Equal(new Rect(0, 0, 150, 40), frames.World.Read<LayoutRect>(row).Value);
        Assert.Equal(new Rect(0, 0, 75, 40), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(75, 0, 75, 20), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Theory]
    [InlineData(false, 200, 200)]
    [InlineData(true, 250, 150)]
    public void AutomaticBasisUsesContentWhileZeroBasisStartsAtZero(bool zeroBasis, float firstWidth, float secondWidth)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(Axis.Horizontal),
            [new Item(Size.Auto, Grow: 1, Natural: new Size(100, 40)),
             new Item(Size.Auto, Grow: 1, Natural: new Size(100, 40), Basis: zeroBasis ? 0 : float.NaN)]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, flex).ToArray();

        Assert.Equal(new Rect(0, 0, firstWidth, 100), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(firstWidth, 0, secondWidth, 100), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Fact]
    public void SharedFlexExamplesUseThePublicProtocol() => Views.AotSmoke.FlexChecks.Run();

    [Fact]
    public void WrappedLinesGrowAndStretchWithinEachLine()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(210, 300) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(Axis.Horizontal, 10, Wrap: true),
            [new Item(new Size(100, float.NaN), Grow: 1, Natural: new Size(100, 40)),
             new Item(new Size(100, float.NaN), Grow: 1, Natural: new Size(100, 60)),
             new Item(new Size(100, float.NaN), Grow: 1, Natural: new Size(100, 30))]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, flex).ToArray();

        Assert.Equal(new Rect(0, 0, 100, 60), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(110, 0, 100, 60), frames.World.Read<LayoutRect>(children[1]).Value);
        Assert.Equal(new Rect(0, 70, 210, 30), frames.World.Read<LayoutRect>(children[2]).Value);
    }

    [Fact]
    public void UpdatedModifiersChangeLayoutInTheSameFrame()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new ExpandedRow(false));
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();
        Assert.Equal(new Rect(0, 0, 100, 50), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(110, 0, 290, 50), frames.World.Read<LayoutRect>(children[1]).Value);

        frames.World.Replace(root, new ExpandedRow(true));
        frames.Frame();
        row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();
        Assert.Equal(new Rect(0, 0, 240, 50), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(260, 0, 140, 50), frames.World.Read<LayoutRect>(children[1]).Value);
    }

#if DEBUG
    [Fact]
    public void DevelopmentBuildReportsANegativeGap()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 100) });
        Views.Views.Spawn(frames.World, canvas, new Flex(Axis.Horizontal, Gap: -1));

        var error = Assert.Throws<InvalidOperationException>(() => frames.Frame());
        Assert.Contains("Flex.Gap", error.Message);
    }
#endif

    [Theory]
    [InlineData(float.NaN, 0, 40, 0, 40)]
    [InlineData(100, 16, 100, 30, 68)]
    public void DividerStretchesToTheRowsContentHeight(float height, float padding, float rowHeight, float buttonY, float dividerHeight)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new DividerRow(height, padding));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();

        Assert.Equal(new Rect(0, 0, 400, rowHeight), frames.World.Read<LayoutRect>(row).Value);
        Assert.Equal(new Rect(padding, buttonY, 100, 40), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(padding + 110, padding, 4, dividerHeight), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Theory]
    [InlineData(true, 400, 100, 150, 250)]
    [InlineData(false, 200, 150, 120, 80)]
    public void FlexRedistributesSpaceAfterAChildReachesItsBoxLimit(bool grow, float width, float basis,
        float firstWidth, float secondWidth)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(width, 100) });
        Item first = grow
            ? new Item(new Size(basis, 50), Grow: 1, Maximum: new Size(150, 100))
            : new Item(new Size(basis, 50), Minimum: new Size(120, 0));
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(Axis.Horizontal),
            [first, new Item(new Size(basis, 50), Grow: grow ? 1 : 0)]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, flex).ToArray();

        Assert.Equal(new Rect(0, 0, firstWidth, 50), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(firstWidth, 0, secondWidth, 50), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Fact]
    public void NestedRowColumnUsesFinalGrowthAndStretchForItsChildren()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 200) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new NestedRows());
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();
        Entity column = children[1];
        Entity[] leaves = Hierarchy<ElementTree>.GetChildren(frames.World, column).ToArray();

        Assert.Equal(new Rect(10, 10, 80, 40), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(100, 10, 290, 180), frames.World.Read<LayoutRect>(column).Value);
        Assert.Equal(new Rect(100, 10, 290, 30), frames.World.Read<LayoutRect>(leaves[0]).Value);
        Assert.Equal(new Rect(100, 45, 290, 30), frames.World.Read<LayoutRect>(leaves[1]).Value);
    }

    [Theory]
    [InlineData(Axis.Horizontal, 0, 200, 40)]
    [InlineData(Axis.Horizontal, 10, 210, 50)]
    [InlineData(Axis.Vertical, 10, 210, 50)]
    public void WrapStartsAnotherLineAfterTheAvailableMainSize(Axis axis, float gap, float mainSize, float secondLine)
    {
        using var frames = new LayoutFrames();
        Size canvasSize = axis == Axis.Horizontal ? new Size(mainSize, 300) : new Size(300, mainSize);
        Size childSize = axis == Axis.Horizontal ? new Size(100, 40) : new Size(40, 100);
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = canvasSize });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(axis, gap, Wrap: true),
            [new Item(childSize, Shrink: 0), new Item(childSize, Shrink: 0), new Item(childSize, Shrink: 0)]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, flex).ToArray();

        Rect first = axis == Axis.Horizontal ? new Rect(0, 0, 100, 40) : new Rect(0, 0, 40, 100);
        Rect second = axis == Axis.Horizontal ? new Rect(100 + gap, 0, 100, 40) : new Rect(0, 100 + gap, 40, 100);
        Rect third = axis == Axis.Horizontal ? new Rect(0, secondLine, 100, 40) : new Rect(secondLine, 0, 40, 100);
        Assert.Equal(first, frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(second, frames.World.Read<LayoutRect>(children[1]).Value);
        Assert.Equal(third, frames.World.Read<LayoutRect>(children[2]).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    public void BasisSetsTheMainSizeBeforeFlexDistribution(float basis)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(Axis.Horizontal),
            [new Item(new Size(100, 50), Basis: basis)]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, flex));

        Assert.Equal(new Rect(0, 0, basis, 50), frames.World.Read<LayoutRect>(child).Value);
    }

    [Theory]
    [InlineData(Align.Start, 0, 70)]
    [InlineData(Align.Center, 140, 210)]
    [InlineData(Align.End, 280, 350)]
    [InlineData(Align.SpaceBetween, 0, 350)]
    public void ContainerModifiersDistributeTheMainAxisAndKeepTheGap(Align main, float firstX, float secondX)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new AlignedRow(main));
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();

        Assert.Equal(new Rect(firstX, 30, 50, 40), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(secondX, 30, 50, 40), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Theory]
    [InlineData(Align.Center, Align.Stretch, 0, 200)]
    [InlineData(Align.Stretch, Align.Center, 80, 40)]
    public void AlignSelfChangesTheChildsCrossAlignment(Align container, Align self, float offset, float height)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 200) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(Axis.Horizontal, CrossAlign: container),
            [new Item(new Size(100, float.NaN), Natural: new Size(100, 40), Self: self)]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, flex));

        Assert.Equal(new Rect(0, offset, 100, height), frames.World.Read<LayoutRect>(child).Value);
    }

    [Theory]
    [InlineData(Axis.Horizontal, Align.Start, 0, 40)]
    [InlineData(Axis.Horizontal, Align.Center, 80, 40)]
    [InlineData(Axis.Horizontal, Align.End, 160, 40)]
    [InlineData(Axis.Horizontal, Align.Stretch, 0, 200)]
    [InlineData(Axis.Vertical, Align.Stretch, 0, 200)]
    public void CrossAlignmentPlacesOrStretchesAnAutomaticDimension(Axis axis, Align align, float offset, float crossSize)
    {
        using var frames = new LayoutFrames();
        Size canvasSize = axis == Axis.Horizontal ? new Size(400, 200) : new Size(200, 400);
        Size fixedSize = axis == Axis.Horizontal ? new Size(100, float.NaN) : new Size(float.NaN, 100);
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = canvasSize });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(axis, CrossAlign: align),
            [new Item(fixedSize, Natural: new Size(40, 40))]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, flex));

        Rect expected = axis == Axis.Horizontal ? new Rect(0, offset, 100, crossSize) : new Rect(offset, 0, crossSize, 100);
        Assert.Equal(expected, frames.World.Read<LayoutRect>(child).Value);
    }

    [Theory]
    [InlineData(200, 150, 150, 1, 1, 100, 100)]
    [InlineData(200, 150, 150, 0, 1, 150, 50)]
    [InlineData(100, 50, 250, 1, 1, 0, 100)]
    public void ShrinkSharesOverflowAndRedistributesAfterAChildReachesZero(float width, float firstBasis,
        float secondBasis, float firstShrink, float secondShrink, float firstWidth, float secondWidth)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(width, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(Axis.Horizontal),
            [new Item(new Size(firstBasis, 50), Shrink: firstShrink), new Item(new Size(secondBasis, 50), Shrink: secondShrink)]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, flex).ToArray();

        Assert.Equal(new Rect(0, 0, firstWidth, 50), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(firstWidth, 0, secondWidth, 50), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Theory]
    [InlineData(Axis.Horizontal, 400, 100, 1, 1, 200, 200)]
    [InlineData(Axis.Horizontal, 300, 0, 1, 2, 100, 200)]
    [InlineData(Axis.Vertical, 400, 0, 1, 1, 200, 200)]
    public void GrowSharesRemainingSpaceAlongEitherAxis(Axis axis, float available, float basis,
        float firstGrow, float secondGrow, float firstSize, float secondSize)
    {
        using var frames = new LayoutFrames();
        Size canvasSize = axis == Axis.Horizontal ? new Size(available, 100) : new Size(100, available);
        Size childSize = axis == Axis.Horizontal ? new Size(basis, 50) : new Size(80, basis);
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = canvasSize });
        Entity root = Views.Views.Spawn(frames.World, canvas, new FlexContent(new Flex(axis),
            [new Item(childSize, firstGrow), new Item(childSize, secondGrow)]));
        frames.Frame();
        Entity flex = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, flex).ToArray();

        Rect first = axis == Axis.Horizontal ? new Rect(0, 0, firstSize, 50) : new Rect(0, 0, 80, firstSize);
        Rect second = axis == Axis.Horizontal ? new Rect(firstSize, 0, secondSize, 50) : new Rect(0, firstSize, 80, secondSize);
        Assert.Equal(first, frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(second, frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Fact]
    public void RowKeepsChildWidthsAndPlacesGapBetweenThem()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new RowChildren());
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();

        Assert.Equal(new Rect(0, 0, 50, 50), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(70, 0, 50, 50), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    private readonly record struct RowChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Row(20).Content())
            {
                Scope.View(new Absolute(), 1).Size(50, 50);
                Scope.View(new Absolute(), 2).Size(50, 50);
            }
            return default;
        }
    }

    private readonly record struct BasisFillRow : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Row().Content())
            {
                Scope.View(new AvailableSize(), 1).Basis(0).Grow();
                Scope.View(new Absolute(), 2).Size(100, 50);
            }
            return default;
        }
    }

    private readonly record struct AvailableSize : ILayout, IComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) => constraints.Max;
    }

    private readonly record struct ReflowRow : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            using (Row().Size(150, float.NaN).Align(Align.Start, Align.Start).Content())
            {
                Scope.View(new ReflowContent(), 2);
                Scope.View(new Absolute(), 3).Size(100, 20);
            }
            return default;
        }
    }

    private readonly record struct ReflowContent : ILayout, IComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            float width = MathF.Min(100, constraints.Max.Width);
            return new Size(width, width < 100 ? 40 : 20);
        }
    }

    private readonly record struct ExpandedRow(bool Expanded) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Row().Gap(Expanded ? 20 : 10).Content())
            {
                Scope.View(new Absolute(), 1).Size(100, 50).Grow(Expanded ? 1 : 0);
                Scope.View(new Absolute(), 2).Size(float.NaN, 50).Grow();
            }
            return default;
        }
    }

    private readonly record struct DividerRow(float Height, float Padding) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            using (Row(10).Size(400, Height).Padding(Padding).Align(Align.Start, Align.Center).Content())
            {
                Scope.View(new Absolute(), 2).Size(100, 40);
                Scope.View(new Absolute(), 3).Size(4, float.NaN).Shrink(0).AlignSelf(Align.Stretch);
                Scope.View(new Absolute(), 4).Size(100, 40);
            }
            return default;
        }
    }

    private readonly record struct NestedRows : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Row(10).Padding(10).Content())
            {
                Scope.View(new Absolute(), 1).Size(80, 40);
                using (Column(5).Grow().Content())
                {
                    Scope.View(new Absolute(), 2).Size(float.NaN, 30);
                    Scope.View(new Absolute(), 3).Size(float.NaN, 30);
                }
            }
            return default;
        }
    }

    private readonly record struct AlignedRow(Align Main) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Row().Gap(20).Align(Main, Align.Center).Content())
            {
                Scope.View(new Absolute(), 1).Size(50, 40);
                Scope.View(new Absolute(), 2).Size(50, 40);
            }
            return default;
        }
    }

    private readonly record struct Item(Size Fixed, float Grow = 0, float Shrink = 1, Size Natural = default,
        Align Self = Align.Auto, float Basis = float.NaN, Size? Minimum = null, Size? Maximum = null);

    private readonly record struct FlexContent(Flex Layout, Item[] Items) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(Layout, 1).Content())
            {
                foreach (Item item in Items)
                {
                    Entity child = Scope.View(new NaturalSize(item.Natural), 2).Size(item.Fixed.Width, item.Fixed.Height);
                    if (item.Grow > 0)
                        child.Grow(item.Grow);
                    if (item.Shrink != 1)
                        child.Shrink(item.Shrink);
                    if (item.Self != Align.Auto)
                        child.AlignSelf(item.Self);
                    if (!float.IsNaN(item.Basis))
                        child.Basis(item.Basis);
                    if (item.Minimum is { } minimum)
                        child.MinSize(minimum.Width, minimum.Height);
                    if (item.Maximum is { } maximum)
                        child.MaxSize(maximum.Width, maximum.Height);
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
