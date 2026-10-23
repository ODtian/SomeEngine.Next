// AVO response and reachable unsafe sets follow van den Berg et al., ICRA 2011, equations 7–20.
// https://gamma.cs.unc.edu/AVO/AVO.pdf
using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>
/// Acceleration-velocity obstacles built from the finite-horizon response trajectory, clipped to reachable changes.
/// The sampled unsafe disks and capsules form convex supporting constraints before velocity selection.
/// </summary>
public readonly struct AccelerationVelocityAvoidance : INavigationAvoidanceSolver
{
    private const int TimeSamples = 100;
    private const int AngularSamples = 32;

    public static NavigationVelocityResponse GetResponse(in NavigationNeighborAgent agent) => new(agent.Avoidance.ResponseTime);

    public static Vector2 Solve(in NavigationAvoidanceInput input, NavigationAvoidanceWorkspace workspace)
    {
        NavigationNeighborAgent self = input.Self;
        NavigationVelocityResponse response = GetResponse(in self);
        float reachable = response.TargetChangeRadius(self.Profile.MaximumAcceleration);
        Vector2 velocity = Xz(self.Motion.Velocity);
        if (reachable == 0) return velocity;
        Span<Vector2> normals = stackalloc Vector2[AngularSamples];
        Span<Vector2> reachableVertices = stackalloc Vector2[AngularSamples];
        float halfAngle = MathF.PI / AngularSamples;
        for (int index = 0; index < AngularSamples; index++)
        {
            float angle = index * 2 * halfAngle;
            normals[index] = new(MathF.Cos(angle), MathF.Sin(angle));
            reachableVertices[index] = new Vector2(MathF.Cos(angle + halfAngle), MathF.Sin(angle + halfAngle)) / MathF.Cos(halfAngle);
        }

        List<NavigationVelocityConstraint> lines = workspace.Constraints;
        lines.Clear();
        foreach (NavigationBoundarySegment boundary in input.Boundaries)
        {
            if (BuildConstraint(Xz(boundary.Start - self.Motion.Position), Xz(boundary.End - self.Motion.Position), velocity,
                MathF.Max(0, self.Radius - boundary.BakedRadius), response, default, reachable, 0, 1,
                self.Avoidance.BoundaryTimeHorizon, input.DeltaTime, normals, reachableVertices, workspace, out var line))
                lines.Add(line);
        }
        int boundaryCount = lines.Count;
        foreach (int index in input.Neighbors)
        {
            NavigationNeighborAgent other = input.Agents[index];
            NavigationVelocityResponse otherResponse = GetResponse(in other);
            float otherReachable = otherResponse.TargetChangeRadius(other.Profile.MaximumAcceleration);
            float ownWeight = reachable * self.Avoidance.Responsibility;
            float otherWeight = otherReachable * other.Avoidance.Responsibility;
            float share = ownWeight / (ownWeight + otherWeight);
            Vector2 offset = Xz(other.Motion.Position - self.Motion.Position);
            float horizon = MathF.Max(self.Avoidance.TimeHorizon, other.Avoidance.TimeHorizon);
            if (BuildConstraint(offset, offset, velocity - Xz(other.Motion.Velocity), self.Radius + other.Radius,
                response, otherResponse, reachable, otherReachable, share, horizon, input.DeltaTime,
                normals, reachableVertices, workspace, out var line))
            {
                line.Point *= share;
                lines.Add(line);
            }
        }
        var disks = new NavigationVelocityDisks(reachable, -velocity, self.Profile.MaximumSpeed);
        return velocity + NavigationVelocitySolver.Solve(lines, boundaryCount, disks,
            input.PreferredVelocity - velocity, workspace.ProjectedConstraints);
    }

    private static bool BuildConstraint(Vector2 start, Vector2 end, Vector2 relativeVelocity, float radius,
        NavigationVelocityResponse response, NavigationVelocityResponse otherResponse, float reachable, float otherReachable,
        float share, float horizon, float deltaTime, ReadOnlySpan<Vector2> normals, ReadOnlySpan<Vector2> reachableVertices,
        NavigationAvoidanceWorkspace workspace, out NavigationVelocityConstraint line)
    {
        // For a target change q, p(t) = p(0) + v(0)t + b(t)q. Reciprocal shares combine the two b(t) values.
        List<Vector2> samples = workspace.VelocitySamples;
        samples.Clear();
        float firstTime = MathF.Min(deltaTime, horizon / TimeSamples);
        for (int sample = 0; sample < TimeSamples; sample++)
        {
            float time = firstTime + sample * (horizon - firstTime) / (TimeSamples - 1);
            float ownFactor = response.DisplacementFactor(time), otherFactor = otherResponse.DisplacementFactor(time);
            float factor = share * ownFactor + (1 - share) * otherFactor;
            // Every pair of independently reachable changes maps inside this time-specific relative disk.
            float currentReach = (ownFactor * reachable + otherFactor * otherReachable) / factor;
            Vector2 first = (start - relativeVelocity * time) / factor;
            Vector2 second = (end - relativeVelocity * time) / factor;
            float scaledRadius = radius / factor;
            float outerReach = currentReach * reachableVertices[0].Length();
            if (Nearest(first, second).LengthSquared() > (scaledRadius + outerReach) * (scaledRadius + outerReach)) continue;

            List<Vector2> polygon = workspace.VelocityPolygon, clipped = workspace.ClippedVelocityPolygon;
            polygon.Clear();
            foreach (Vector2 vertex in reachableVertices) polygon.Add(currentReach * vertex);
            foreach (Vector2 normal in normals)
            {
                float support = MathF.Max(Vector2.Dot(normal, first), Vector2.Dot(normal, second)) + scaledRadius;
                Clip(polygon, clipped, normal, support);
                (polygon, clipped) = (clipped, polygon);
                if (polygon.Count == 0) break;
            }
            samples.AddRange(polygon);
        }
        if (samples.Count == 0)
        {
            line = default;
            return false;
        }

        List<Vector2> hull = workspace.VelocityHull;
        ConvexHull(samples, hull);
        bool inside = hull.Count >= 3;
        for (int index = 0; index < hull.Count && inside; index++)
            inside = Cross(hull[(index + 1) % hull.Count] - hull[index], -hull[index]) >= -0.000001f;
        Vector2 point = default, normalToSafe = default;
        float closest = float.PositiveInfinity, preference = float.NegativeInfinity;
        Vector2 passingSide = new(-start.Y, start.X);
        for (int index = 0; index < hull.Count; index++)
        {
            Vector2 a = hull[index], b = hull[(index + 1) % hull.Count];
            Vector2 candidate = Nearest(a, b);
            float distance = candidate.LengthSquared();
            Vector2 edge = b - a;
            if (edge.LengthSquared() == 0) continue;
            Vector2 outward = Vector2.Normalize(new Vector2(edge.Y, -edge.X));
            float side = Vector2.Dot(outward, passingSide);
            if (distance > closest + 0.000001f || MathF.Abs(distance - closest) <= 0.000001f && side <= preference) continue;
            point = candidate;
            closest = distance;
            preference = side;
            normalToSafe = outward;
        }
        if (!inside && point.LengthSquared() > 0) normalToSafe = -Vector2.Normalize(point);
        line = new(point, new(normalToSafe.Y, -normalToSafe.X));
        return normalToSafe.LengthSquared() > 0;
    }

    private static void Clip(List<Vector2> polygon, List<Vector2> clipped, Vector2 normal, float support)
    {
        clipped.Clear();
        Vector2 previous = polygon[^1];
        float previousDistance = Vector2.Dot(normal, previous) - support;
        foreach (Vector2 current in polygon)
        {
            float distance = Vector2.Dot(normal, current) - support;
            if ((distance <= 0) != (previousDistance <= 0))
                clipped.Add(Vector2.Lerp(previous, current, previousDistance / (previousDistance - distance)));
            if (distance <= 0) clipped.Add(current);
            previous = current;
            previousDistance = distance;
        }
    }

    private static void ConvexHull(List<Vector2> points, List<Vector2> hull)
    {
        points.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        hull.Clear();
        foreach (Vector2 point in points)
        {
            while (hull.Count >= 2 && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        int lower = hull.Count;
        for (int index = points.Count - 2; index >= 0; index--)
        {
            Vector2 point = points[index];
            while (hull.Count > lower && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
    }

    private static Vector2 Nearest(Vector2 first, Vector2 second)
    {
        Vector2 edge = second - first;
        float lengthSquared = edge.LengthSquared();
        return lengthSquared == 0 ? first : first + Math.Clamp(-Vector2.Dot(first, edge) / lengthSquared, 0, 1) * edge;
    }

    private static Vector2 Xz(Vector3 value) => new(value.X, value.Z);
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
