using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>One velocity disk or the intersection of a reachable change disk and a speed disk.</summary>
internal readonly record struct NavigationVelocityDisks(float Radius, Vector2 SpeedCenter = default,
    float SpeedRadius = float.PositiveInfinity)
{
    internal Vector2 Project(Vector2 target, bool directionOnly)
    {
        if (float.IsPositiveInfinity(SpeedRadius))
            return directionOnly ? target * Radius : Limit(target, Radius);
        float radius = Radius, speedRadius = SpeedRadius;
        Vector2 center = SpeedCenter, result = default;
        float best = float.PositiveInfinity;
        if (!directionOnly) Consider(target);
        Consider(Vector2.Zero);
        Consider(directionOnly ? target * radius : Limit(target, radius));
        Consider(center + (directionOnly ? target * speedRadius : Limit(target - center, speedRadius)));
        float distance = center.Length();
        if (distance > 0 && distance <= radius + speedRadius && distance >= MathF.Abs(radius - speedRadius))
        {
            float along = (radius * radius - speedRadius * speedRadius + distance * distance) / (2 * distance);
            float perpendicular = MathF.Sqrt(MathF.Max(0, radius * radius - along * along));
            Vector2 axis = center / distance;
            Vector2 tangent = new(-axis.Y, axis.X);
            Consider(along * axis + perpendicular * tangent);
            Consider(along * axis - perpendicular * tangent);
        }
        return result;

        void Consider(Vector2 candidate)
        {
            if (candidate.LengthSquared() > radius * radius + 0.000001f ||
                Vector2.DistanceSquared(candidate, center) > speedRadius * speedRadius + 0.000001f) return;
            float score = directionOnly ? -Vector2.Dot(target, candidate) : Vector2.DistanceSquared(target, candidate);
            if (score >= best) return;
            best = score;
            result = candidate;
        }
    }

    internal bool ClipLine(Vector2 point, Vector2 direction, out float lower, out float upper)
    {
        lower = float.NegativeInfinity;
        upper = float.PositiveInfinity;
        return ClipDisk(point, direction, Radius, ref lower, ref upper) &&
            (float.IsPositiveInfinity(SpeedRadius) || ClipDisk(point - SpeedCenter, direction, SpeedRadius, ref lower, ref upper));
    }

    private static bool ClipDisk(Vector2 point, Vector2 direction, float radius, ref float lower, ref float upper)
    {
        float along = Vector2.Dot(point, direction);
        float discriminant = along * along + radius * radius - point.LengthSquared();
        if (discriminant < 0) return false;
        float reach = MathF.Sqrt(discriminant);
        lower = MathF.Max(lower, -along - reach);
        upper = MathF.Min(upper, -along + reach);
        return lower <= upper;
    }

    private static Vector2 Limit(Vector2 value, float radius) => value.LengthSquared() > radius * radius
        ? Vector2.Normalize(value) * radius : value;
}
