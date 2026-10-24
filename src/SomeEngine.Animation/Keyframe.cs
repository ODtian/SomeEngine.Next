using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Components;

namespace SomeEngine.Animation;

/// <summary>A normalized curve point; easing belongs to the interval starting at this point.</summary>
[BufferCapacity(4)]
public readonly record struct Keyframe : IBufferElement
{
    private readonly nint _easing;

    public unsafe Keyframe(float time, float value, EasingType easing = EasingType.Linear)
    {
        DevelopmentCheck.Require(float.IsFinite(time) && time >= 0 && time <= 1, "Keyframe time must be normalized and finite.");
        DevelopmentCheck.Require(float.IsFinite(value), "Keyframe value must be finite.");
        Time = time;
        Value = value;
        Easing = easing;
        _easing = (nint)global::SomeEngine.Animation.Easing.Resolve(easing);
    }

    public float Time { get; }
    public float Value { get; }
    public EasingType Easing { get; }

    public unsafe float Sample(float progress) => _easing == 0 ? Linear.Sample(progress) :
        ((delegate* managed<float, float>)_easing)(progress);
}
