using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.VirtualListFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class VirtualListTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListsAndScrollViewsComposeThroughPublicApis(bool rewritten) => ListChecks.Run(rewritten);

    [Fact]
    public void TenThousandItemsCreateTheViewportRowsAndOneBufferRow()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ListExample(10000));
        frames.Frame();
        frames.Frame();
        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, panel));
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();

        Assert.Equal([0, 1, 2], rows.Select(row => frames.World.Read<ItemRow>(row).Index));
        Assert.Equal(new VisibleRange(0, 3), frames.World.Read<DisplayedRange>(root).Value);
        Assert.Equal(new Vector2(0, 319936), frames.World.Read<ScrollOffset>(list).Max);
        Assert.Equal(new Rect(0, 0, 100, 32), frames.World.Read<LayoutRect>(rows[0]).Value);
        Assert.Equal(new Rect(0, 64, 100, 32), frames.World.Read<LayoutRect>(rows[2]).Value);
    }

    [Fact]
    public void ScrollingReleasesDepartingRowsCreatesEnteringRowsAndPreservesKeptRowData()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ListExample(10000));
        frames.Frame();
        frames.Frame();
        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, panel));
        Entity[] before = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        frames.World.Replace(before[1], new RowText("Editing item 1"));

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(0, 64));
        frames.Frame();
        frames.Frame();
        Entity[] after = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();

        Assert.Equal(new VisibleRange(1, 5), frames.World.Read<DisplayedRange>(root).Value);
        Assert.Equal([1, 2, 3, 4], after.Select(row => frames.World.Read<ItemRow>(row).Index));
        Assert.Equal(before[1], after[0]);
        Assert.Equal(before[2], after[1]);
        Assert.False(frames.World.IsAlive(before[0]));
        Assert.Equal("Editing item 1", frames.World.Read<RowText>(after[0]).Value);
        Assert.Equal("Item 3", frames.World.Read<RowText>(after[2]).Value);
        Assert.Equal(new Rect(0, 0, 100, 32), frames.World.Read<LayoutRect>(after[1]).Value);
    }

    [Fact]
    public void PaddingEndOfListAndShrinkingItemsKeepTheRangeAndOffsetWithinContent()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ListExample(12, Height: 84, Padding: 10));
        frames.Frame();
        frames.Frame();
        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, panel));
        Assert.Equal(new VisibleRange(0, 3), frames.World.Read<DisplayedRange>(root).Value);
        Assert.Equal(320, frames.World.Read<ScrollOffset>(list).Max.Y);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(20, 20));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(0, 1000));
        frames.Frame();
        frames.Frame();
        Entity[] endRows = Hierarchy<ElementTree>.GetChildren(frames.World, list).ToArray();
        Assert.Equal([9, 10, 11], endRows.Select(row => frames.World.Read<ItemRow>(row).Index));
        Assert.Equal(new VisibleRange(9, 12), frames.World.Read<DisplayedRange>(root).Value);
        Assert.Equal(-22, frames.World.Read<LayoutRect>(endRows[0]).Value.Y);

        frames.World.Replace(root, new ListExample(2, Height: 84, Padding: 10));
        frames.Frame();
        frames.Frame();
        Assert.Equal(new VisibleRange(0, 2), frames.World.Read<DisplayedRange>(root).Value);
        Assert.Equal(0, frames.World.Read<ScrollOffset>(list).Value.Y);
        Assert.Equal(0, frames.World.Read<ScrollOffset>(list).Max.Y);
        Assert.All(endRows, row => Assert.False(frames.World.IsAlive(row)));

        frames.World.Replace(root, new ListExample(0, Height: 84, Padding: 10));
        frames.Frame();
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, list));
        Assert.Equal(default, frames.World.Read<DisplayedRange>(root).Value);
    }

    [Fact]
    public void ViewportAndRowHeightChangesRefreshPartialRowsAndAnEmptyViewport()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ListExample(10));
        frames.Frame();
        frames.Frame();
        Entity panel = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity list = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, panel));
        Entity first = Hierarchy<ElementTree>.GetChildren(frames.World, list)[0];
        frames.World.Replace(first, new RowText("Editing item 0"));

        frames.World.Replace(root, new ListExample(10, RowHeight: 16, Height: 100));
        frames.Frame();
        frames.Frame();
        Assert.Equal(new VisibleRange(0, 8), frames.World.Read<DisplayedRange>(root).Value);
        Assert.Equal(first, Hierarchy<ElementTree>.GetChildren(frames.World, list)[0]);
        Assert.Equal("Editing item 0", frames.World.Read<RowText>(first).Value);
        Assert.Equal(new Rect(0, 0, 100, 16), frames.World.Read<LayoutRect>(first).Value);

        frames.World.Replace(root, new ListExample(10, RowHeight: 16, Height: 0));
        frames.Frame();
        frames.Frame();
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, list));
        Assert.Equal(default, frames.World.Read<DisplayedRange>(root).Value);
        Assert.Equal(160, frames.World.Read<ScrollOffset>(list).Max.Y);
        Assert.False(frames.World.IsAlive(first));

        frames.World.Replace(root, new ListExample(10, RowHeight: 16, Height: 32));
        frames.Frame();
        frames.Frame();
        Assert.Equal(new VisibleRange(0, 3), frames.World.Read<DisplayedRange>(root).Value);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(0, 1));
        frames.Frame();
        frames.Frame();
        Assert.Equal(new VisibleRange(0, 4), frames.World.Read<DisplayedRange>(root).Value);
        Entity row = Hierarchy<ElementTree>.GetChildren(frames.World, list)[0];
        Assert.Equal(new Rect(0, -1, 100, 16), frames.World.Read<LayoutRect>(row).Value);
    }

