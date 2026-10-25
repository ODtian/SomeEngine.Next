using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>A shared convex core with an explicit spherical radius around its surface.</summary>
public readonly record struct ConvexHullGeometry(ConvexHullData Data, float ConvexRadius)
    : IComponent, ICollisionConvex<ConvexHullGeometry>
{
    public static float GetConvexRadius(in ConvexHullGeometry geometry) => geometry.ConvexRadius;

    public static CollisionBounds Bounds(in ConvexHullGeometry geometry, in BodyPose pose) =>
        CollisionConvex.Bounds(in geometry, in pose);

    public static CollisionFeaturePoint SupportCore(in ConvexHullGeometry geometry, in BodyPose pose,
        Vector3 direction)
    {
        Vector3 local = GeometrySpace.DirectionToLocal(in pose, direction);
        ReadOnlySpan<Vector3> vertices = geometry.Data.Vertices.Span;
        int selected = 0;
        for (int index = 1; index < vertices.Length; index++)
            if (Vector3.Dot(vertices[index], local) > Vector3.Dot(vertices[selected], local)) selected = index;
        return new(GeometrySpace.ToWorld(in pose, vertices[selected]), geometry.Data.Faces.Length + selected);
    }

    public static int FaceCapacity(in ConvexHullGeometry geometry) => geometry.Data.MaximumFaceVertices;

    public static int GetFace(in ConvexHullGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points)
    {
        Vector3 local = GeometrySpace.DirectionToLocal(in pose, direction);
        ReadOnlySpan<HullFace> faces = geometry.Data.Faces.Span;
        int selected = 0;
        for (int index = 1; index < faces.Length; index++)
            if (Vector3.Dot(faces[index].Normal, local) > Vector3.Dot(faces[selected].Normal, local)) selected = index;
        HullFace face = faces[selected];
        ReadOnlySpan<int> indices = geometry.Data.FaceVertices.Span.Slice(face.FirstIndex, face.IndexCount);
        ReadOnlySpan<Vector3> vertices = geometry.Data.Vertices.Span;
        for (int index = 0; index < indices.Length; index++)
            points[index] = new(GeometrySpace.ToWorld(in pose,
                vertices[indices[index]] + face.Normal * geometry.ConvexRadius), selected);
        return indices.Length;
    }

    public static GeometryPointDistance DistanceToPoint(in ConvexHullGeometry geometry, in BodyPose pose,
        Vector3 point)
    {
        Vector3 local = GeometrySpace.ToLocal(in pose, point);
        ReadOnlySpan<Vector3> vertices = geometry.Data.Vertices.Span;
        ReadOnlySpan<HullFace> faces = geometry.Data.Faces.Span;
        ReadOnlySpan<int> indices = geometry.Data.FaceVertices.Span;
        float planeDistance = float.NegativeInfinity;
        int nearestFace = 0;
        for (int index = 0; index < faces.Length; index++)
        {
            HullFace face = faces[index];
            float distance = Vector3.Dot(face.Normal, local - vertices[indices[face.FirstIndex]]);
            if (distance > planeDistance)
            {
                planeDistance = distance;
                nearestFace = index;
            }
        }

        Vector3 normal, closest;
        float separation;
        if (planeDistance <= 0 || ContainsFacePoint(geometry.Data, nearestFace,
                local - faces[nearestFace].Normal * planeDistance))
        {
            normal = faces[nearestFace].Normal;
            closest = local - normal * planeDistance;
            separation = planeDistance;
        }
        else
        {
            float squared = float.PositiveInfinity;
            closest = default;
            for (int index = 0; index < faces.Length; index++)
            {
                HullFace face = faces[index];
                int first = indices[face.FirstIndex];
                for (int triangle = 1; triangle < face.IndexCount - 1; triangle++)
                {
                    Vector3 candidate = CollisionTriangles.ClosestPoint(local, vertices[first],
                        vertices[indices[face.FirstIndex + triangle]], vertices[indices[face.FirstIndex + triangle + 1]]);
                    float distance = Vector3.DistanceSquared(local, candidate);
                    if (distance >= squared) continue;
                    squared = distance;
                    closest = candidate;
                    nearestFace = index;
                }
            }
            separation = MathF.Sqrt(squared);
            normal = (local - closest) / separation;
        }
        return new(GeometrySpace.ToWorld(in pose, closest + normal * geometry.ConvexRadius),
            GeometrySpace.DirectionToWorld(in pose, normal), separation - geometry.ConvexRadius, nearestFace);
    }

    private static bool ContainsFacePoint(ConvexHullData data, int faceIndex, Vector3 point)
    {
        HullFace face = data.Faces.Span[faceIndex];
        ReadOnlySpan<int> indices = data.FaceVertices.Span.Slice(face.FirstIndex, face.IndexCount);
        ReadOnlySpan<Vector3> vertices = data.Vertices.Span;
        for (int edge = 0; edge < indices.Length; edge++)
        {
            Vector3 start = vertices[indices[edge]];
            Vector3 end = vertices[indices[(edge + 1) % indices.Length]];
            if (Vector3.Dot(Vector3.Cross(end - start, point - start), face.Normal) < 0) return false;
        }
        return true;
    }
}
