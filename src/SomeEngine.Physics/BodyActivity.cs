using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>One body's sleep state, accumulated quiet time and angular-motion length scale.</summary>
public struct BodyActivity : IComponent
{
    public bool Sleeping;
    public float QuietSeconds;
    public float Radius;
    internal bool MotionPredicted;
}

public readonly record struct BodyActivitySettings(float SpeedThreshold, float QuietDuration,
    float LinearErrorThreshold, float AngularErrorThreshold);

internal static class BodyActivities
{
    internal static bool IsAwake(scoped QueryRow row) =>
        !row.TryReadRef<BodyActivity>(out var activity) || !activity.Value.Sleeping;
}
