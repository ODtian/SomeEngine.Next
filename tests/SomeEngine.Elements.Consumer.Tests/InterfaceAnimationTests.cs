using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class InterfaceAnimationTests
{
    [Fact]
    public void UserValuesAndInlineInterfaceAnimationsPassTheSharedChecks() => InterfaceAnimationChecks.Run();

    private static readonly Channel<Color> ParameterFill = Channel<Color>.Of<ColorNode>(static (ref ColorNode node) => ref node.Fill);

    [Fact]
    public void FlipContinuesAcrossBoundsTransformAndOpacityChanges()
    {
        using var frames = new InterfaceFrames();
        Entity node = frames.World.Create(new LayoutRect { Value = new Rect(10, 20, 80, 20) });
        frames.World.Add(node, new ElementTransform { Matrix = Matrix3x2.CreateTranslation(3, 4), Alpha = 0.8f });
        Flip.Enable(frames.World, node, Spec.Tween(1));
        frames.Frame(0);

        frames.World.Set(node, new LayoutRect { Value = new Rect(100, 50, 160, 40) });
        var user = new ElementTransform { Matrix = Matrix3x2.CreateScale(2), Alpha = 0.4f };
        frames.World.Set(node, user);
        frames.Frame(0);
        Assert.Equal(new Vector2(13, 24), Present(Vector2.Zero));
        Assert.Equal(new Vector2(93, 44), Present(new Vector2(160, 40)));
        Assert.Equal(0.8f, Opacity());

        frames.Frame(0.5f);
        Assert.Equal(new Vector2(56.5f, 37), Present(Vector2.Zero));
        Assert.Equal(new Vector2(256.5f, 87), Present(new Vector2(160, 40)));
        Assert.Equal(0.6f, Opacity(), precision: 6);
        frames.Frame(0.5f);
        Assert.Equal(PresentationOffset.Identity, frames.World.ReadComponentData<PresentationOffset>(node));
        Assert.Equal(user, frames.World.ReadComponentData<ElementTransform>(node));
        Assert.Equal(new Vector2(100, 50), Present(Vector2.Zero));
        Assert.Equal(0.4f, Opacity());

        Vector2 Present(Vector2 local) => Vector2.Transform(
            Vector2.Transform(local, frames.World.ReadComponentData<ElementTransform>(node).Matrix) + new Vector2(100, 50),
            frames.World.ReadComponentData<PresentationOffset>(node).Matrix);
        float Opacity() => frames.World.ReadComponentData<ElementTransform>(node).Alpha * frames.World.ReadComponentData<PresentationOffset>(node).Alpha;
    }

    [Fact]
    public void ChangingAnAnimatedParameterPreservesItsPresentedValueAndWriteVersion()
    {
        using var world = new World();
        using var builds = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        builds.Add(new ViewRebuildSystem());
        using var animations = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(animations);
        var target = new State<Color>(new Color(1, 0, 0));
        Entity root = Views.Views.Spawn(world, Entity.Null, new ColorParent(target, new State<int>(), true));
        builds.Update();
        Entity panel = Assert.Single(ElementChildren.Read(world, root));
        ulong version = world.ComponentDataWriteVersion<ColorNode>(panel);
        target.Value = new Color(0, 0, 1);
        builds.Update();
        Assert.Equal(new Color(1, 0, 0), world.ReadComponentData<ColorNode>(panel).Fill);
        Assert.Equal(version, world.ComponentDataWriteVersion<ColorNode>(panel));
        var frame = new ImmediateSystemContext(world, 0, 0, 0.5);
        animations.Update(ref frame);
        Assert.Equal(new Color(0.5f, 0, 0.5f), world.ReadComponentData<ColorNode>(panel).Fill);
        Assert.NotEqual(version, world.ComponentDataWriteVersion<ColorNode>(panel));
    }

    [Fact]
    public void FlipUsesThePrecedingFrameOfTheSameEntity()
    {
        using var frames = new InterfaceFrames();
        Entity node = frames.World.Create(new LayoutRect { Value = new Rect(0, 0, 80, 20) });
        Flip.Enable(frames.World, node, Spec.Tween(1));
        frames.Frame(0);
        frames.World.Remove<LayoutRect>(node);
        frames.Frame(0);
        frames.World.Add(node, new LayoutRect { Value = new Rect(100, 0, 80, 20) });
        frames.Frame(0);
        Assert.Equal(PresentationOffset.Identity, frames.World.ReadComponentData<PresentationOffset>(node));

        frames.World.Destroy(node);
        Entity replacement = frames.World.Create(new LayoutRect { Value = new Rect(200, 0, 80, 20) });
        Flip.Enable(frames.World, replacement, Spec.Tween(1));
        frames.Frame(0);
        Assert.Equal(PresentationOffset.Identity, frames.World.ReadComponentData<PresentationOffset>(replacement));
    }

    [Fact]
    public void AFailedParentBuildKeepsThePreviouslyCommittedAnimationTarget()
    {
        using var frames = new InterfaceFrames();
        var target = new State<Color>(new Color(1, 0, 0));
        var fail = new State<bool>();
        Entity root = frames.Spawn(new RejectingParent(target, fail));
        frames.Frame(0);
        Entity panel = Assert.Single(ElementChildren.Read(frames.World, root));

        target.Value = new Color(0, 0, 1);
        fail.Value = true;
        frames.Frame(0);
        Assert.True(frames.World.HasSparse<BuildError>(root));
        frames.Frame(0.5f);
        Assert.Equal(new Color(1, 0, 0), frames.World.ReadComponentData<ColorNode>(panel).Fill);

        fail.Value = false;
        frames.Frame(0);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.5f, 0, 0.5f), frames.World.ReadComponentData<ColorNode>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0, 0, 1), frames.World.ReadComponentData<ColorNode>(panel).Fill);
    }

    [Fact]
    public void InterfaceFillAndFlipContinueWhileGameAnimationIsPaused()
    {
        using var frames = new InterfaceFrames();
        var hovered = new State<bool>();
        var reversed = new State<bool>();
        Entity root = frames.Spawn(new HoverPanel(hovered));
        Entity list = frames.Spawn(new ReorderedRows(reversed));
        frames.Frame(0);
        Entity panel = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity column = Assert.Single(ElementChildren.Read(frames.World, list));
        Entity row = ElementChildren.Read(frames.World, column)[0];
        Entity game = frames.World.Create(new Surface { Fill = new Color(1, 0, 0) });
        PropertyAnimation.Declare<Color>(frames.World, game);
        PropertyAnimation.To(frames.World, game, SurfaceChannels.Fill, new Color(0, 0, 1), Spec.Tween(1));

        hovered.Value = true;
        reversed.Value = true;
        frames.Frame(0, gamePaused: true);
        frames.Frame(0.5f, gamePaused: true);
        Assert.Equal(new Color(0.5f, 0, 0.5f), frames.World.ReadComponentData<Surface>(panel).Fill);
        Assert.Equal(Matrix3x2.CreateTranslation(0, -10), frames.World.ReadComponentData<PresentationOffset>(row).Matrix);
        Assert.Equal(new Color(1, 0, 0), frames.World.ReadComponentData<Surface>(game).Fill);

        frames.Frame(0.5f, gamePaused: true);
        Assert.Equal(new Color(0, 0, 1), frames.World.ReadComponentData<Surface>(panel).Fill);
        Assert.Equal(PresentationOffset.Identity, frames.World.ReadComponentData<PresentationOffset>(row));
        Assert.Equal(new Color(1, 0, 0), frames.World.ReadComponentData<Surface>(game).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.5f, 0, 0.5f), frames.World.ReadComponentData<Surface>(game).Fill);
    }

    [Fact]
    public void ReorderedRowsMoveThroughPresentationOffsetsAndKeepTheirUserTransforms()
    {
        using var frames = new InterfaceFrames();
        var reversed = new State<bool>();
        Entity root = frames.Spawn(new ReorderedRows(reversed));
        frames.Frame(0);
        Entity column = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity first = ElementChildren.Read(frames.World, column)[0];
        Entity second = ElementChildren.Read(frames.World, column)[1];
        ElementTransform user = frames.World.ReadComponentData<ElementTransform>(first);
        Assert.Equal(new Rect(0, 0, 80, 20), frames.World.ReadComponentData<LayoutRect>(first).Value);
        Assert.Equal(PresentationOffset.Identity, frames.World.ReadComponentData<PresentationOffset>(first));

        reversed.Value = true;
        frames.Frame(0);
        Assert.Equal(new[] { second, first }, ElementChildren.Read(frames.World, column));
        Assert.Equal(new Rect(0, 20, 80, 20), frames.World.ReadComponentData<LayoutRect>(first).Value);
        Assert.Equal(Matrix3x2.CreateTranslation(0, -20), frames.World.ReadComponentData<PresentationOffset>(first).Matrix);
        Assert.Equal(Matrix3x2.CreateTranslation(0, 20), frames.World.ReadComponentData<PresentationOffset>(second).Matrix);
        Assert.Equal(user, frames.World.ReadComponentData<ElementTransform>(first));

        frames.Frame(0.5f);
        Assert.Equal(Matrix3x2.CreateTranslation(0, -10), frames.World.ReadComponentData<PresentationOffset>(first).Matrix);
        Assert.Equal(user, frames.World.ReadComponentData<ElementTransform>(first));
        frames.Frame(0.5f);
        Assert.Equal(PresentationOffset.Identity, frames.World.ReadComponentData<PresentationOffset>(first));
        Assert.Equal(user, frames.World.ReadComponentData<ElementTransform>(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentRerunsWithTheSameTargetKeepAnimationProgress(bool parameter)
    {
        using var frames = new InterfaceFrames();
        var target = new State<Color>(new Color(1, 0, 0));
        var revision = new State<int>();
        Entity root = frames.Spawn(new ColorParent(target, revision, parameter));
        frames.Frame(0);
        Entity panel = Assert.Single(ElementChildren.Read(frames.World, root));

        target.Value = new Color(0, 0, 1);
        frames.Frame(0);
        Assert.Equal(new Color(1, 0, 0), ReadColor());
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), ReadColor());

        revision.Value++;
        frames.Frame(0);
        Assert.Equal(new Color(0.75f, 0, 0.25f), ReadColor());
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.5f, 0, 0.5f), ReadColor());
        frames.Frame(0.5f);
        Assert.Equal(new Color(0, 0, 1), ReadColor());

        Color ReadColor() => parameter ? frames.World.ReadComponentData<ColorNode>(panel).Fill : frames.World.ReadComponentData<Surface>(panel).Fill;
    }

    [Fact]
    public void HoveredFillTransitionsAndReversesFromItsPresentedColor()
    {
        using var frames = new InterfaceFrames();
        var hovered = new State<bool>();
        Entity root = frames.Spawn(new HoverPanel(hovered));
        frames.Frame(0);
        Entity panel = Assert.Single(ElementChildren.Read(frames.World, root));
        Assert.Equal(new Color(1, 0, 0), frames.World.ReadComponentData<Surface>(panel).Fill);
        var timelines = frames.World.Query(new QueryDescription().WithStored<Timeline>());
        ulong members = timelines.MembershipVersion;
        using var tables = frames.World.WatchMembership(timelines);
        int entities = frames.World.EntityCount;

        hovered.Value = true;
        frames.Frame(0);
        Assert.Equal(new Color(1, 0, 0), frames.World.ReadComponentData<Surface>(panel).Fill);
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.ReadComponentData<Surface>(panel).Fill);
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.5f, 0, 0.5f), frames.World.ReadComponentData<Surface>(panel).Fill);

        hovered.Value = false;
        frames.Frame(0);
        Assert.Equal(new Color(0.5f, 0, 0.5f), frames.World.ReadComponentData<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.ReadComponentData<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(1, 0, 0), frames.World.ReadComponentData<Surface>(panel).Fill);
        Assert.Equal(entities, frames.World.EntityCount);
        Assert.Equal(members, timelines.MembershipVersion);
        using var membership = tables.Open();
        Assert.Equal(0, membership.Count);

    }

    private readonly record struct HoverPanel(State<bool> Hovered) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Absolute(), 1)
                .Background(Hovered.Value ? new Color(0, 0, 1) : new Color(1, 0, 0))
                .Animate(SurfaceChannels.Fill, Spec.Tween(1));
            return default;
        }
    }

    private readonly record struct ColorParent(State<Color> Target, State<int> Revision, bool Parameter) : IView, IComponent
    {
        public BuildTask Build()
        {
            _ = Revision.Value;
            if (Parameter)
                Scope.View(new ColorNode { Fill = Target.Value }, 1).Animate(ParameterFill, Spec.Tween(1));
            else
                Scope.View(new Absolute(), 1).Background(Target.Value).Animate(SurfaceChannels.Fill, Spec.Tween(1));
            return default;
        }
    }

    private record struct ColorNode : IView, IComponent
    {
        public Color Fill;
        public BuildTask Build() => default;
    }

    private readonly record struct RejectingParent(State<Color> Target, State<bool> Fail) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new ColorNode { Fill = Target.Value }, 1).Animate(ParameterFill, Spec.Tween(1));
            if (Fail.Value) throw new InvalidOperationException("The example rejects this build.");
            return default;
        }
    }

    private readonly record struct ReorderedRows(State<bool> Reversed) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Flex(Axis.Vertical), 1).Content())
            {
                for (int index = 0; index < 2; index++)
                {
                    int row = Reversed.Value ? 1 - index : index;
                    Entity node = Scope.View(new Absolute(), (ulong)row + 10).Size(80, 20);
                    Scope.Write<ElementTransform>(node) = new ElementTransform
                    {
                        Matrix = Matrix3x2.CreateTranslation(3, 4),
                        Alpha = 0.8f,
                    };
                    Flip.Enable(node, Spec.Tween(1));
                }
            }
            return default;
        }
    }
}

internal sealed class InterfaceFrames : IDisposable
{
    private readonly SystemGroup<ImmediateSystemContext> _systems;
    private readonly Entity _time;
    public World World { get; } = new();
    public Entity Canvas { get; }

    public InterfaceFrames()
    {
        Canvas = World.Create(new Canvas { Size = new Size(800, 600) });
        _time = World.Create(new AnimationTime(0));
        _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        _systems.Add(new ViewRebuildSystem());
        AnimationSystems.AddDefaults(_systems, _time);
        _systems.Add(new LayoutSystem());
        _systems.Add(new FlipSystem());
    }

    public Entity Spawn<T>(in T view) where T : struct, IView, IEquatable<T> =>
        Views.Views.Spawn(World, Canvas, in view);

    public void Frame(float delta, bool gamePaused = false)
    {
        World.Set(_time, new AnimationTime(delta, gamePaused: gamePaused));
        _systems.Update();
    }

    public void Dispose()
    {
        _systems.Dispose();
        World.Dispose();
    }
}
