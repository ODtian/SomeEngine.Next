using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation;

public readonly struct Spec
{
    public readonly float Duration;
    public readonly Curve Curve;

    public static unsafe Spec<T, TOps, FunctionTweenParameters, TweenState, FunctionTween<T, TOps>> FromFunction<T, TOps>(
        float duration, delegate* managed<float, float> function)
        where T : unmanaged
        where TOps : struct, IValueOps<T> => new(new FunctionTweenParameters(duration, function));

    public static Spec<T, TOps, TParameters, TState, TEvaluator> Create<T, TOps, TParameters, TState, TEvaluator>(
        in TParameters parameters)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState> => new(in parameters);

    public static Spec<T, TOps, TweenParameters, TweenState, Tween<T, TOps, TEasing>> Tween<T, TOps, TEasing>(float duration)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TEasing : struct, IEasing => new(new TweenParameters(duration));

    public static Spec<T, TOps, SpringParameters, SpringState<T>, Spring<T, TOps>> Spring<T, TOps>(
        float stiffness = 180, float damping = 20, float tolerance = 0.00001f, float step = 1f / 120)
        where T : unmanaged
        where TOps : struct, IValueOps<T> => new(new SpringParameters(stiffness, damping, tolerance, step));

    public static Spec<T, TOps, KeyframeParameters, KeyframeState, Keyframes<T, TOps>> Keyframes<T, TOps>(
        float duration, ReadOnlyMemory<Keyframe> keys)
        where T : unmanaged
        where TOps : struct, IValueOps<T> => new(new KeyframeParameters(duration, keys));

    public Spec(float duration, in Curve curve)
    {
        DevelopmentCheck.Require(float.IsFinite(duration) && duration >= 0, "Animation duration must be finite and nonnegative.");
        Duration = duration;
        Curve = curve;
    }

    public static Spec Tween(float duration, EasingType easing = EasingType.Linear) =>
        new(duration, new Curve { Kind = easing == EasingType.Linear ? CurveKind.Linear : CurveKind.Easing, Easing = easing });

    /// <summary>Samples an instance keyframe range; count zero uses the remainder of the buffer.</summary>
    public static Spec Keyframes(float duration, int start = 0, int count = 0)
    {
        DevelopmentCheck.Require(start >= 0 && count >= 0, "Keyframe range values must be nonnegative.");
        return new(duration, new Curve { Kind = CurveKind.Keyframes, KeyframeStart = start, KeyframeCount = count });
    }

    public static Spec Spring(float stiffness = 180, float damping = 20, float tolerance = 0.00001f)
    {
        DevelopmentCheck.Require(float.IsFinite(stiffness) && stiffness > 0, "Spring stiffness must be finite and positive.");
        DevelopmentCheck.Require(float.IsFinite(damping) && damping >= 0, "Spring damping must be finite and nonnegative.");
        DevelopmentCheck.Require(float.IsFinite(tolerance) && tolerance > 0, "Spring tolerance must be finite and positive.");
        return new(0, new Curve { Kind = CurveKind.Spring, Stiffness = stiffness, Damping = damping, Tolerance = tolerance });
    }
}

/// <summary>Parameters with their value, operations, history, and evaluator types preserved.</summary>
public readonly struct Spec<T, TOps, TParameters, TState, TEvaluator>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    public Spec(in TParameters parameters) => Parameters = parameters;

    public TParameters Parameters { get; }
    public MotionBoundary Boundary => TEvaluator.Describe(Parameters);
}
