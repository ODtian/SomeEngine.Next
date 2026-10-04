using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.ScrollViewFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class ScrollViewTests
{
    [Fact]
    public void CallerContentScrollsWithinItsMeasuredRangeAndSurvivesRedeclaration()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ScrollScene(0));
        frames.Frame();
        Entity panel = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity scroll = Hierarchy<ElementTree>.GetChildren(frames.World, panel)[0];
        Entity content = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, scroll));
        Assert.Equal(new Rect(20, 10, 100, 60), frames.World.Read<LayoutRect>(scroll).Value);
        Assert.Equal(new Vector2(160, 140), frames.World.Read<ScrollOffset>(scroll).Max);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 30));
        Pointer.Wheel(frames.World, frames.Canvas, new Vector2(30, 50));
        frames.Frame();

        Assert.Equal(new Vector2(30, 50), frames.World.Read<ScrollOffset>(scroll).Value);
        Assert.Equal(new Rect(0, -30, 240, 180), frames.World.Read<LayoutRect>(content).Value);
        frames.World.Replace(root, new ScrollScene(1));
        frames.Frame();
        Assert.Equal(content, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, scroll)));
        Assert.Equal(new Vector2(30, 50), frames.World.Read<ScrollOffset>(scroll).Value);
    }

    private readonly record struct ScrollScene(int Revision) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (ScrollView().Size(100, 60).Padding(10).Position(20, 10).Content())
                Absolute().Size(240, 180);
            return default;
        }
    }
}
