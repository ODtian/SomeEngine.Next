using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class BehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoutedClicksToggleSelectableAncestorsThroughTheStoppingBoundary(bool stop)
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new SelectionTree(stop));
        frames.Frame();
        Entity parent = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, parent)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Click(frames);
        frames.Frame();
        Assert.True(frames.World.Read<Selected>(child).Value);
        Assert.Equal(!stop, frames.World.Read<Selected>(parent).Value);

        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(12, 12));
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.True(frames.World.Read<Selected>(child).Value);
        Assert.Equal(!stop, frames.World.Read<Selected>(parent).Value);

        Click(frames);
        frames.Frame();
        Assert.False(frames.World.Read<Selected>(child).Value);
        Assert.False(frames.World.Read<Selected>(parent).Value);
    }

    [Fact]
    public void WheelAtTheContentBoundaryPreservesThePublicOffsetWriteVersion()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ScrollScene(new Size(240, 180)));
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity scroll = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(1000, 1000));
        frames.Frame();
        uint version = frames.World.WriteVersion<ScrollOffset>(scroll);

        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(5, 5));
        frames.Frame();
        Assert.Equal(new Vector2(160, 140), frames.World.Read<ScrollOffset>(scroll).Value);
        Assert.Equal(version, frames.World.WriteVersion<ScrollOffset>(scroll));
    }

#if DEBUG
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BehaviorDeclarationsUseTheSharedDevelopmentScopeCheck(int behavior)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            switch (behavior)
            {
                case 0: DraggableFunctions.Draggable(Entity.Null); break;
                case 1: ScrollableFunctions.Scrollable(Entity.Null); break;
                case 2: SelectableFunctions.Selectable(Entity.Null); break;
            }
        });
        Assert.Contains("active view build", error.Message);
    }
