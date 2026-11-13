using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Layout;
using SomeEngine.Job;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Runtime.Tests;

public sealed partial class GameFrameTests
{
    [Fact]
    public void Frame_description_follows_the_wiki_rebuild_schedule()
    {
        using var world = new World();
        using var frames = new GameFrameSystems(world);
        FrameStageDescription[] stages = frames.Describe();

        Assert.Equal(new[]
        {
            FrameStage.PlatformInput, FrameStage.InterfaceInput, FrameStage.GameInputAndSimulation, FrameStage.GameTransforms,
            FrameStage.ViewRebuild, FrameStage.InterfaceUpdate, FrameStage.Animation,
            FrameStage.TextMeasurement, FrameStage.Layout, FrameStage.Flip, FrameStage.Extraction,
        }, stages.Select(stage => stage.Stage));
        Assert.Equal(new[] { typeof(HitTestSystem), typeof(FocusNavigationSystem) }, stages[1].SystemTypes);
        Assert.Equal(new[] { typeof(TransformSystem) }, stages[3].SystemTypes);
        Assert.Equal(new[] { typeof(ViewRebuildSystem) }, stages[4].SystemTypes);
        Assert.Equal(typeof(TimelineSystem), stages[6].SystemTypes[0]);
        Assert.Equal(typeof(TimelineEndSystem), stages[6].SystemTypes[^1]);
        Assert.Equal(new[] { typeof(TextShapingSystem) }, stages[7].SystemTypes);
        Assert.Equal(new[] { typeof(LayoutSystem) }, stages[8].SystemTypes);
        Assert.Equal(new[] { typeof(FlipSystem) }, stages[9].SystemTypes);
    }

    [Fact]
    public void User_systems_and_animation_reach_layout_before_the_after_layout_stage()
    {
        using var world = new World();
        Entity canvas = world.Create(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas, new MovingElement());
        using var frames = new GameFrameSystems(world);
        frames.UpdateViews(0);
        Entity container = FirstChild(world, root);
        Entity node = FirstChild(world, container);
        Entity result = world.Create(new MeasuredRectangle());
        frames.AddInterfaceSystem(new ResizeElement(node));
        frames.AddFlipSystem(new ReadRectangle(node, result));
        var position = Channel<Vector2>.Of<AbsolutePosition>(static (ref AbsolutePosition value) => ref value.Value);
        PropertyAnimation.To(world, node, position, new Vector2(100, 0), Spec.Tween(1));

        frames.UpdateViews(0.5);

        Assert.Equal(new Rect(50, 0, 30, 40), world.ReadComponentData<MeasuredRectangle>(result).Value);
        Assert.Equal(new[] { typeof(ResizeElement) }, frames.Describe()[5].SystemTypes);
        Assert.Equal(new[] { typeof(FlipSystem), typeof(ReadRectangle) }, frames.Describe()[9].SystemTypes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_frame_preserves_presentation_when_layout_moves(bool jobs)
    {
        using var world = new World(execution: jobs ? new WorldJobs(JobSystem.Default) : null);
        Entity canvas = world.Create(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas, new MovingElement());
        using var frames = new GameFrameSystems(world);
        frames.UpdateViews(0);
        Entity container = FirstChild(world, root);
        Entity node = FirstChild(world, container);
        Flip.Enable(world, node, Spec.Tween(1));
        frames.UpdateViews(0);

        world.Set(node, new AbsolutePosition { Value = new Vector2(100, 0) });
        frames.UpdateViews(0);

        Assert.Equal(100, world.ReadComponentData<LayoutRect>(node).Value.X);
        Assert.Equal(Matrix3x2.CreateTranslation(-100, 0), world.ReadComponentData<PresentationOffset>(node).Matrix);
    }

    private static Entity FirstChild(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, parent);
        return children.Entities[0];
    }

    private readonly record struct MovingElement() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                Absolute().Size(20, 20).Position(0, 0);
            return default;
        }
    }

    private readonly record struct ResizeElement(Entity Node) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Box box = context.World.ReadComponentData<Box>(Node);
            box.Fixed = new Size(30, 40);
            context.World.Set(Node, box);
        }
    }

    private readonly record struct ReadRectangle(Entity Node, Entity Result) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) =>
            context.World.Set(Result, new MeasuredRectangle(context.World.ReadComponentData<LayoutRect>(Node).Value));
    }

    private readonly record struct MeasuredRectangle(Rect Value) : IComponent;
}
