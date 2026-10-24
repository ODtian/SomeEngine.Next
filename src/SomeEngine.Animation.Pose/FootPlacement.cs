using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Foot clearance, terrain resolution and the duration of a changed selection's handoff.</summary>
public sealed record FootPlacementSettings(float FootRadius = .06f, float Clearance = .08f,
    int EnvelopeSamples = 9, float TransitionDuration = .3f);

/// <summary>A movement sample in the selected request's clock; heading zero faces +Z.</summary>
public readonly record struct FootMovementPoint(float Time, Vector3 Position, Vector3 Velocity, float Heading);

/// <summary>A completed movement prediction, beginning with actual movement feedback.</summary>
public sealed class FootMovementTrajectory
{
    private readonly FootMovementPoint[] _points;
    public Span<FootMovementPoint> Points => _points;
    public FootMovementTrajectory(ReadOnlySpan<FootMovementPoint> points) => _points = points.ToArray();

    public Matrix4x4 Sample(double time)
    {
        int first = 0;
        while (first + 2 < _points.Length && _points[first + 1].Time <= time) first++;
        FootMovementPoint from = _points[first], to = _points[first + 1];
        float duration = to.Time - from.Time, amount = Math.Clamp((float)(time - from.Time) / duration, 0, 1);
        float squared = amount * amount, cubed = squared * amount;
        Vector3 position = (2 * cubed - 3 * squared + 1) * from.Position
            + (cubed - 2 * squared + amount) * duration * from.Velocity
            + (-2 * cubed + 3 * squared) * to.Position + (cubed - squared) * duration * to.Velocity;
        float heading = from.Heading + MathF.IEEERemainder(to.Heading - from.Heading, 2 * MathF.PI) * amount;
        return Matrix4x4.CreateRotationY(heading) * Matrix4x4.CreateTranslation(position);
    }
}

/// <summary>Movement prediction before the remaining selected root warp is applied.</summary>
public readonly record struct FootMovementInput(FootMovementTrajectory? Trajectory = null, MotionWarpInput? Warp = null)
{
    internal Matrix4x4 Sample(double time, double currentTime, in PosePredictionRequest source, in Matrix4x4 actor)
    {
        Matrix4x4 movement = actor;
        if (Trajectory is { } trajectory)
        {
            Matrix4x4.Invert(trajectory.Sample(currentTime), out Matrix4x4 inverseMovement);
            movement = actor * inverseMovement * trajectory.Sample(time);
        }
        if (Warp is not { } warp) return movement;
        Matrix4x4.Invert(warp.Sample(source.GetPathTime(currentTime)), out Matrix4x4 inverse);
        return warp.Sample(source.GetPathTime(time)) * inverse * movement;
    }
}

/// <summary>Selected-pose sampling and actual scene-query time spent by this character's latest update.</summary>
public readonly record struct FootPlacementCosts(TimeSpan PoseSampling, TimeSpan GeometryQueries);

/// <summary>The selected future sample, its root in world space, and the actual landing surface.</summary>
public readonly record struct FootLandingPrediction(PosePredictionSample Pose, Matrix4x4 RootWorld, FootContactTarget Contact)
{
    public bool HasPrediction => Pose.Request.Path is not null;
}
