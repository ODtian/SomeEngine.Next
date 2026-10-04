using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Layout.Tests;

public sealed partial class StackLayoutTests
{
    [Fact]
    public void NamedStackAndGridFunctionsComposeAndResizeThroughTheSharedProtocol() => Views.AotSmoke.StackGridChecks.Run();

#if DEBUG
    [Theory]
    [InlineData(Align.Auto, Align.Start)]
    [InlineData(Align.Start, Align.SpaceBetween)]
    public void InvalidAlignmentIsReportedByDevelopmentChecks(Align horizontal, Align vertical)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(100, 100) });
        Views.Views.Spawn(frames.World, canvas, new Stack(horizontal, vertical));

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);
        Assert.Contains("Stack", error.Message);
    }
#endif

    [Fact]
    public void StackStretchesAutomaticDimensionsWithinBoxLimitsAndKeepsFixedDimensions()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 300) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new StretchedChildren());
        frames.Frame();
        Entity stack = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, stack).ToArray();

        Assert.Equal(new Rect(10, 10, 160, 120), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(10, 10, 60, 40), frames.World.Read<LayoutRect>(children[1]).Value);
        Assert.Equal(new Rect(10, 10, 380, 280), frames.World.Read<LayoutRect>(children[2]).Value);
    }

    [Theory]
    [InlineData(Align.Start, Align.Start, 15, 20)]
    [InlineData(Align.Center, Align.End, 55, 80)]
    [InlineData(Align.End, Align.Center, 95, 50)]
    public void StackSizesToItsLargestChildrenAndAlignsTheirOuterBoxes(Align horizontal, Align vertical,
        float firstX, float secondY)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 300) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new OverlaidChildren(horizontal, vertical));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity stack = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, stack).ToArray();

        Assert.Equal(new Rect(10, 15, 130, 90), frames.World.Read<LayoutRect>(stack).Value);
        Assert.Equal(new Rect(firstX, 20, 40, 80), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(15, secondY, 120, 20), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    private readonly record struct OverlaidChildren(Align Horizontal, Align Vertical) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            using (Scope.View(new Stack(Horizontal, Vertical), 2).Padding(5).Position(10, 15).Content())
            {
                Scope.View(new Absolute(), 3).Size(40, 80);
                Scope.View(new Absolute(), 4).Size(120, 20);
            }
            return default;
        }
    }

    private readonly record struct StretchedChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Stack(Align.Stretch, Align.Stretch), 1).Padding(10).Content())
            {
                Scope.View(new NaturalSize(new Size(40, 20)), 2).MaxSize(160, 120);
                Scope.View(new NaturalSize(new Size(40, 20)), 3).Size(60, 40);
                Scope.View(new NaturalSize(new Size(40, 20)), 4).MinSize(800, 600);
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
