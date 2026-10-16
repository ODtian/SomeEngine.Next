using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation;

public readonly record struct TweenParameters
{
    public TweenParameters(float duration)
    {
        DevelopmentCheck.Require(float.IsFinite(duration) && duration >= 0, "Tween duration must be finite and nonnegative.");
        Duration = duration;
    }

    public float Duration { get; }
}

public struct TweenState { }

public readonly struct Tween<T, TOps, TEasing> : IMotion<T, TOps, TweenParameters, TweenState>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TEasing : struct, IEasing
{
    public static MotionBoundary Describe(in TweenParameters parameters) => new(StageEnd.Duration, parameters.Duration);
    public static void Initialize(in TweenParameters parameters, in MotionValues<T> values, ref TweenState state) => state = default;
    public static void Retarget(in TweenParameters previous, in TweenParameters next, in MotionValues<T> values, ref TweenState state) { }
    public static bool ParametersEqual(in TweenParameters previous, in TweenParameters next) => previous.Duration == next.Duration;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MotionSample<T> Evaluate(in TweenParameters parameters, in MotionTime time,
        in MotionValues<T> values, ref TweenState state)
    {
        bool finished = time.Time >= parameters.Duration;
        T value = finished ? values.To : TOps.Lerp(in values.From, in values.To, TEasing.Sample(Math.Max(0, time.Time / parameters.Duration)));
        return new(value, finished);
    }
}

public readonly record struct SpringParameters
{
    public SpringParameters() : this(180, 20, 0.00001f, 1f / 120) { }

    public SpringParameters(float stiffness = 180, float damping = 20, float tolerance = 0.00001f, float step = 1f / 120)
    {
        DevelopmentCheck.Require(float.IsFinite(stiffness) && stiffness > 0, "Spring stiffness must be finite and positive.");
        DevelopmentCheck.Require(float.IsFinite(damping) && damping >= 0, "Spring damping must be finite and nonnegative.");
        DevelopmentCheck.Require(float.IsFinite(tolerance) && tolerance > 0, "Spring tolerance must be finite and positive.");
        DevelopmentCheck.Require(float.IsFinite(step) && step > 0, "Spring integration step must be finite and positive.");
        (Stiffness, Damping, Tolerance, Step) = (stiffness, damping, tolerance, step);
    }

    public float Stiffness { get; }
    public float Damping { get; }
    public float Tolerance { get; }
    public float Step { get; }
}

public struct SpringState<T> where T : unmanaged
{
    public T Velocity;
}

/// <summary>A mass-one spring; retargeting carries its existing velocity.</summary>
public readonly struct Spring<T, TOps> : IMotion<T, TOps, SpringParameters, SpringState<T>>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    public static MotionBoundary Describe(in SpringParameters parameters) => new(StageEnd.Settled);
    public static void Initialize(in SpringParameters parameters, in MotionValues<T> values, ref SpringState<T> state) => state = default;
    public static void Retarget(in SpringParameters previous, in SpringParameters next, in MotionValues<T> values, ref SpringState<T> state) { }
    public static bool ParametersEqual(in SpringParameters previous, in SpringParameters next) =>
        previous.Stiffness == next.Stiffness && previous.Damping == next.Damping &&
        previous.Tolerance == next.Tolerance && previous.Step == next.Step;

    public static MotionSample<T> Evaluate(in SpringParameters parameters, in MotionTime time,
        in MotionValues<T> values, ref SpringState<T> state)
    {
        T current = values.Current;
        float remaining = time.Delta;
        while (remaining > 0)
        {
            float step = MathF.Min(remaining, parameters.Step);
            T displacement = TOps.Subtract(in values.To, in current);
            T force = TOps.Scale(in displacement, parameters.Stiffness);
            T resistance = TOps.Scale(in state.Velocity, -parameters.Damping);
            T acceleration = TOps.Add(in force, in resistance);
            T velocityChange = TOps.Scale(in acceleration, step);
            state.Velocity = TOps.Add(in state.Velocity, in velocityChange);
            T positionChange = TOps.Scale(in state.Velocity, step);
            current = TOps.Add(in current, in positionChange);
            remaining -= step;
        }

        T zero = default;
        bool finished = TOps.Distance(in current, in values.To) < parameters.Tolerance &&
            TOps.Distance(in state.Velocity, in zero) < parameters.Tolerance;
        if (finished)
        {
            current = values.To;
            state.Velocity = default;
        }
        return new(current, finished);
    }
}

