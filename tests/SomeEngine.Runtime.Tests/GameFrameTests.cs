using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.Math;
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
            FrameStage.PlatformInput, FrameStage.InterfaceInput, FrameStage.GameInputAndSimulation,
            FrameStage.ViewRebuild, FrameStage.InterfaceUpdate, FrameStage.Animation,
            FrameStage.TextMeasurement, FrameStage.Layout, FrameStage.Flip, FrameStage.Extraction,
        }, stages.Select(stage => stage.Stage));
        Assert.Equal(new[] { typeof(HitTestSystem), typeof(FocusNavigationSystem) }, stages[1].SystemTypes);
        Assert.Equal(new[] { typeof(ViewRebuildSystem) }, stages[3].SystemTypes);
        Assert.Equal(typeof(TimelineSystem), stages[5].SystemTypes[0]);
        Assert.Equal(typeof(TimelineEndSystem), stages[5].SystemTypes[^1]);
        Assert.Equal(new[] { typeof(TextShapingSystem) }, stages[6].SystemTypes);
        Assert.Equal(new[] { typeof(LayoutSystem) }, stages[7].SystemTypes);
        Assert.Equal(new[] { typeof(FlipSystem) }, stages[8].SystemTypes);
    }

    [Fact]
    public void User_systems_and_animation_reach_layout_before_the_after_layout_stage()
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas, new MovingElement());
        using var frames = new GameFrameSystems(world);
        frames.UpdateViews(0);
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        Entity result = world.CreateEntity(new MeasuredRectangle());
        frames.AddInterfaceSystem(new ResizeElement(node));
        frames.AddFlipSystem(new ReadRectangle(node, result));
        var position = Channel<Vector2>.Of<AbsolutePosition>(static (ref AbsolutePosition value) => ref value.Value);
        PropertyAnimation.To(world, node, position, new Vector2(100, 0), Spec.Tween(1));

        frames.UpdateViews(0.5);

        Assert.Equal(new Rect(50, 0, 30, 40), world.Read<MeasuredRectangle>(result).Value);
        Assert.Equal(new[] { typeof(ResizeElement) }, frames.Describe()[4].SystemTypes);
        Assert.Equal(new[] { typeof(FlipSystem), typeof(ReadRectangle) }, frames.Describe()[8].SystemTypes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_frame_preserves_presentation_when_layout_moves(bool jobs)
    {
        using var world = new World(execution: jobs ? new WorldJobs(JobSystem.Default) : null);
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(world, canvas, new MovingElement());
        using var frames = new GameFrameSystems(world);
        frames.UpdateViews(0);
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Entity node = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        Flip.Enable(world, node, Spec.Tween(1));
        frames.UpdateViews(0);

        world.Replace(node, new AbsolutePosition { Value = new Vector2(100, 0) });
        frames.UpdateViews(0);

        Assert.Equal(100, world.Read<LayoutRect>(node).Value.X);
        Assert.Equal(Matrix3x2.CreateTranslation(-100, 0), world.Read<PresentationOffset>(node).Matrix);
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
            Box box = context.World.Read<Box>(Node);
            box.Fixed = new Size(30, 40);
            context.World.Replace(Node, box);
        }
    }

    private readonly record struct ReadRectangle(Entity Node, Entity Result) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) =>
            context.World.Replace(Result, new MeasuredRectangle(context.World.Read<LayoutRect>(Node).Value));
    }

    private readonly record struct MeasuredRectangle(Rect Value) : IComponent;
}
