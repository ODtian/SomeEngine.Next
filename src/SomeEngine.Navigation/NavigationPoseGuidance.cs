using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationPoseGuidance
{
    internal static NavigationGuidance Sample(NavigationPath path, ref int poseIndex, NavigationMotion motion,
        NavigationProfile profile, float desiredSpeed, float deltaTime, ulong version)
    {
        ReadOnlySpan<NavigationPose> poses = path.Poses.Span;
        while (poseIndex < poses.Length && Vector3.DistanceSquared(motion.Position, poses[poseIndex].Position) <= 0.00000001f &&
            MathF.Abs(NavigationHeading.Difference(poses[poseIndex].Heading, motion.Heading)) <= 0.0001f) poseIndex++;
        if (poseIndex == poses.Length) return new(default, motion.Heading, version, true);
        NavigationPose target = poses[poseIndex];
        float difference = NavigationHeading.Difference(target.Heading, motion.Heading);
        float heading = motion.Heading + Math.Clamp(difference, -profile.MaximumAngularSpeed * deltaTime, profile.MaximumAngularSpeed * deltaTime);
        if (MathF.Abs(difference) > 0.0001f) return new(default, heading, version, true);
        Vector3 delta = target.Position - motion.Position;
        float distance = delta.Length();
        if (distance <= 0.0001f) return new(default, heading, version, true);
        float travel = MathF.Min(distance, MathF.Min(desiredSpeed, profile.MaximumSpeed) * deltaTime);
        Vector3 next = motion.Position + delta * (travel / distance);
        if (path.TrySampleHeight(next, out float height)) next.Y = height;
        return new((next - motion.Position) / deltaTime, heading, version, true);
    }
}