public readonly record struct KeyframeParameters
{
    public KeyframeParameters(float duration, ReadOnlyMemory<Keyframe> keys)
    {
        DevelopmentCheck.Require(float.IsFinite(duration) && duration >= 0, "Keyframe duration must be finite and nonnegative.");
        DevelopmentCheck.Require(keys.Length > 0, "A keyframe motion requires points.");
        ReadOnlySpan<Keyframe> points = keys.Span;
        for (int index = 1; index < points.Length; index++)
            DevelopmentCheck.Require(points[index].Time > points[index - 1].Time, "Keyframes must be ordered by increasing time.");
        (Duration, Keys) = (duration, keys);
    }

    public float Duration { get; }
    public ReadOnlyMemory<Keyframe> Keys { get; }
}

public struct KeyframeState
{
    public int Segment;
}

/// <summary>Normalized points belong to the definition; the instance caches its current interval.</summary>
public readonly struct Keyframes<T, TOps> : IMotion<T, TOps, KeyframeParameters, KeyframeState>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    public static MotionBoundary Describe(in KeyframeParameters parameters) => new(StageEnd.Duration, parameters.Duration);
    public static void Initialize(in KeyframeParameters parameters, in MotionValues<T> values, ref KeyframeState state) => state = default;
    public static void Retarget(in KeyframeParameters previous, in KeyframeParameters next, in MotionValues<T> values, ref KeyframeState state) => state = default;
    public static void UpdateParameters(in KeyframeParameters previous, in KeyframeParameters next, ref KeyframeState state) => state = default;
    public static bool ParametersEqual(in KeyframeParameters previous, in KeyframeParameters next) =>
        previous.Duration == next.Duration && previous.Keys.Equals(next.Keys);

    public static MotionSample<T> Evaluate(in KeyframeParameters parameters, in MotionTime time,
        in MotionValues<T> values, ref KeyframeState state)
    {
        if (time.Time >= parameters.Duration)
            return new(values.To, true);
        float progress = Math.Max(0, time.Time / parameters.Duration);
        ReadOnlySpan<Keyframe> keys = parameters.Keys.Span;
        float amount;
        if (progress <= keys[0].Time)
            amount = keys[0].Value;
        else if (progress >= keys[^1].Time)
            amount = keys[^1].Value;
        else
        {
            while (state.Segment > 0 && progress < keys[state.Segment].Time)
                state.Segment--;
            while (state.Segment + 1 < keys.Length - 1 && progress > keys[state.Segment + 1].Time)
                state.Segment++;
            Keyframe left = keys[state.Segment];
            Keyframe right = keys[state.Segment + 1];
            float interval = (progress - left.Time) / (right.Time - left.Time);
            amount = left.Value + (right.Value - left.Value) * Easing.Evaluate(left.Easing, interval);
        }
        return new(TOps.Lerp(in values.From, in values.To, amount), false);
    }
}

public readonly unsafe struct FunctionTweenParameters
{
    public FunctionTweenParameters(float duration, delegate* managed<float, float> function)
    {
        DevelopmentCheck.Require(float.IsFinite(duration) && duration >= 0, "Tween duration must be finite and nonnegative.");
        DevelopmentCheck.Require(function != null, "A function tween requires a sample address.");
        Duration = duration;
        Function = function;
    }

    public float Duration { get; }
    public readonly delegate* managed<float, float> Function;
}

/// <summary>Adapts a scalar function address; the address is invoked for each active sample.</summary>
public readonly unsafe struct FunctionTween<T, TOps> : IMotion<T, TOps, FunctionTweenParameters, TweenState>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    public static MotionBoundary Describe(in FunctionTweenParameters parameters) => new(StageEnd.Duration, parameters.Duration);
    public static void Initialize(in FunctionTweenParameters parameters, in MotionValues<T> values, ref TweenState state) => state = default;
    public static void Retarget(in FunctionTweenParameters previous, in FunctionTweenParameters next, in MotionValues<T> values, ref TweenState state) { }
    public static bool ParametersEqual(in FunctionTweenParameters previous, in FunctionTweenParameters next) =>
        previous.Duration == next.Duration && (nint)previous.Function == (nint)next.Function;

    public static MotionSample<T> Evaluate(in FunctionTweenParameters parameters, in MotionTime time,
        in MotionValues<T> values, ref TweenState state)
    {
        bool finished = time.Time >= parameters.Duration;
        float progress = finished ? 1 : Math.Max(0, time.Time / parameters.Duration);
        return new(finished ? values.To : TOps.Lerp(in values.From, in values.To, parameters.Function(progress)), finished);
    }
}
