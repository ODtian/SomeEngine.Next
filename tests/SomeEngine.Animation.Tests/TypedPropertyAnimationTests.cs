using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Tests;

public sealed class TypedPropertyAnimationTests
{
    [Fact]
    public void CompleteTweenSpecDrivesAGeneratedReferenceField()
    {
        using var world = new World();
        var style = new PanelStyle();
        Entity target = world.CreateEntity(new FieldPanel { Style = style });
        var tween = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Radius, tween);
        using var systems = TweenSystems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Radius, 8f, tween);

        Tick(systems, world, 0.5);

        Assert.Equal(4f, style.Corner.Radius);
    }

    [Fact]
    public void RepeatedTargetAndParametersKeepTheTweenProgress()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var tween = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, tween);
        using var systems = TweenSystems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, tween);
        Tick(systems, world, 0.25);

        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, tween);
        Tick(systems, world, 0.25);

        Assert.Equal(0.5f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void ChangingOnlyParametersKeepsTheOriginalTweenStart()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var tween = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, tween);
        using var systems = TweenSystems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, tween);
        Tick(systems, world, 0.25);

        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, Spec.Tween<float, FloatOps, Linear>(2));
        Tick(systems, world, 0.25);

        Assert.Equal(0.25f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void TypedDisplacementReturnsToTheCapturedFieldValue()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel { Opacity = 3 });
        var tween = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, tween);
        using var systems = TweenSystems(world);
        PropertyAnimation.By(world, target, FieldPanelChannels.Opacity, 4f, tween);

        Tick(systems, world, 0.5);
        Assert.Equal(5f, world.Read<FieldPanel>(target).Opacity);
        Tick(systems, world, 0.5);

        Assert.Equal(3f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void TypedSpringRetargetKeepsVelocity()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var spring = Spec.Spring<float, FloatOps>();
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spring);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, SpringParameters, SpringState<float>, Spring<float, FloatOps>>());
        systems.Add(new TimelineEndSystem());
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, spring);
        Tick(systems, world, 1d / 60);

        PropertyAnimation.Retarget(world, target, FieldPanelChannels.Opacity, 2f);
        Tick(systems, world, 1d / 60);

        Assert.Equal(0.13907372f, world.Read<FieldPanel>(target).Opacity, 6);
    }

    [Fact]
    public void ManagedUserStateKeepsHistoryWhenRetargeted()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var history = new MotionHistory();
        var spec = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(history));
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        using var systems = HistorySystems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 100f, spec);
        Tick(systems, world, 0.1);

        PropertyAnimation.Retarget(world, target, FieldPanelChannels.Opacity, 200f);
        Tick(systems, world, 0.1);

        Assert.Equal(3f, world.Read<FieldPanel>(target).Opacity);
        Assert.Equal(1, history.Retargets);
    }

    [Fact]
    public void SwitchingDeclaredAlgorithmsReleasesThePreviousUserState()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var history = new MotionHistory();
        var spec = Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(history));
        var tween = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, tween);
        using var systems = HistorySystems(world);
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 100f, spec);
        Tick(systems, world, 0.1);

        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 2f, tween);

        Assert.Equal(1, history.Releases);
        Assert.Equal(1f, world.Read<FieldPanel>(target).Opacity);
    }

    [Fact]
    public void OneHundredControlsUseTheDeclaredEntityAndComponentStructure()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var tween = Spec.Tween<float, FloatOps, Linear>(1);
        var spring = Spec.Spring<float, FloatOps>();
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, tween);
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spring);
        using var changes = world.ObserveEntityTables();
        changes.Accept(changes.Read());
        int count = world.EntityCount;
        using var systems = TweenSystems(world);
        for (int repeat = 0; repeat < 100; repeat++)
        {
            PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, tween);
            Tick(systems, world, 0.1);
            PropertyAnimation.Retarget(world, target, FieldPanelChannels.Opacity, 2f);
            PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 3f, spring);
            PropertyAnimation.Stop(world, target);
        }

        Assert.Equal(count, world.EntityCount);
        Assert.Equal(0, changes.Read().Count);
    }

    [Fact]
    public void CompletingAPooledTypedInstanceAllowsTheNextTargetToReuseIt()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new FieldPanel());
        Entity second = world.CreateEntity(new FieldPanel());
        var tween = Spec.Tween<float, FloatOps, Linear>(1);
        using var systems = TweenSystems(world);
        Entity animation = PropertyAnimation.To(world, first, FieldPanelChannels.Opacity, 1f, tween);
        Tick(systems, world, 1);

        Entity reused = PropertyAnimation.To(world, second, FieldPanelChannels.Opacity, 2f, tween);
        Tick(systems, world, 1);

        Assert.Equal(animation, reused);
        Assert.Equal(2f, world.Read<FieldPanel>(second).Opacity);
    }

    [Fact]
    public void AnExternalMotionCompletesWhenItsDeclaredBoundaryIsAdvanced()
    {
        using var world = new World();
        Entity target = world.CreateEntity(new FieldPanel());
        var spec = Spec.Create<float, FloatOps, ExternalParameters, ExternalState, ExternalMotion>(new());
        PropertyAnimation.Declare(world, target, FieldPanelChannels.Opacity, spec);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, ExternalParameters, ExternalState, ExternalMotion>());
        systems.Add(new TimelineEndSystem());
        PropertyAnimation.To(world, target, FieldPanelChannels.Opacity, 1f, spec);
        Tick(systems, world, 0.25);
        Assert.True(world.IsEnabled<Timeline>(target));

        Animation.Advance(world, target);
        Tick(systems, world, 0);

        Assert.Equal(1u, world.Read<Timeline>(target).Completions);
    }

    private static SystemGroup<ImmediateSystemContext> TweenSystems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>());
        systems.Add(new TimelineEndSystem());
        return systems;
    }

    private static SystemGroup<ImmediateSystemContext> HistorySystems(World world)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>());
        systems.Add(new TimelineEndSystem());
        return systems;
    }

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double delta)
    {
        var frame = new ImmediateSystemContext(world, 0, 0, delta);
        systems.Update(ref frame);
    }

    private sealed class MotionHistory
    {
        public int Retargets;
        public int Releases;
    }

    private readonly record struct HistoryParameters(MotionHistory History);
    private struct HistoryState
    {
        public MotionHistory History;
        public int Samples;
    }

    private readonly struct HistoryMotion : IMotion<float, FloatOps, HistoryParameters, HistoryState>
    {
        public static MotionBoundary Describe(in HistoryParameters parameters) => new(StageEnd.Settled);
        public static void Initialize(in HistoryParameters parameters, in MotionValues<float> values, ref HistoryState state) => state = new() { History = parameters.History };
        public static MotionSample<float> Evaluate(in HistoryParameters parameters, in MotionTime time, in MotionValues<float> values, ref HistoryState state) => new(values.Current + ++state.Samples, false);
        public static void Retarget(in HistoryParameters previous, in HistoryParameters next, in MotionValues<float> values, ref HistoryState state) => state.History.Retargets++;
        public static void Release(ref HistoryState state) { state.History.Releases++; state = default; }
    }

    private struct ExternalParameters;
    private struct ExternalState;
    private readonly struct ExternalMotion : IMotion<float, FloatOps, ExternalParameters, ExternalState>
    {
        public static MotionBoundary Describe(in ExternalParameters parameters) => new(StageEnd.External);
        public static void Initialize(in ExternalParameters parameters, in MotionValues<float> values, ref ExternalState state) { }
        public static MotionSample<float> Evaluate(in ExternalParameters parameters, in MotionTime time, in MotionValues<float> values, ref ExternalState state) => new(values.To, false);
        public static void Retarget(in ExternalParameters previous, in ExternalParameters next, in MotionValues<float> values, ref ExternalState state) { }
    }
}
