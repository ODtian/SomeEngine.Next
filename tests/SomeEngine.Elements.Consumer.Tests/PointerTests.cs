using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
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
        Entity entity = world.Create();
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
        Entity otherCanvas = frames.World.Create(new Canvas { Size = new Size(400, 300), Scale = 2 });
        if (nested)
            Hierarchy<ElementTree>.SetParent(frames.World, otherCanvas, frames.Canvas);
        Entity first = Views.Views.Spawn(frames.World, frames.Canvas, new SingleButton());
        Entity second = Views.Views.Spawn(frames.World, otherCanvas, new SingleButton());
        frames.Frame();
        Entity firstContainer = ElementChildren.Read(frames.World, first)[0];
        Entity firstButton = ElementChildren.Read(frames.World, firstContainer)[0];
        Entity secondContainer = ElementChildren.Read(frames.World, second)[0];
        Entity secondButton = ElementChildren.Read(frames.World, secondContainer)[0];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(firstButton).Count);
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(secondButton).Count);

        Pointer.Move(frames.World, otherCanvas, new Vector2(10, 10));
        Pointer.Press(frames.World, otherCanvas, PointerButton.Primary);
        Pointer.Release(frames.World, otherCanvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(firstButton).Count);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(secondButton).Count);
        Query capture = frames.World.Query(new QueryDescription().With<InputCapture>());
        try
        {
            int count = capture.Count;
            Assert.Equal(1, count);
        }
        finally
        {

        }
    }

    [Fact]
    public void HitTestingUsesThePreviousFramesLayout()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new MovingButton(0));
        frames.Frame();
        Entity container = ElementChildren.Read(frames.World, root)[0];
        Entity button = ElementChildren.Read(frames.World, container)[0];

        frames.World.Set(root, new MovingButton(150));
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(button).Count);
        Assert.Equal(150, frames.World.ReadComponentData<LayoutRect>(button).Value.X);

        frames.Frame();
        Assert.False(frames.World.ReadComponentData<Hover>(button).Hovered);
        Assert.False(frames.World.ReadComponentData<Hover>(button).Within);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(160, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(2u, frames.World.ReadComponentData<Clicks>(button).Count);
        Assert.True(frames.World.ReadComponentData<Hover>(button).Within);
    }

    [Fact]
    public void DrawOrderSelectsTheDescendantAndClicksReachItsMarkedAncestors()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new NestedButtons());
        frames.Frame();
        Entity parent = ElementChildren.Read(frames.World, root)[0];
        var children = ElementChildren.Read(frames.World, parent);
        Entity branch = children[1];
        Entity button = ElementChildren.Read(frames.World, branch)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(button).Count);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(parent).Count);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(branch).Count);
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(children[0]).Count);
        Assert.True(frames.World.ReadComponentData<Hover>(button).Hovered);
        Assert.False(frames.World.ReadComponentData<Hover>(parent).Hovered);
        Assert.False(frames.World.ReadComponentData<Hover>(branch).Hovered);
        Assert.True(frames.World.ReadComponentData<Hover>(parent).Within);
        Assert.True(frames.World.ReadComponentData<Hover>(branch).Within);
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
        Entity parent = ElementChildren.Read(frames.World, root)[0];
        Entity branch = ElementChildren.Read(frames.World, parent)[1];
        Entity button = ElementChildren.Read(frames.World, branch)[0];
        Entity[] path = [button, branch, parent];
        Entity stop = path[stopIndex];
        frames.World.Add(stop, new StopsPointer());
        if (!pressable)
            frames.World.Remove<Pressable>(stop);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(button).Count);
        Assert.Equal(branchClicks, frames.World.ReadComponentData<Clicks>(branch).Count);
        Assert.Equal(parentClicks, frames.World.ReadComponentData<Clicks>(parent).Count);
    }

    [Fact]
    public void TransparentCompositionCanDeclarePressableOnItsOwnNode()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new CompositionButtonScene());
        frames.Frame();
        Entity container = ElementChildren.Read(frames.World, root)[0];
        Entity button = ElementChildren.Read(frames.World, container)[0];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(40, 20));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.True(frames.World.ReadComponentData<Hover>(button).Hovered);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(button).Count);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        frames.Frame();
        Assert.False(frames.World.ReadComponentData<Hover>(button).Hovered);
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
        Entity outerCard = ElementChildren.Read(frames.World, outerRoot)[0];
        Entity outerBranch = ElementChildren.Read(frames.World, outerCard)[1];
        Entity canvas = frames.World.Create(new Canvas { Size = new Size(400, 300) });
        frames.World.Add(canvas, new Hover());
        Hierarchy<ElementTree>.SetParent(frames.World, canvas, outerBranch);
        Entity root = Views.Views.Spawn(frames.World, canvas, new NestedButtons());
        frames.Frame();
        Entity card = ElementChildren.Read(frames.World, root)[0];
        Entity branch = ElementChildren.Read(frames.World, card)[1];
        Entity button = ElementChildren.Read(frames.World, branch)[0];

        Pointer.Move(frames.World, canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, canvas, PointerButton.Primary);
        Pointer.Release(frames.World, canvas, PointerButton.Primary);
        frames.Frame();

        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(button).Count);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(branch).Count);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(card).Count);
        Assert.True(frames.World.ReadComponentData<Hover>(canvas).Within);
        Assert.True(frames.World.ReadComponentData<Hover>(card).Within);
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(outerBranch).Count);
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(outerCard).Count);
        Assert.Equal(default, frames.World.ReadComponentData<Hover>(outerBranch));
        Assert.Equal(default, frames.World.ReadComponentData<Hover>(outerCard));
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
        Entity container = ElementChildren.Read(frames.World, root)[0];
        var buttons = ElementChildren.Read(frames.World, container);
        Entity left = buttons[0];
        Entity right = buttons[1];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, first);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(160, 10));
        Pointer.Press(frames.World, frames.Canvas, second);
        Pointer.Press(frames.World, frames.Canvas, first);
        frames.Frame();
        Assert.True(frames.World.ReadComponentData<Hover>(left).Pressed);
        Assert.True(frames.World.ReadComponentData<Hover>(right).Pressed);

        Pointer.Release(frames.World, frames.Canvas, first);
        frames.Frame();
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(right).Count);
        Assert.True(frames.World.ReadComponentData<Hover>(right).Pressed);

        Pointer.Release(frames.World, frames.Canvas, second);
        frames.Frame();
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(left).Count);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(right).Count);
        Assert.False(frames.World.ReadComponentData<Hover>(right).Pressed);
    }

    [Fact]
    public void RemovingPressableReleasesTheHeldPointerAndCapture()
    {
        var game = new GameInput();
        using var frames = new PointerFrames(game);
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new SingleButton());
        frames.Frame();
        Entity container = ElementChildren.Read(frames.World, root)[0];
        Entity button = ElementChildren.Read(frames.World, container)[0];
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.True(frames.World.ReadComponentData<Hover>(button).Pressed);

        frames.World.Remove<Pressable>(button);
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        frames.Frame();

        Assert.False(frames.World.ReadComponentData<Hover>(button).Pressed);
        Assert.False(frames.World.ReadComponentData<GamePointer>(game.Result).Captured);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(button).Count);
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
        Assert.True(frames.World.ReadComponentData<GamePointer>(game.Result).Captured);

        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.False(frames.World.ReadComponentData<GamePointer>(game.Result).Captured);
    }

    [Fact]
    public void TopmostDrawnPressableReceivesOverlappingClicks()
    {
        using var frames = new PointerFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new OverlappingButtons());
        frames.Frame();
        Entity container = ElementChildren.Read(frames.World, root)[0];
        var buttons = ElementChildren.Read(frames.World, container);
        Entity lower = buttons[0];
        Entity upper = buttons[1];

        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Assert.True(frames.World.ReadComponentData<Hover>(upper).Hovered);
        Assert.False(frames.World.ReadComponentData<Hover>(lower).Hovered);
        Assert.Equal(1u, frames.World.ReadComponentData<Clicks>(upper).Count);
        Assert.Equal(0u, frames.World.ReadComponentData<Clicks>(lower).Count);
    }

    [Fact]
    public void ClickUpdatesTheCounterOnTheNextFrameAndHoverPreservesClickDependentOutput()
    {
        using var frames = new PointerFrames();
        Entity caption = frames.World.Create(new Caption("Count"));
        Entity button = Views.Views.Spawn(frames.World, frames.Canvas, new CounterButton(caption));
        frames.Frame();
        Assert.Equal(new ButtonText("Count", 0), frames.World.ReadComponentData<ButtonText>(button));

        frames.World.Set(caption, new Caption("Clicks"));
        Pointer.Move(frames.World, frames.Canvas, new Vector2(10, 10));
        frames.Frame();
        Assert.True(frames.World.ReadComponentData<Hover>(button).Hovered);
        Assert.Equal(new ButtonText("Count", 0), frames.World.ReadComponentData<ButtonText>(button));

        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        Assert.Equal(new ButtonText("Count", 0), frames.World.ReadComponentData<ButtonText>(button));
        frames.Frame();

        Assert.Equal(new ButtonText("Clicks", 1), frames.World.ReadComponentData<ButtonText>(button));
        frames.Frame();
        Assert.Equal(new ButtonText("Clicks", 1), frames.World.ReadComponentData<ButtonText>(button));
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
            Scope.Write<ButtonText>(Scope.Entity) = new ButtonText(Scope.World.ReadComponentData<Caption>(Caption).Value, count.Value);
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
        private Query _capture;
        public Entity Result { get; private set; }

        public void OnCreate(ref ImmediateSystemContext context)
        {
            Result = context.World.Create(new GamePointer());
            _capture = context.World.Query(new QueryDescription().WithStored<InputCapture>());
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            bool captured = false;
            using (QueryScope rows = _capture.OpenRead())
                foreach (QueryChunk chunk in rows)
                    foreach (InputCapture value in chunk.Read<InputCapture>()) captured |= value.Pointer;
            context.World.Set(Result, new GamePointer(captured));
        }

        public void OnDestroy(ref ImmediateSystemContext context) { }
    }

    private readonly record struct GamePointer(bool Captured) : IComponent;

    private readonly record struct Caption(string Value) : IComponent;
    private record struct Counter(int Value) : IComponent, ITrackedComponent;
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
        Canvas = World.Create(new Canvas { Size = new Size(800, 600) });
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
