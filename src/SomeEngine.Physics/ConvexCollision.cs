using System.Numerics;

namespace SomeEngine.Physics;

public readonly record struct ConvexCollisionSettings(float Tolerance, int DistanceIterations, int PenetrationIterations);

/// <summary>Support-mapped core distance, EPA penetration and a clipped contact manifold.</summary>
public readonly struct ConvexCollision<TFirst, TSecond>(ConvexCollisionSettings settings)
    : ICollisionNarrowPhase<TFirst, TSecond>
    where TFirst : struct, ICollisionConvex<TFirst>
    where TSecond : struct, ICollisionConvex<TSecond>
{
    public int ContactCapacity => 4;

    public CollisionDistance Distance(in TFirst first, in BodyPose firstPose, in TSecond second, in BodyPose secondPose)
    {
        Span<ConvexSupportPoint> simplex = stackalloc ConvexSupportPoint[4];
        Vector3 direction = secondPose.Position - firstPose.Position;
        if (direction.LengthSquared() == 0) direction = Vector3.UnitX;
        simplex[0] = Support(in first, in firstPose, in second, in secondPose, direction);
        int count = 1;
        Vector4 weights = new(1, 0, 0, 0);
        Vector3 closest = simplex[0].Difference;
        for (int iteration = 0; iteration < settings.DistanceIterations; iteration++)
        {
            float squared = closest.LengthSquared();
            if (squared <= settings.Tolerance * settings.Tolerance)
                return Penetration(in first, in firstPose, in second, in secondPose, simplex, count);
            ConvexSupportPoint support = Support(in first, in firstPose, in second, in secondPose, -closest);
            if (squared - Vector3.Dot(closest, support.Difference) <= settings.Tolerance * MathF.Sqrt(squared)) break;
            simplex[count++] = support;
            closest = CollisionSimplex.Reduce(simplex, ref count, out weights);
        }
        float distance = closest.Length();
        if (distance <= settings.Tolerance)
            return Penetration(in first, in firstPose, in second, in secondPose, simplex, count);
        return CollisionSimplex.Witness(simplex[..count], weights, -closest / distance, distance,
            TFirst.GetConvexRadius(in first), TSecond.GetConvexRadius(in second));
    }

    public int GetManifold(in TFirst first, in BodyPose firstPose, in TSecond second, in BodyPose secondPose,
        float contactDistance, Span<CollisionDistance> points)
    {
        CollisionDistance distance = Distance(in first, in firstPose, in second, in secondPose);
        if (distance.Separation > contactDistance) return 0;
        return CollisionManifolds.Create(in first, in firstPose, in second, in secondPose,
            in distance, contactDistance, settings.Tolerance, points);
    }

    private static ConvexSupportPoint Support(in TFirst first, in BodyPose firstPose,
        in TSecond second, in BodyPose secondPose, Vector3 direction)
    {
        CollisionFeaturePoint a = TFirst.SupportCore(in first, in firstPose, direction);
        CollisionFeaturePoint b = TSecond.SupportCore(in second, in secondPose, -direction);
        return new(a.Point, b.Point, a.Feature, b.Feature);
    }

    private CollisionDistance Penetration(in TFirst first, in BodyPose firstPose,
        in TSecond second, in BodyPose secondPose, ReadOnlySpan<ConvexSupportPoint> simplex, int count)
    {
        Span<ConvexSupportPoint> vertices = stackalloc ConvexSupportPoint[4 + settings.PenetrationIterations];
        simplex[..count].CopyTo(vertices);
        int originalCount = count;
        float squaredTolerance = settings.Tolerance * settings.Tolerance;
        if (count == 1)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 direction = Vector3.Zero;
                direction[axis] = 1;
                vertices[count] = Support(in first, in firstPose, in second, in secondPose, direction);
                if ((vertices[count].Difference - vertices[0].Difference).LengthSquared() > squaredTolerance) break;
                vertices[count] = Support(in first, in firstPose, in second, in secondPose, -direction);
                if ((vertices[count].Difference - vertices[0].Difference).LengthSquared() > squaredTolerance) break;
            }
            if ((vertices[count].Difference - vertices[0].Difference).LengthSquared() <= squaredTolerance)
                return RoundedCoreContact(in first, in second, simplex[..originalCount], Vector3.UnitX);
            count++;
        }
        if (count == 2)
        {
            Vector3 edge = vertices[1].Difference - vertices[0].Difference;
            Vector3 axis = MathF.Abs(edge.X) < MathF.Abs(edge.Y) ? Vector3.UnitX : Vector3.UnitY;
            Vector3 normal = Vector3.Cross(edge, axis);
            float areaTolerance = squaredTolerance * edge.LengthSquared();
            Vector3 alternate = Vector3.Cross(edge, normal);
            for (int direction = 0; direction < 4; direction++)
            {
                Vector3 search = direction < 2 ? normal : alternate;
                if ((direction & 1) != 0) search = -search;
                vertices[count] = Support(in first, in firstPose, in second, in secondPose, search);
                if (Vector3.Cross(edge, vertices[count].Difference - vertices[0].Difference).LengthSquared() > areaTolerance) break;
            }
            if (Vector3.Cross(edge, vertices[count].Difference - vertices[0].Difference).LengthSquared() <= areaTolerance)
                return RoundedCoreContact(in first, in second, simplex[..originalCount], Vector3.Normalize(normal));
            count++;
        }
        if (count == 3)
        {
            Vector3 normal = Vector3.Cross(vertices[1].Difference - vertices[0].Difference,
                vertices[2].Difference - vertices[0].Difference);
            normal = Vector3.Normalize(normal);
            vertices[count] = Support(in first, in firstPose, in second, in secondPose, normal);
            if (MathF.Abs(Vector3.Dot(normal, vertices[count].Difference - vertices[0].Difference)) <= settings.Tolerance)
                vertices[count] = Support(in first, in firstPose, in second, in secondPose, -normal);
            if (MathF.Abs(Vector3.Dot(normal, vertices[count].Difference - vertices[0].Difference)) <= settings.Tolerance)
                return RoundedCoreContact(in first, in second, simplex[..originalCount], normal);
            count++;
        }
        Vector3 interior = (vertices[0].Difference + vertices[1].Difference +
            vertices[2].Difference + vertices[3].Difference) * .25f;
        Span<PenetrationFace> faces = stackalloc PenetrationFace[2 * vertices.Length];
        Span<PenetrationEdge> edges = stackalloc PenetrationEdge[6 * vertices.Length];
        int faceCount = 0;
        AddFace(vertices, faces, ref faceCount, 0, 1, 2, interior);
        AddFace(vertices, faces, ref faceCount, 0, 3, 1, interior);
        AddFace(vertices, faces, ref faceCount, 0, 2, 3, interior);
        AddFace(vertices, faces, ref faceCount, 1, 3, 2, interior);
        PenetrationFace nearest = default;
        for (int iteration = 0; iteration < settings.PenetrationIterations; iteration++)
        {
            int selected = 0;
            for (int index = 1; index < faceCount; index++)
                if (faces[index].Distance < faces[selected].Distance) selected = index;
            nearest = faces[selected];
            ConvexSupportPoint support = Support(in first, in firstPose, in second, in secondPose, nearest.Normal);
            if (Vector3.Dot(nearest.Normal, support.Difference) - nearest.Distance <= settings.Tolerance) break;
            int edgeCount = 0;
            int kept = 0;
            for (int index = 0; index < faceCount; index++)
            {
                PenetrationFace face = faces[index];
                if (Vector3.Dot(face.Normal, support.Difference - vertices[face.First].Difference) > 0)
                {
                    AddEdge(edges, ref edgeCount, face.First, face.Second);
                    AddEdge(edges, ref edgeCount, face.Second, face.Third);
                    AddEdge(edges, ref edgeCount, face.Third, face.First);
                }
                else faces[kept++] = face;
            }
            faceCount = kept;
            int vertex = count;
            vertices[count++] = support;
            for (int index = 0; index < edgeCount; index++)
                AddFace(vertices, faces, ref faceCount, edges[index].First, edges[index].Second, vertex, interior);
        }
        Span<ConvexSupportPoint> triangle = stackalloc ConvexSupportPoint[3];
        triangle[0] = vertices[nearest.First];
        triangle[1] = vertices[nearest.Second];
        triangle[2] = vertices[nearest.Third];
        CollisionSimplex.Closest(triangle, nearest.Normal * nearest.Distance, out Vector4 weights);
        return CollisionSimplex.Witness(triangle, weights, nearest.Normal, -nearest.Distance,
            TFirst.GetConvexRadius(in first), TSecond.GetConvexRadius(in second));
    }

    private static CollisionDistance RoundedCoreContact(in TFirst first, in TSecond second,
        ReadOnlySpan<ConvexSupportPoint> simplex, Vector3 normal)
    {
        CollisionSimplex.Closest(simplex, Vector3.Zero, out Vector4 weights);
        return CollisionSimplex.Witness(simplex, weights, normal, 0,
            TFirst.GetConvexRadius(in first), TSecond.GetConvexRadius(in second));
    }

    private static void AddFace(ReadOnlySpan<ConvexSupportPoint> vertices, Span<PenetrationFace> faces,
        ref int count, int first, int second, int third, Vector3 interior)
    {
        Vector3 a = vertices[first].Difference;
        Vector3 normal = Vector3.Cross(vertices[second].Difference - a, vertices[third].Difference - a);
        float squared = normal.LengthSquared();
        if (squared == 0) return;
        normal /= MathF.Sqrt(squared);
        if (Vector3.Dot(normal, interior - a) > 0)
        {
            normal = -normal;
            (second, third) = (third, second);
        }
        faces[count++] = new(first, second, third, normal, MathF.Max(0, Vector3.Dot(normal, a)));
    }

    private static void AddEdge(Span<PenetrationEdge> edges, ref int count, int first, int second)
    {
        for (int index = 0; index < count; index++)
        {
            if (edges[index].First != second || edges[index].Second != first) continue;
            edges[index] = edges[--count];
            return;
        }
        edges[count++] = new(first, second);
    }

    private readonly record struct PenetrationFace(int First, int Second, int Third, Vector3 Normal, float Distance);
    private readonly record struct PenetrationEdge(int First, int Second);
}
