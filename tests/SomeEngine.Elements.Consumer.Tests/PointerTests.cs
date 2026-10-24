using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.PressableFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class PointerTests
{
#if DEBUG
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PointerInputsRequireALiveCanvas(int input)
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (input)
            {
                case 0: Pointer.Move(world, entity, Vector2.Zero); break;
                case 1: Pointer.Press(world, entity, PointerButton.Primary); break;
                case 2: Pointer.Release(world, entity, PointerButton.Primary); break;
                case 3: Pointer.Wheel(world, entity, Vector2.One); break;
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PressableReadsAndDeclarationsRequireAnActiveScope(bool read)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (read)
                Entity.Null.Clicked();
            else
                Pressable(Entity.Null);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PointerInputsReportUnsupportedValuesDuringDevelopment(int input)
    {
        using var frames = new PointerFrames();
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (input)
            {
                case 0: Pointer.Move(frames.World, frames.Canvas, new Vector2(float.NaN, 0)); break;
                case 1: Pointer.Wheel(frames.World, frames.Canvas, new Vector2(0, float.PositiveInfinity)); break;
                case 2: Pointer.Press(frames.World, frames.Canvas, (PointerButton)byte.MaxValue); break;
            }
        });
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputStreamsStayWithinTheirCanvases(bool nested)
    {
        using var frames = new PointerFrames();
        Entity otherCanvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 300), Scale = 2 });
        if (nested)
            Hierarchy<ElementTree>.SetParent(frames.World, otherCanvas, frames.Canvas);
        Entity first = Views.Views.Spawn(frames.World, frames.Canvas, new SingleButton());
        Entity second = Views.Views.Spawn(frames.World, otherCanvas, new SingleButton());
        frames.Frame();
        Entity firstContainer = Hierarchy<ElementTree>.GetChildren(frames.World, first)[0];
        Entity firstButton = Hierarchy<ElementTree>.GetChildren(frames.World, firstContainer)[0];
        Entity secondContainer = Hierarchy<ElementTree>.GetChildren(frames.World, second)[0];
        Entity secondButton = Hierarchy<ElementTree>.GetChildren(frames.World, secondContainer)[0];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(1u, frames.World.Read<Clicks>(firstButton).Count);
        Assert.Equal(0u, frames.World.Read<Clicks>(secondButton).Count);

        Pointer.Move(frames.World, otherCanvas, new Vector2(10, 10));
        Pointer.Press(frames.World, otherCanvas, PointerButton.Primary);
        Pointer.Release(frames.World, otherCanvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(1u, frames.World.Read<Clicks>(firstButton).Count);
        Assert.Equal(1u, frames.World.Read<Clicks>(secondButton).Count);
        QueryHandle capture = frames.World.Query(frames.World.QueryDefinition().All<InputCapture>());
        try
        {
            int count = 0;
            frames.World.ExecuteQuery(capture, ref count, static (rows, ref total) =>
            {
                foreach (var row in rows.Rows)
                    total++;
            });
            Assert.Equal(1, count);
        }
        finally
        {
            frames.World.ReleaseQuery(capture);
        }
    }

    [Fact]
    public void HitTestingUsesThePreviousFramesLayout()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new MovingButton(0));
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity button = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];

        frames.World.Replace(root, new MovingButton(150));
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(1u, frames.World.Read<Clicks>(button).Count);
        Assert.Equal(150, frames.World.Read<LayoutRect>(button).Value.X);

        frames.Frame();
        Assert.False(frames.World.Read<Hover>(button).Hovered);
        Assert.False(frames.World.Read<Hover>(button).Within);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(160, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(2u, frames.World.Read<Clicks>(button).Count);
        Assert.True(frames.World.Read<Hover>(button).Within);
    }

    [Fact]
    public void DrawOrderSelectsTheDescendantAndClicksReachItsMarkedAncestors()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new NestedButtons());
        frames.Frame();
        Entity parent = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        var children = Hierarchy<ElementTree>.GetChildren(frames.World, parent);
        Entity branch = children[1];
        Entity button = Hierarchy<ElementTree>.GetChildren(frames.World, branch)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.Equal(1u, frames.World.Read<Clicks>(button).Count);
        Assert.Equal(1u, frames.World.Read<Clicks>(parent).Count);
        Assert.Equal(1u, frames.World.Read<Clicks>(branch).Count);
        Assert.Equal(0u, frames.World.Read<Clicks>(children[0]).Count);
        Assert.True(frames.World.Read<Hover>(button).Hovered);
        Assert.False(frames.World.Read<Hover>(parent).Hovered);
        Assert.False(frames.World.Read<Hover>(branch).Hovered);
        Assert.True(frames.World.Read<Hover>(parent).Within);
        Assert.True(frames.World.Read<Hover>(branch).Within);
    }

    [Theory]
    [InlineData(0, true, 0u, 0u)]
    [InlineData(1, true, 1u, 0u)]
    [InlineData(1, false, 0u, 0u)]
    [InlineData(2, true, 1u, 1u)]
    public void ClicksEndAtStopsPointer(int stopIndex, bool pressable, uint branchClicks, uint parentClicks)
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new NestedButtons());
        frames.Frame();
        Entity parent = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity branch = Hierarchy<ElementTree>.GetChildren(frames.World, parent)[1];
        Entity button = Hierarchy<ElementTree>.GetChildren(frames.World, branch)[0];
        Entity[] path = [button, branch, parent];
        Entity stop = path[stopIndex];
        frames.World.Add(stop, new StopsPointer());
        if (!pressable)
            frames.World.Remove<Pressable>(stop);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.Equal(1u, frames.World.Read<Clicks>(button).Count);
        Assert.Equal(branchClicks, frames.World.Read<Clicks>(branch).Count);
        Assert.Equal(parentClicks, frames.World.Read<Clicks>(parent).Count);
    }

    [Fact]
    public void TransparentCompositionCanDeclarePressableOnItsOwnNode()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new CompositionButtonScene());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity button = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 20));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.True(frames.World.Read<Hover>(button).Hovered);
        Assert.Equal(1u, frames.World.Read<Clicks>(button).Count);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        frames.Frame();
        Assert.False(frames.World.Read<Hover>(button).Hovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerConsumesClicksThroughPublicComponents(bool rewritten) => PointerChecks.Run(rewritten);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerConsumesBubbledClicksAndReadsSubtreeHover(bool rewritten) => PointerChecks.RunRouting(rewritten);

    [Fact]
    public void NestedCanvasKeepsBubbledClicksAndSubtreeHoverInsideItsOwnTree()
    {
        using var frames = new PointerFrames();
        Entity outerRoot = Views.Views.Spawn(frames.World, frames.Canvas, new NestedButtons());
        frames.Frame();
        Entity outerCard = Hierarchy<ElementTree>.GetChildren(frames.World, outerRoot)[0];
        Entity outerBranch = Hierarchy<ElementTree>.GetChildren(frames.World, outerCard)[1];
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(400, 300) });
        frames.World.Add(canvas, new Hover());
        Hierarchy<ElementTree>.SetParent(frames.World, canvas, outerBranch);
        Entity root = Views.Views.Spawn(frames.World, canvas, new NestedButtons());
        frames.Frame();
        Entity card = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity branch = Hierarchy<ElementTree>.GetChildren(frames.World, card)[1];
        Entity button = Hierarchy<ElementTree>.GetChildren(frames.World, branch)[0];

        Pointer.Move(frames.World, canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, canvas, PointerButton.Primary);
        Pointer.Release(frames.World, canvas, PointerButton.Primary);
        frames.Frame();

        Assert.Equal(1u, frames.World.Read<Clicks>(button).Count);
        Assert.Equal(1u, frames.World.Read<Clicks>(branch).Count);
        Assert.Equal(1u, frames.World.Read<Clicks>(card).Count);
        Assert.True(frames.World.Read<Hover>(canvas).Within);
        Assert.True(frames.World.Read<Hover>(card).Within);
        Assert.Equal(0u, frames.World.Read<Clicks>(outerBranch).Count);
        Assert.Equal(0u, frames.World.Read<Clicks>(outerCard).Count);
        Assert.Equal(default, frames.World.Read<Hover>(outerBranch));
        Assert.Equal(default, frames.World.Read<Hover>(outerCard));
    }

    [Theory]
    [InlineData(PointerButton.Primary, PointerButton.Secondary)]
    [InlineData(PointerButton.Secondary, PointerButton.Middle)]
    [InlineData(PointerButton.Middle, PointerButton.Primary)]
    public void ButtonsPairTheirOwnPressAndRelease(PointerButton first, PointerButton second)
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new SeparatedButtons());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        var buttons = Hierarchy<ElementTree>.GetChildren(frames.World, container);
        Entity left = buttons[0];
        Entity right = buttons[1];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, first);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(160, 10));
        Pointer.Press(frames.World, frames.Canvas, second);
        Pointer.Press(frames.World, frames.Canvas, first);
        frames.Frame();
        Assert.True(frames.World.Read<Hover>(left).Pressed);
        Assert.True(frames.World.Read<Hover>(right).Pressed);

        Pointer.Release(frames.World, frames.Canvas, first);
        frames.Frame();
        Assert.Equal(0u, frames.World.Read<Clicks>(right).Count);
        Assert.True(frames.World.Read<Hover>(right).Pressed);

        Pointer.Release(frames.World, frames.Canvas, second);
        frames.Frame();
        Assert.Equal(0u, frames.World.Read<Clicks>(left).Count);
        Assert.Equal(1u, frames.World.Read<Clicks>(right).Count);
        Assert.False(frames.World.Read<Hover>(right).Pressed);
    }

    [Fact]
    public void RemovingPressableReleasesTheHeldPointerAndCapture()
    {
        var game = new GameInput();
        using var frames = new PointerFrames(game);
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new SingleButton());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity button = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.True(frames.World.Read<Hover>(button).Pressed);

        frames.World.Remove<Pressable>(button);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        frames.Frame();

        Assert.False(frames.World.Read<Hover>(button).Pressed);
        Assert.False(frames.World.Read<GamePointer>(game.Result).Captured);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(0u, frames.World.Read<Clicks>(button).Count);
    }

    [Fact]
    public void GameInputReadsPointerCaptureInTheSameFrame()
    {
        var game = new GameInput();
        using var frames = new PointerFrames(game);
        Views.Views.Spawn(frames.World, frames.Canvas, new SingleButton());
        frames.Frame();

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.True(frames.World.Read<GamePointer>(game.Result).Captured);

        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.False(frames.World.Read<GamePointer>(game.Result).Captured);
    }

    [Fact]
    public void TopmostDrawnPressableReceivesOverlappingClicks()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new OverlappingButtons());
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        var buttons = Hierarchy<ElementTree>.GetChildren(frames.World, container);
        Entity lower = buttons[0];
        Entity upper = buttons[1];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.True(frames.World.Read<Hover>(upper).Hovered);
        Assert.False(frames.World.Read<Hover>(lower).Hovered);
        Assert.Equal(1u, frames.World.Read<Clicks>(upper).Count);
        Assert.Equal(0u, frames.World.Read<Clicks>(lower).Count);
    }

    [Fact]
    public void ClickUpdatesTheCounterOnTheNextFrameAndHoverPreservesClickDependentOutput()
    {
        using var frames = new PointerFrames();
        Entity caption = frames.World.CreateEntity(new Caption("Count"));
        Entity button = Views.Views.Spawn(frames.World, frames.Canvas, new CounterButton(caption));
        frames.Frame();
        Assert.Equal(new ButtonText("Count", 0), frames.World.Read<ButtonText>(button));

        frames.World.Replace(caption, new Caption("Clicks"));
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        frames.Frame();
        Assert.True(frames.World.Read<Hover>(button).Hovered);
        Assert.Equal(new ButtonText("Count", 0), frames.World.Read<ButtonText>(button));

        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        Assert.Equal(new ButtonText("Count", 0), frames.World.Read<ButtonText>(button));
        frames.Frame();

        Assert.Equal(new ButtonText("Clicks", 1), frames.World.Read<ButtonText>(button));
        frames.Frame();
        Assert.Equal(new ButtonText("Clicks", 1), frames.World.Read<ButtonText>(button));
    }

    private readonly record struct CounterButton(Entity Caption) : IView, IComponent, ILayout
    {
        public BuildTask Build()
        {
            Pressable(Scope.Entity);
            ref Counter count = ref Scope.ReadWrite<Counter>(Scope.Entity);
            if (Scope.Entity.Clicked())
                count.Value++;
            // The caption is deliberately sampled through an untracked application read.
            Scope.Write<ButtonText>(Scope.Entity) = new ButtonText(Scope.World.Read<Caption>(Caption).Value, count.Value);
            return default;
        }

        public Size Measure(ref LayoutPass pass, in Constraints constraints) => constraints.Constrain(new Size(100, 50));
    }

    private readonly record struct OverlappingButtons() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            {
                Pressable(Absolute().Size(100, 50));
                Pressable(Absolute().Size(100, 50));
            }
            return default;
        }
    }

    private readonly record struct SingleButton() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Pressable(Absolute().Size(100, 50));
            return default;
        }
    }

    private readonly record struct SeparatedButtons() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            {
                Pressable(Absolute().Size(100, 50));
                Pressable(Absolute().Size(100, 50).Position(150, 0));
            }
            return default;
        }
    }

    private readonly record struct CompositionButtonScene() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Scope.View(new CompositionButton());
            return default;
        }
    }

    private readonly record struct CompositionButton() : IView, IComponent
    {
        public BuildTask Build()
        {
            Pressable(Scope.Entity);
            Absolute().Size(100, 50).Position(20, 10);
            return default;
        }
    }

    private readonly record struct NestedButtons() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Pressable(Absolute()).Content())
            {
                Pressable(Absolute().Size(100, 50));
                using (Pressable(Absolute().Size(100, 50)).Content())
                    Pressable(Absolute().Size(100, 50));
                Absolute().Size(100, 50);
            }
            return default;
        }
    }

    private readonly record struct MovingButton(float X) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Pressable(Absolute().Size(100, 50).Position(X, 0));
            return default;
        }
    }

    private sealed class GameInput : ISystem<ImmediateSystemContext>
    {
        private QueryHandle _capture;
        public Entity Result { get; private set; }

        public void OnCreate(ref ImmediateSystemContext context)
        {
            Result = context.World.CreateEntity(new GamePointer());
            _capture = context.World.Query(context.World.QueryDefinition().Read<InputCapture>());
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            bool captured = false;
            context.World.ExecuteQuery(_capture, ref captured, static (rows, ref value) =>
            {
                foreach (var row in rows.Rows)
                    value |= row.Read<InputCapture>().Pointer;
            });
            context.World.Replace(Result, new GamePointer(captured));
        }

        public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_capture);
    }

    private readonly record struct GamePointer(bool Captured) : IComponent;

    private readonly record struct Caption(string Value) : IComponent;
    private record struct Counter(int Value) : IComponent;
    private readonly record struct ButtonText(string Caption, int Count) : IComponent;
}

internal sealed class PointerFrames : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    public World World { get; } = new();
    public Entity Canvas { get; }
    public HitTestSystem HitTest { get; } = new();

    public PointerFrames(ISystem<ImmediateSystemContext>? gameInput = null)
    {
        Canvas = World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        _systems.Add(HitTest);
        if (gameInput is not null)
            _systems.Add(gameInput);
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
