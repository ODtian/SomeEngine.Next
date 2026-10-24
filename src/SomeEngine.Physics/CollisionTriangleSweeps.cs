using System.Numerics;

namespace SomeEngine.Physics;

internal static partial class CollisionTriangles
{
    internal static bool SweepSphere(in CollisionSphereSweep sweep, Vector3 a, Vector3 b, Vector3 c,
        float maximum, out float distance, out Vector3 point, out Vector3 normal)
    {
        Vector3 face = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        point = ClosestPoint(sweep.Center, a, b, c);
        Vector3 separation = sweep.Center - point;
        float squared = separation.LengthSquared();
        if (squared <= sweep.Radius * sweep.Radius)
        {
            distance = 0;
            normal = squared > 0 ? separation / MathF.Sqrt(squared)
                : Vector3.Dot(face, sweep.Direction) > 0 ? -face : face;
            return true;
        }

        bool found = false;
        distance = maximum;
        point = default;
        normal = default;
        Vector3 facing = Vector3.Dot(face, sweep.Direction) > 0 ? -face : face;
        float speed = Vector3.Dot(facing, sweep.Direction);
        if (speed < -1e-7f)
        {
            float travel = (sweep.Radius - Vector3.Dot(facing, sweep.Center - a)) / speed;
            Vector3 contact = sweep.Center + sweep.Direction * travel - facing * sweep.Radius;
            if (travel >= 0 && travel <= distance && Inside(contact, a, b, c, face))
            {
                distance = travel;
                point = contact;
                normal = facing;
                found = true;
            }
        }

        ReadOnlySpan<Vector3> vertices = [a, b, c];
        for (int index = 0; index < 3; index++)
        {
            if (SweepEdge(in sweep, vertices[index], vertices[(index + 1) % 3], distance,
                out float travel, out Vector3 contact, out Vector3 direction))
            {
                distance = travel; point = contact; normal = direction; found = true;
            }
            if (SweepVertex(in sweep, vertices[index], distance, out travel, out direction))
            {
                distance = travel; point = vertices[index]; normal = direction; found = true;
            }
        }
        return found;
    }

    private static bool Inside(Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        => Vector3.Dot(Vector3.Cross(b - a, point - a), normal) >= 0
        && Vector3.Dot(Vector3.Cross(c - b, point - b), normal) >= 0
        && Vector3.Dot(Vector3.Cross(a - c, point - c), normal) >= 0;

    private static bool SweepEdge(in CollisionSphereSweep sweep, Vector3 from, Vector3 to, float maximum,
        out float distance, out Vector3 point, out Vector3 normal)
    {
        Vector3 edge = to - from;
        float lengthSquared = edge.LengthSquared();
        Vector3 offset = sweep.Center - from;
        Vector3 across = offset - edge * (Vector3.Dot(offset, edge) / lengthSquared);
        Vector3 velocity = sweep.Direction - edge * (Vector3.Dot(sweep.Direction, edge) / lengthSquared);
        float a = velocity.LengthSquared();
        float b = Vector3.Dot(across, velocity);
        float c = across.LengthSquared() - sweep.Radius * sweep.Radius;
        float discriminant = b * b - a * c;
        distance = 0;
        point = normal = default;
        if (a < 1e-7f || discriminant < 0) return false;
        distance = (-b - MathF.Sqrt(discriminant)) / a;
        if (distance < 0 || distance > maximum) return false;
        Vector3 center = sweep.Center + sweep.Direction * distance;
        float fraction = Vector3.Dot(center - from, edge) / lengthSquared;
        if (fraction < 0 || fraction > 1) return false;
        point = from + edge * fraction;
        normal = Vector3.Normalize(center - point);
        return true;
    }

    private static bool SweepVertex(in CollisionSphereSweep sweep, Vector3 vertex, float maximum,
        out float distance, out Vector3 normal)
    {
        Vector3 offset = sweep.Center - vertex;
        float b = Vector3.Dot(offset, sweep.Direction);
        float c = offset.LengthSquared() - sweep.Radius * sweep.Radius;
        float discriminant = b * b - c;
        distance = 0;
        normal = default;
        if (discriminant < 0) return false;
        distance = -b - MathF.Sqrt(discriminant);
        if (distance < 0 || distance > maximum) return false;
        normal = Vector3.Normalize(offset + sweep.Direction * distance);
        return true;
    }

    private static Vector3 ClosestPoint(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = point - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = point - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = point - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
        float inverse = 1 / (va + vb + vc);
        return a + ab * (vb * inverse) + ac * (vc * inverse);
    }
}