#if DEBUG
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RowHeightUsageErrorsAreReportedThroughTheBuildError(float height)
    {
        using var frames = new PointerFrames();
        Entity list = Views.Views.Spawn(frames.World, Entity.Null, new VirtualList(height));
        frames.Frame();

        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(list).Exception);
        Assert.Contains("RowHeight", error.Message);
    }

    [Fact]
    public void ItemCountUsageErrorsAreReportedThroughTheBuildError()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ListExample(-1));
        frames.Frame();

        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Contains("item count", error.Message);
    }

    [Fact]
    public void VisibleRequiresAnActiveBuildAndAVirtualListNode()
    {
        Assert.Throws<InvalidOperationException>(() => Visible(Entity.Null, 1));
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new OtherContainer());
        frames.Frame();

        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Contains("VirtualList node", error.Message);
    }

    private readonly record struct OtherContainer() : IView, IComponent
    {
        public BuildTask Build()
        {
            Visible(Absolute(), 1);
            return default;
        }
    }
#endif

    private readonly record struct ListExample(int Count, float RowHeight = 32, float Height = 64, float Padding = 0) : IView, IComponent
    {
        public BuildTask Build()
        {
            using var panel = Absolute().Content();
            using var content = VirtualList(RowHeight).Size(100, Height).Padding(Padding).Content();
            VisibleRange range = Visible(content.Entity, Count);
            Scope.Write<DisplayedRange>(Scope.Entity) = new DisplayedRange(range);
            foreach (int index in range)
            {
                using (Scope.Key((ulong)index))
                    Scope.View(new ItemRow(index));
            }
            return default;
        }
    }

    private readonly record struct ItemRow(int Index) : IView, IComponent, ILayout
    {
        public BuildTask Build()
        {
            Scope.Write<RowText>(Scope.Entity) = new RowText($"Item {Index}");
            return default;
        }

        public Size Measure(ref LayoutPass pass, in Constraints constraints) => constraints.Constrain(new Size(100, 16));
    }

    private readonly record struct DisplayedRange(VisibleRange Value) : IComponent;
    private readonly record struct RowText(string Value) : IComponent;
}