#endif

    [Fact]
    public void LeavingTheInteractiveTreeEndsTheDragAndKeepsItsCompletedData()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new DragScene());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(20, 15));
        frames.Frame();
        AssertDrag(frames.World, node, true, new Vector2(10, 5), new Vector2(10, 5), 1);

        frames.World.Remove<Pressable>(node);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        frames.Frame();
        AssertDrag(frames.World, node, false, Vector2.Zero, new Vector2(10, 5), 1);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        AssertDrag(frames.World, node, false, Vector2.Zero, new Vector2(10, 5), 1);
        Assert.Equal(0u, frames.World.Read<Clicks>(node).Count);
    }

    [Theory]
    [InlineData(PointerButton.Primary)]
    [InlineData(PointerButton.Secondary)]
    [InlineData(PointerButton.Middle)]
    public void TheNearestDragBehaviorUsesThePrimaryButtonAndOtherButtonsKeepClickSelection(PointerButton button)
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new NestedDragScene());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity scroll = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, scroll)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 40));
        Pointer.Press(frames.World, frames.Canvas, button);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 30));
        Pointer.Release(frames.World, frames.Canvas, button);
        frames.Frame();

        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(scroll).Value);
        AssertDrag(frames.World, scroll, false, Vector2.Zero, Vector2.Zero, 0);
        if (button == PointerButton.Primary)
        {
            AssertDrag(frames.World, content, false, new Vector2(0, -10), new Vector2(0, -10), 1);
            Assert.False(frames.World.Read<Selected>(content).Value);
            Assert.Equal(0u, frames.World.Read<Clicks>(content).Count);
        }
        else
        {
            AssertDrag(frames.World, content, false, Vector2.Zero, Vector2.Zero, 0);
            Assert.True(frames.World.Read<Selected>(content).Value);
            Assert.Equal(1u, frames.World.Read<Clicks>(content).Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallersComposeTheSameBehaviorsFromPublicComponents(bool rewritten) => BehaviorChecks.Run(rewritten);

    [Fact]
    public void ContentAndViewportChangesClampTheOffsetAndEachInputAppliesOnce()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ScrollScene(new Size(240, 180)));
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, node)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(70, 80));
        frames.Frame();

        using var layout = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(frames.World));
        layout.Add(new LayoutSystem());
        layout.Update();
        Assert.Equal(new Vector2(70, 80), frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, -40, -60);

        frames.World.Replace(root, new ScrollScene(new Size(110, 65)));
        frames.Frame();
        Assert.Equal(new Vector2(30, 25), frames.World.Read<ScrollOffset>(node).Max);
        Assert.Equal(new Vector2(30, 25), frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, 0, -5);

        frames.World.Replace(node, new Box { Fixed = new Size(200, 120), Padding = new Thickness(10, 10, 10, 10) });
        frames.Frame();
        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(node).Max);
        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, 30, 20);

        frames.World.Replace(root, new ScrollScene(new Size(40, 20)));
        frames.Frame();
        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(node).Max);
        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, 30, 20);
    }

    [Fact]
    public void NestedScrollViewportsCombineTheirOffsetsAndWheelUsesTheNearestViewport()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new NestedScrollScene());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity outer = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Entity inner = Hierarchy<ElementTree>.GetChildren(frames.World, outer)[0];
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, inner)[0];
        Assert.Equal(new Vector2(70, 50), frames.World.Read<ScrollOffset>(outer).Max);
        Assert.Equal(new Vector2(50, 40), frames.World.Read<ScrollOffset>(inner).Max);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(25, 15));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(30, 20));
        frames.Frame();
        Assert.Equal(new Vector2(30, 20), frames.World.Read<ScrollOffset>(outer).Value);
        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(inner).Value);
        AssertPosition(frames.World, inner, 10, 0);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(50, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(40, 30));
        frames.Frame();
        Assert.Equal(new Vector2(30, 20), frames.World.Read<ScrollOffset>(outer).Value);
        Assert.Equal(new Vector2(40, 30), frames.World.Read<ScrollOffset>(inner).Value);
        AssertPosition(frames.World, content, -30, -30);
    }

    [Fact]
    public void ScrollableContentsReceiveClicksWithinTheViewportAndDragTheirScrollParent()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ScrollButtonsScene());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, node)[0];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(180, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(0, 10));
        Click(frames);
        frames.Frame();
        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(node).Value);
        Assert.False(frames.World.Read<Selected>(content).Value);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(0, 20));
        frames.Frame();
        Assert.Equal(new Vector2(0, 20), frames.World.Read<ScrollOffset>(node).Value);
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 10));
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(new Vector2(0, 40), frames.World.Read<ScrollOffset>(node).Value);
        AssertDrag(frames.World, node, false, new Vector2(0, -20), new Vector2(0, -20), 1);
        Assert.False(frames.World.Read<Selected>(content).Value);
        Assert.Equal(0u, frames.World.Read<Clicks>(content).Count);

        Click(frames);
        frames.Frame();
        Assert.True(frames.World.Read<Selected>(content).Value);
    }

    [Fact]
    public void TransparentCompositionProvidesAScrollViewportAndKeepsDraggingOutsideIt()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ScrollCompositionScene());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, node)[0];
        Assert.Equal(new Vector2(160, 140), frames.World.Read<ScrollOffset>(node).Max);
        AssertPosition(frames.World, content, 30, 20);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(30, 20));
        frames.Frame();
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 0));
        frames.Frame();
        Assert.Equal(new Vector2(60, 50), frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, -30, -30);
        AssertDrag(frames.World, node, true, new Vector2(-30, -30), new Vector2(-30, -30), 1);

        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        AssertDrag(frames.World, node, false, Vector2.Zero, new Vector2(-30, -30), 1);
        Assert.Equal(new Vector2(60, 50), frames.World.Read<ScrollOffset>(node).Value);
    }

    [Fact]
    public void WheelInputClampsToContentAndMovesChildRectanglesInEventOrder()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ScrollScene(new Size(240, 180)));
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, node)[0];
        Assert.Equal(new Vector2(160, 140), frames.World.Read<ScrollOffset>(node).Max);
        AssertPosition(frames.World, content, 30, 20);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(40, 30));
        frames.Frame();
        Assert.Equal(new Vector2(40, 30), frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, -10, -10);

        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(1000, 1000));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(-20, -15));
        frames.Frame();
        Assert.Equal(new Vector2(140, 125), frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, -110, -105);
        frames.Frame();
        Assert.Equal(new Vector2(140, 125), frames.World.Read<ScrollOffset>(node).Value);

        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(-1000, -1000));
        frames.Frame();
        Assert.Equal(Vector2.Zero, frames.World.Read<ScrollOffset>(node).Value);
        AssertPosition(frames.World, content, 30, 20);
    }

    [Fact]
    public void ClicksToggleSelectionAndRefreshTheCallersDisplay()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new SelectionScene(0));
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Assert.Equal("Off", frames.World.Read<SelectionLabel>(root).Value);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Click(frames);
        frames.Frame();
        Assert.True(frames.World.Read<Selected>(node).Value);
        Assert.Equal("On", frames.World.Read<SelectionLabel>(root).Value);

        frames.World.Replace(root, new SelectionScene(1));
        frames.Frame();
        Assert.True(frames.World.Read<Selected>(node).Value);
        Click(frames);
        Click(frames);
        frames.Frame();
        Assert.True(frames.World.Read<Selected>(node).Value);
        Click(frames);
        frames.Frame();
        Assert.False(frames.World.Read<Selected>(node).Value);
        Assert.Equal("Off", frames.World.Read<SelectionLabel>(root).Value);
    }

    [Fact]
    public void PrimaryPointerMotionReportsEachDragAndItsFrameDisplacement()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new DragScene());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        AssertDrag(frames.World, node, false, Vector2.Zero, Vector2.Zero, 0);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(22, 14));
        Pointer.Move(frames.World, frames.Canvas, new Vector2(30, 20));
        frames.Frame();
        AssertDrag(frames.World, node, true, new Vector2(20, 10), new Vector2(20, 10), 1);
        frames.Frame();
        AssertDrag(frames.World, node, true, Vector2.Zero, new Vector2(20, 10), 1);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(150, 80));
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        AssertDrag(frames.World, node, false, new Vector2(120, 60), new Vector2(140, 70), 1);
        frames.Frame();
        AssertDrag(frames.World, node, false, Vector2.Zero, new Vector2(140, 70), 1);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(7, 5));
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        AssertDrag(frames.World, node, false, new Vector2(-3, -5), new Vector2(-3, -5), 2);
    }

    private static void AssertDrag(World world, Entity node, bool active, Vector2 delta, Vector2 total, uint count)
    {
        Drag drag = world.Read<Drag>(node);
        Assert.Equal(active, drag.Active);
        Assert.Equal(delta, drag.Delta);
        Assert.Equal(total, drag.Total);
        Assert.Equal(count, drag.Count);
    }

    private static void AssertPosition(World world, Entity node, float x, float y)
    {
        var rect = world.Read<LayoutRect>(node).Value;
        Assert.Equal(x, rect.X);
        Assert.Equal(y, rect.Y);
    }

    private readonly record struct ScrollScene(Size ContentSize) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollableFunctions.Scrollable(Absolute().Size(100, 60).Padding(10).Position(20, 10)).Content())
                Absolute().Size(ContentSize.Width, ContentSize.Height);
            return default;
        }
    }

    private readonly record struct ScrollCompositionScene() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Scope.View(new ScrollPanel()).Size(100, 60).Padding(10).Position(20, 10);
            return default;
        }
    }

    private readonly record struct ScrollPanel() : IView, IComponent
    {
        public BuildTask Build()
        {
            ScrollableFunctions.Scrollable(Scope.Entity);
            Absolute().Size(240, 180);
            return default;
        }
    }

    private readonly record struct ScrollButtonsScene() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollableFunctions.Scrollable(Absolute().Size(100, 60).Padding(10).Position(20, 10)).Content())
                SelectableFunctions.Selectable(Absolute().Size(240, 180));
            return default;
        }
    }

    private readonly record struct NestedScrollScene() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollableFunctions.Scrollable(Absolute().Size(100, 60).Position(20, 10)).Content())
            using (ScrollableFunctions.Scrollable(Absolute().Size(150, 100).Position(20, 10)).Content())
                Absolute().Size(200, 140);
            return default;
        }
    }

    private readonly record struct NestedDragScene() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollableFunctions.Scrollable(Absolute().Size(100, 60).Padding(10).Position(20, 10)).Content())
                DraggableFunctions.Draggable(SelectableFunctions.Selectable(Absolute().Size(240, 180)));
            return default;
        }
    }

    private readonly record struct SelectionTree(bool Stop) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity parent = SelectableFunctions.Selectable(Absolute());
            DraggableFunctions.Draggable(parent);
            using (parent.Content())
            {
                Entity child = SelectableFunctions.Selectable(Absolute().Size(100, 50));
                if (Stop)
                    Scope.Ensure<StopsPointer>(child);
            }
            return default;
        }
    }

    private static void Click(PointerFrames frames)
    {
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
    }

    private readonly record struct SelectionLabel(string Value) : IComponent;

    private readonly record struct SelectionScene(int Revision) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            {
                Entity node = SelectableFunctions.Selectable(Absolute().Size(100, 50));
                Scope.Write<SelectionLabel>(Scope.Entity) = new SelectionLabel(Scope.Read<Selected>(node).Value ? "On" : "Off");
            }
            return default;
        }
    }

    private readonly record struct DragScene() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                DraggableFunctions.Draggable(Absolute().Size(100, 50));
            return default;
        }
    }
}
