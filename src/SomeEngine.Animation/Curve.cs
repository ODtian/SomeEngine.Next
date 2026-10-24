using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation;

public enum CurveKind : byte { Linear, Easing, Spring, Keyframes, Function }

/// <summary>Curve data sampled by a value evaluator.</summary>
public unsafe struct Curve
{
    public CurveKind Kind;
    public EasingType Easing;
    public float Stiffness;
    public float Damping;
    public float Tolerance;
    public int KeyframeStart;
    public int KeyframeCount;
    public delegate*<float, float> Function;

    public static Curve FromFunction(delegate*<float, float> function)
    {
        DevelopmentCheck.Require(function != null, "A function curve requires a function pointer.");
        return new Curve { Kind = CurveKind.Function, Function = function };
    }

    public readonly float Sample(float progress, ReadOnlySpan<Keyframe> keyframes = default)
    {
        if (Kind == CurveKind.Keyframes)
            return SampleKeyframes(progress, keyframes);
        if (Kind == CurveKind.Function)
            return Function(Math.Clamp(progress, 0, 1));
        DevelopmentCheck.Require(Kind is CurveKind.Linear or CurveKind.Easing, "Stateful spring curves are sampled with Step.");
        return global::SomeEngine.Animation.Easing.Evaluate(Kind == CurveKind.Linear ? EasingType.Linear : Easing, progress);
    }

    private readonly float SampleKeyframes(float progress, ReadOnlySpan<Keyframe> buffer)
    {
        int count = KeyframeCount == 0 ? buffer.Length - KeyframeStart : KeyframeCount;
        DevelopmentCheck.Require(KeyframeStart >= 0 && count > 0 && KeyframeStart <= buffer.Length - count, "A keyframe curve requires a populated range in its instance buffer.");
        ReadOnlySpan<Keyframe> keys = buffer.Slice(KeyframeStart, count);
        if (progress <= keys[0].Time) return keys[0].Value;
        if (progress >= keys[^1].Time) return keys[^1].Value;
        for (int i = 1; i < keys.Length; i++)
        {
            Keyframe left = keys[i - 1];
            Keyframe right = keys[i];
            DevelopmentCheck.Require(right.Time > left.Time, "Keyframes must be ordered by increasing time.");
            if (progress > right.Time)
                continue;
            float t = (progress - left.Time) / (right.Time - left.Time);
            float eased = left.Sample(t);
            return left.Value + (right.Value - left.Value) * eased;
        }
        return keys[^1].Value;
    }

    /// <summary>Advances a mass-one spring, preserving velocity between samples and target changes.</summary>
    public readonly bool Step<T, TOps>(float delta, in T target, ref T current, ref T velocity)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
    {
        DevelopmentCheck.Require(Kind == CurveKind.Spring, "Spring stepping requires a spring curve.");
        DevelopmentCheck.Require(float.IsFinite(delta) && delta >= 0, "Spring frame time must be finite and nonnegative.");
        float remaining = delta;
        while (remaining > 0)
        {
            float step = MathF.Min(remaining, 1f / 120);
            T displacement = TOps.Subtract(in target, in current);
            T force = TOps.Scale(in displacement, Stiffness);
            T resistance = TOps.Scale(in velocity, -Damping);
            T acceleration = TOps.Add(in force, in resistance);
            T velocityChange = TOps.Scale(in acceleration, step);
            velocity = TOps.Add(in velocity, in velocityChange);
            T positionChange = TOps.Scale(in velocity, step);
            current = TOps.Add(in current, in positionChange);
            remaining -= step;
        }

        T zero = default;
        if (TOps.Distance(in current, in target) >= Tolerance || TOps.Distance(in velocity, in zero) >= Tolerance)
            return false;
        current = target;
        velocity = default;
        return true;
    }
}
