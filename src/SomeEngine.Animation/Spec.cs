using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation;

public readonly struct Spec
{
    public readonly float Duration;
    public readonly Curve Curve;

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
