using static SomeEngine.Layout.AbsoluteFunctions;
using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Views;

namespace SomeEngine.Layout.Tests;

public sealed partial class LayoutTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(float.NaN, 0, 50, 0)]
    [InlineData(0, float.NaN, 0, 20)]
    public void FixedSizeSupportsZeroAndAnAutomaticAxis(float width, float height, float expectedWidth, float expectedHeight)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new StyledLeaf(new Size(width, height), default));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));

        Assert.Equal(new Rect(0, 0, expectedWidth, expectedHeight), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Fact]
    public void DefaultBoxSupportsIntrinsicSizingAndAsymmetricPadding()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new StyledLeaf(Size.Auto, new Thickness(3, 5, 7, 11)));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));

        Assert.Equal(new Rect(0, 0, 60, 36), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Fact]
    public void EachCanvasUsesItsOwnSizeAndScaleIncludingNestedCanvases()
    {
        using var frames = new LayoutFrames();
        Entity first = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity firstRoot = Views.Views.Spawn(frames.World, first, new Fill());
        Entity second = frames.World.CreateEntity(new Canvas
        {
            Size = new Size(200, 100), Scale = 2, Target = CanvasTarget.RenderTexture(first),
        });
        Hierarchy<ElementTree>.SetParent(frames.World, second, first);
        Entity secondRoot = Views.Views.Spawn(frames.World, second, new Fill());

        frames.Frame();

        Assert.Equal(new Rect(0, 0, 800, 600), frames.World.Read<LayoutRect>(firstRoot).Value);
        Assert.Equal(new Rect(0, 0, 100, 50), frames.World.Read<LayoutRect>(secondRoot).Value);
    }

    [Fact]
    public void ExplicitUserLayoutPlacesNestedContentOnAScaledCanvas() => Views.AotSmoke.LayoutChecks.Run();

#if DEBUG
    [Fact]
    public void ConflictingBoxLimitsAreReportedByDevelopmentChecks()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new Fill());
        frames.World.Add(root, new Box { Min = new Size(150, 80), Max = new Size(100, 100) });

        Assert.Throws<InvalidOperationException>(frames.Frame);
    }
