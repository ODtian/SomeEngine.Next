using SomeEngine.Animation;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Views.Tests;

public static partial class TypedAnimationChecks
{
    public static void GeneratedFieldTargetsAnimateThroughTypedSpecs()
    {
        using var frames = new TypedFrames();
        var target = new State<float>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new FieldTargets(target, new State<int>()));
        frames.Frame(0);
        Entity panel = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];

        target.Value = 10;
        frames.Frame(0);
        frames.Frame(0.5f);

        Require(frames.World.Read<AnimatedPanel>(panel).Opacity == 5, "The ordinary field retains the expected animation progress.");
    }

    public static void EqualTargetsPreserveTheirElapsedProgress()
    {
        using var frames = new TypedFrames();
        var target = new State<float>();
        var revision = new State<int>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new FieldTargets(target, revision));
        frames.Frame(0);
        Entity panel = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        target.Value = 10;
        frames.Frame(0);
        frames.Frame(0.25f);

        revision.Value++;
        frames.Frame(0.25f);

        Require(frames.World.Read<AnimatedPanel>(panel).Opacity == 5, "The ordinary field retains the expected animation progress.");
    }

    public static void ReferencedStateReceivesOnlyThePresentedValues()
    {
        using var frames = new TypedFrames();
        var target = new State<float>();
        var presented = new State<float>();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ReferenceTargets(target, presented));
        frames.Frame(0);
        Entity panel = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        uint version = frames.World.WriteVersion<AnimatedPanel>(panel);

        target.Value = 10;
        frames.Frame(0);
        Require(presented.Value == 0, "The target submission preserves the current referenced presentation.");
        frames.Frame(0.5f);

        Require(presented.Value == 5, "The consumer receives the expected intermediate value.");
        Require(version != frames.World.WriteVersion<AnimatedPanel>(panel), "The referenced field advances the root write version.");
        Require(ReferenceEquals(presented, frames.World.Read<AnimatedPanel>(panel).Progress), "The referenced state keeps its identity.");
    }

    public static void AReversedReferenceTargetContinuesFromItsPresentedValue()
    {
        using var frames = new TypedFrames();
        var target = new State<float>();
        var presented = new State<float>();
        Views.Spawn(frames.World, Entity.Null, new ReferenceTargets(target, presented));
        frames.Frame(0);
        target.Value = 10;
        frames.Frame(0);
        frames.Frame(0.5f);

        target.Value = 0;
        frames.Frame(0);
        frames.Frame(0.5f);

        Require(presented.Value == 2.5f, "The reverse target continues from the presented value.");
    }

    public static void AParameterUpdateUsesTheExistingElapsedTime()
    {
        using var frames = new TypedFrames();
        var target = new State<float>();
        var duration = new State<float>(1);
        var presented = new State<float>();
        Views.Spawn(frames.World, Entity.Null, new ParameterTargets(target, duration, presented));
        frames.Frame(0);
        target.Value = 10;
        frames.Frame(0);
        frames.Frame(0.25f);

        duration.Value = 0.5f;
        frames.Frame(0.25f);

        Require(presented.Value == 10, "The parameter update uses the elapsed timeline time.");
    }

    public static void AConsumerMotionUsesItsManagedHistoryThroughAnimate()
    {
        using var frames = new TypedFrames();
        frames.Add(Spec.Create<float, FloatOps, NamedParameters, NamedState, NamedMotion>(new(1, "xx")));
        var target = new State<float>();
        var presented = new State<float>();
        Views.Spawn(frames.World, Entity.Null, new ConsumerTargets(target, presented));
        frames.Frame(0);
        target.Value = 10;
        frames.Frame(0);
        frames.Frame(0.25f);

        Require(presented.Value == 5, "The consumer receives the expected intermediate value.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class TypedFrames : IDisposable
    {
        internal World World { get; } = new();
        private readonly SystemGroup<ImmediateSystemContext> _builds;
        private readonly SystemGroup<ImmediateSystemContext> _animations;

        internal TypedFrames()
        {
            _builds = new(new ImmediateSystemDriver(World));
            _builds.Add(new ViewRebuildSystem());
            _animations = new(new ImmediateSystemDriver(World));
            AnimationSystems.Add(_animations, Spec.Tween<float, FloatOps, Linear>(1));
        }

        internal void Frame(float delta)
        {
            _builds.Update();
            var frame = new ImmediateSystemContext(World, 0, 0, delta);
            _animations.Update(ref frame);
        }

        internal void Add<TParameters, TState, TEvaluator>(Spec<float, FloatOps, TParameters, TState, TEvaluator> spec)
            where TParameters : struct
            where TState : struct
            where TEvaluator : struct, IMotion<float, FloatOps, TParameters, TState> =>
            AnimationSystems.Add(_animations, spec);

        public void Dispose()
        {
            _animations.Dispose();
            _builds.Dispose();
            World.Dispose();
        }
    }

    private readonly record struct FieldTargets(State<float> Target, State<int> Revision) : IView, IComponent
    {
        public BuildTask Build()
        {
            _ = Revision.Value;
            Scope.View(new AnimatedPanel { Opacity = Target.Value }, 0)
                .Animate(AnimatedPanelChannels.Opacity, Spec.Tween<float, FloatOps, Linear>(1));
            return default;
        }
    }

    private readonly record struct ReferenceTargets(State<float> Target, State<float> Presented) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity panel = Scope.View(new AnimatedPanel { Progress = Presented }, 0)
                .Animate(AnimatedPanelChannels.ProgressValue, Spec.Tween<float, FloatOps, Linear>(1));
            Scope.Set(panel, AnimatedPanelChannels.ProgressValue, Target.Value);
            return default;
        }
    }

    private readonly record struct ParameterTargets(State<float> Target, State<float> Duration, State<float> Presented) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity panel = Scope.View(new AnimatedPanel { Progress = Presented }, 0)
                .Animate(AnimatedPanelChannels.ProgressValue, Spec.Tween<float, FloatOps, Linear>(Duration.Value));
            Scope.Set(panel, AnimatedPanelChannels.ProgressValue, Target.Value);
            return default;
        }
    }

    private readonly record struct ConsumerTargets(State<float> Target, State<float> Presented) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity panel = Scope.View(new AnimatedPanel { Progress = Presented }, 0)
                .Animate(AnimatedPanelChannels.ProgressValue,
                    Spec.Create<float, FloatOps, NamedParameters, NamedState, NamedMotion>(new(1, "xx")));
            Scope.Set(panel, AnimatedPanelChannels.ProgressValue, Target.Value);
            return default;
        }
    }

    private readonly record struct NamedParameters(float Duration, string Name);
    private struct NamedState { internal string? Name; }
    private readonly struct NamedMotion : IMotion<float, FloatOps, NamedParameters, NamedState>
    {
        public static MotionBoundary Describe(in NamedParameters parameters) => new(StageEnd.Duration, parameters.Duration);
        public static void Initialize(in NamedParameters parameters, in MotionValues<float> values, ref NamedState state) => state.Name = parameters.Name;
        public static void Retarget(in NamedParameters previous, in NamedParameters next, in MotionValues<float> values, ref NamedState state) { }
        public static MotionSample<float> Evaluate(in NamedParameters parameters, in MotionTime time, in MotionValues<float> values, ref NamedState state) =>
            new(time.Time >= parameters.Duration ? values.To : values.From + (values.To - values.From) * time.Time / parameters.Duration * state.Name!.Length,
                time.Time >= parameters.Duration);
    }
}

[GenerateFields]
[ComponentField("Progress.Value", Name = "ProgressValue")]
internal struct AnimatedPanel : IView, IComponent, IEquatable<AnimatedPanel>
{
    public float Opacity;
    public State<float> Progress;
    public BuildTask Build() => default;
    public bool Equals(AnimatedPanel other) => Opacity == other.Opacity && ReferenceEquals(Progress, other.Progress);
}
