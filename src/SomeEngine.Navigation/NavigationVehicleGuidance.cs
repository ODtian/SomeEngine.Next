using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationVehicleGuidance
{
    internal static NavigationGuidance Sample(NavigationPath path, ref int segmentIndex, NavigationMotion motion,
        NavigationProfile profile, float desiredSpeed, float deltaTime, ulong version)
    {
        ReadOnlySpan<NavigationMotionSegment> segments = path.MotionSegments.Span;
        while (segmentIndex < segments.Length &&
            Vector3.Distance(motion.Position, segments[segmentIndex].End.Position) < 0.0001f &&
            MathF.Abs(NavigationHeading.Difference(segments[segmentIndex].End.Heading, motion.Heading)) < 0.0001f)
            segmentIndex++;
        if (segmentIndex == segments.Length) return new(default, motion.Heading, version, true);
        NavigationMotionSegment segment = segments[segmentIndex];
        NavigationVehicleControl control = segment.Control;
        float speed = MathF.CopySign(MathF.Min(MathF.Abs(control.Speed), MathF.Min(desiredSpeed, profile.MaximumSpeed)), control.Speed);
        float remaining = MathF.Abs(control.Curvature) < 0.000001f
            ? Vector3.Dot(segment.End.Position - motion.Position, NavigationHeading.Direction(motion.Heading)) / speed
            : NavigationHeading.Difference(segment.End.Heading, motion.Heading) / (control.Curvature * speed);
        control = control with { Speed = speed, Duration = MathF.Min(deltaTime, MathF.Max(0, remaining)) };
        NavigationPose next = profile.VehicleModel!.Value.Integrate(new(motion.Position, motion.Heading), control, control.Duration);
        return new((next.Position - motion.Position) / deltaTime, next.Heading, version, true, control);
    }
}