#endif

    [Fact]
    public void NamedAbsoluteFunctionDeclaresAndPlacesCallerChildren()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new NamedChildren());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));

        Assert.Equal(new Rect(7, 9, 30, 40), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Fact]
    public void PaddingChangesChildAvailableSizeAndContentOriginInTheSameFrame()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new PaddedChildren(10));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity box = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity fill = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, box));
        Assert.Equal(new Rect(30, 40, 200, 100), frames.World.Read<LayoutRect>(box).Value);
        Assert.Equal(new Rect(45, 57, 175, 73), frames.World.Read<LayoutRect>(fill).Value);

        frames.World.Replace(root, new PaddedChildren(20));
        frames.Frame();

        Assert.Equal(new Rect(55, 67, 155, 53), frames.World.Read<LayoutRect>(fill).Value);
    }

    [Fact]
    public void CanvasPlacesTwoFixedChildrenAtTheirAbsolutePositions()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas
        {
            Size = new Size(800, 600), Scale = 1,
        });
        Entity root = Views.Views.Spawn(frames.World, canvas, new PositionedChildren());

        frames.Frame();

        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, absolute).ToArray();
        Assert.Equal(new Rect(0, 0, 800, 600), frames.World.Read<LayoutRect>(absolute).Value);
        Assert.Equal(new Rect(10, 20, 100, 50), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(300, 200, 80, 120), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Fact]
    public void CompositionBetweenAnAbsoluteAndItsChildrenPreservesTheirPositions()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new WrappedChildren());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity composition = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, composition).ToArray();

        Assert.Equal(new Rect(10, 20, 100, 50), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(300, 200, 80, 120), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Fact]
    public void UserRingPlacesNestedAbsoluteContentInCanvasCoordinates()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new RingChildren());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity ring = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, ring).ToArray();
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[0]));

        Assert.Equal(new Rect(40, 30, 200, 200), frames.World.Read<LayoutRect>(ring).Value);
        AssertRect(new Rect(195, 120, 20, 20), frames.World.Read<LayoutRect>(children[0]).Value);
        AssertRect(new Rect(65, 120, 20, 20), frames.World.Read<LayoutRect>(children[1]).Value);
        AssertRect(new Rect(198, 125, 4, 6), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Theory]
    [InlineData(30, 20, 120, 80)]
    [InlineData(500, 300, 160, 100)]
    public void BoxLimitsAnIntrinsicSizeThroughModifiers(float width, float height, float expectedWidth, float expectedHeight)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new BoundedChildren(new Size(width, height)));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));

        Assert.Equal(new Rect(0, 0, expectedWidth, expectedHeight), frames.World.Read<LayoutRect>(child).Value);
    }

    private readonly record struct PositionedChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            {
                Scope.View(new Absolute(), 2).Size(100, 50).Position(10, 20);
                Scope.View(new Absolute(), 3).Size(80, 120).Position(300, 200);
            }
            return default;
        }
    }

    private readonly record struct NamedChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Absolute().Size(30, 40).Position(7, 9);
            return default;
        }
    }

    private static void AssertRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 3);
        Assert.Equal(expected.Y, actual.Y, precision: 3);
        Assert.Equal(expected.Width, actual.Width, precision: 3);
        Assert.Equal(expected.Height, actual.Height, precision: 3);
    }

    private readonly record struct PaddedChildren(float Padding) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            using (Scope.View(new Absolute(), 2).Size(200, 100).Position(30, 40).Padding(Padding).Content())
                Scope.View(new Fill(), 3).Position(5, 7);
            return default;
        }
    }

    private readonly record struct Fill : ILayout, IComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) => constraints.Max;
    }

    private readonly record struct WrappedChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
                Scope.View(new TwoChildren(), 2).Size(1, 1).Position(700, 500).Padding(99);
            return default;
        }
    }

    private readonly record struct TwoChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Absolute(), 1).Size(100, 50).Position(10, 20);
            Scope.View(new Absolute(), 2).Size(80, 120).Position(300, 200);
            return default;
        }
    }

    private readonly record struct RingChildren : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            using (Scope.View(new Ring(65), 2).Size(200, 200).Position(40, 30).Padding(10).Content())
            {
                using (Scope.View(new Absolute(), 3).Size(20, 20).Content())
                    Scope.View(new Absolute(), 4).Size(4, 6).Position(3, 5);
                Scope.View(new Absolute(), 5).Size(20, 20);
            }
            return default;
        }
    }

    // User-defined sparse layout parameters use the same protocol as Absolute.
    private readonly record struct Ring(float Radius) : ILayout, SomeEngine.ECS.Components.ISparseComponent
    {
        public static ChildSource Children => ChildSource.Caller;
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            float radius = pass.Read<Ring>(pass.Node).Radius;
            for (int index = 0; index < pass.Children.Count; index++)
            {
                Entity child = pass.Children[index];
                var available = new Constraints(default, constraints.Max);
                Size size = pass.Measure(child, in available);
                float angle = 2 * MathF.PI * index / pass.Children.Count;
                pass.Place(child, new Vector2(
                    constraints.Max.Width / 2 + radius * MathF.Cos(angle) - size.Width / 2,
                    constraints.Max.Height / 2 + radius * MathF.Sin(angle) - size.Height / 2));
            }
            return constraints.Max;
        }
    }

    private readonly record struct NaturalSize(Size Value) : ILayout, IComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) => Value;
    }

    private readonly record struct BoundedChildren(Size Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
                Scope.View(new NaturalSize(Value), 2).MinSize(120, 80).MaxSize(160, 100);
            return default;
        }
    }

    private readonly record struct StyledLeaf(Size Fixed, Thickness Padding) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            {
                Entity leaf = Scope.View(new NaturalSize(new Size(50, 20)), 2).Size(Fixed.Width, Fixed.Height);
                Scope.Write<Box>(leaf).Padding = Padding;
            }
            return default;
        }
    }
}

internal sealed class LayoutFrames : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    public World World { get; } = new();

    public LayoutFrames()
    {
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        _systems.Add(new ViewRebuildSystem());
        _systems.Add(new LayoutSystem());
    }

    public void Frame() => _systems.Update();

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
    }
}
