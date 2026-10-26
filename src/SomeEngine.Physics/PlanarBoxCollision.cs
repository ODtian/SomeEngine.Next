// SPDX-License-Identifier: MIT
// Copyright (c) 2019 Erin Catto
// Four face axes and incident-edge clipping follow Box2D-Lite through
// codex/job331-380:tools/SomeEngine.Physics.Research/References/2d/source/collide.cpp.
// See ThirdParty/Box2DLite/LICENSE.
using System.Numerics;

namespace SomeEngine.Physics;

/// <summary>XY box distance and at most two in-plane contacts; Z half-width remains available to the selected 3D bounds/rendering.</summary>
public readonly struct PlanarBoxCollision(float tolerance) : ICollisionNarrowPhase<BoxGeometry, BoxGeometry>
{
    public int ContactCapacity => 2;

    public CollisionDistance Distance(in BoxGeometry first, in BodyPose firstPose, in BoxGeometry second, in BodyPose secondPose)
    {
        ProjectedBox left = Project(in first, in firstPose), right = Project(in second, in secondPose);
        BoxAxis axis = FindAxis(in left, in right);
        if (axis.Separation > 0) return FindClosest(in left, in right);
        Span<CollisionDistance> points = stackalloc CollisionDistance[2];
        int count = ClipFaces(in left, in right, in axis, 0, points);
        CollisionDistance closest = points[0];
        for (int index = 1; index < count; index++)
            if (points[index].Separation < closest.Separation) closest = points[index];
        return closest;
    }

    public int GetManifold(in BoxGeometry first, in BodyPose firstPose, in BoxGeometry second, in BodyPose secondPose,
        float contactDistance, Span<CollisionDistance> points)
    {
        ProjectedBox left = Project(in first, in firstPose), right = Project(in second, in secondPose);
        BoxAxis axis = FindAxis(in left, in right);
        if (axis.Separation > contactDistance) return 0;
        int count = ClipFaces(in left, in right, in axis, contactDistance, points);
        if (count > 0 || axis.Separation <= 0) return count;
        CollisionDistance closest = FindClosest(in left, in right);
        if (closest.Separation > contactDistance) return 0;
        points[0] = closest;
        return 1;
    }

    private static ProjectedBox Project(in BoxGeometry geometry, in BodyPose pose)
    {
        Vector3 x = Vector3.Transform(Vector3.UnitX, pose.Rotation), y = Vector3.Transform(Vector3.UnitY, pose.Rotation);
        return new(new(pose.Position.X, pose.Position.Y), new(x.X, x.Y), new(y.X, y.Y),
            new(geometry.HalfExtents.X, geometry.HalfExtents.Y), pose.Position.Z);
    }

    private static BoxAxis FindAxis(in ProjectedBox first, in ProjectedBox second)
    {
        Vector2 delta = second.Center - first.Center;
        BoxAxis selected = new(0, Vector2.UnitX, float.NegativeInfinity);
        for (int index = 0; index < 4; index++)
        {
            Vector2 direction = index < 2 ? first.GetAxis(index) : second.GetAxis(index - 2);
            float offset = Vector2.Dot(delta, direction);
            float separation = MathF.Abs(offset) - first.GetRadius(direction) - second.GetRadius(direction);
            if (separation > selected.Separation) selected = new(index, offset >= 0 ? direction : -direction, separation);
        }
        return selected;
    }

    private int ClipFaces(in ProjectedBox first, in ProjectedBox second, in BoxAxis axis,
        float contactDistance, Span<CollisionDistance> points)
    {
        bool referenceFirst = axis.Index < 2;
        ProjectedBox reference = referenceFirst ? first : second, incident = referenceFirst ? second : first;
        int referenceAxis = axis.Index % 2;
        Vector2 normal = referenceFirst ? axis.Normal : -axis.Normal;
        Vector2 side = reference.GetAxis(1 - referenceAxis);
        float front = Vector2.Dot(reference.Center, normal) + reference.HalfExtents[referenceAxis];
        float sideCenter = Vector2.Dot(reference.Center, side), extent = reference.HalfExtents[1 - referenceAxis];
        int referenceFeature = referenceAxis * 2 + (Vector2.Dot(reference.GetAxis(referenceAxis), normal) > 0 ? 1 : 0);
        Span<ClipPoint> edge = stackalloc ClipPoint[2], scratch = stackalloc ClipPoint[2];
        GetIncidentEdge(in incident, normal, edge, out int incidentFeature);
        int count = Clip(edge, scratch, -side, -sideCenter + extent, incidentFeature);
        count = Clip(scratch[..count], edge, side, sideCenter + extent, incidentFeature);
        int written = 0;
        for (int index = 0; index < count; index++)
        {
            float separation = Vector2.Dot(normal, edge[index].Position) - front;
            if (separation > contactDistance + tolerance) continue;
            Vector2 onReference = edge[index].Position - normal * separation;
            Vector3 a = new(referenceFirst ? onReference : edge[index].Position, first.Z);
            Vector3 b = new(referenceFirst ? edge[index].Position : onReference, second.Z);
            points[written++] = new(a, b, new Vector3(axis.Normal, 0), separation,
                referenceFirst ? referenceFeature : edge[index].Feature, referenceFirst ? edge[index].Feature : referenceFeature);
        }
        return written;
    }

    private static void GetIncidentEdge(in ProjectedBox box, Vector2 normal, Span<ClipPoint> points, out int face)
    {
        int axis = MathF.Abs(Vector2.Dot(normal, box.X)) > MathF.Abs(Vector2.Dot(normal, box.Y)) ? 0 : 1;
        Vector2 direction = box.GetAxis(axis);
        float sign = Vector2.Dot(normal, direction) > 0 ? -1 : 1;
        Vector2 center = box.Center + direction * (sign * box.HalfExtents[axis]);
        Vector2 extent = box.GetAxis(1 - axis) * box.HalfExtents[1 - axis];
        face = axis * 2 + (sign > 0 ? 1 : 0);
        points[0] = new(center - extent, box.GetVertexFeature(center - extent));
        points[1] = new(center + extent, box.GetVertexFeature(center + extent));
    }

    private int Clip(ReadOnlySpan<ClipPoint> input, Span<ClipPoint> output, Vector2 normal, float offset, int face)
    {
        int count = 0;
        for (int index = 0; index < input.Length; index++)
            if (Vector2.Dot(normal, input[index].Position) - offset - tolerance <= 0) output[count++] = input[index];
        if (input.Length == 2)
        {
            float first = Vector2.Dot(normal, input[0].Position) - offset - tolerance;
            float second = Vector2.Dot(normal, input[1].Position) - offset - tolerance;
            if (first * second < 0)
                output[count++] = new(Vector2.Lerp(input[0].Position, input[1].Position, first / (first - second)), face);
        }
        return count;
    }

    private static CollisionDistance FindClosest(in ProjectedBox first, in ProjectedBox second)
    {
        float squared = float.PositiveInfinity;
        Vector2 a = default, b = default;
        int firstFeature = 0, secondFeature = 0;
        for (int vertex = 0; vertex < 4; vertex++)
            for (int edge = 0; edge < 4; edge++)
            {
                Vector2 point = first.GetVertex(vertex);
                Vector2 closest = ClosestOnSegment(point, second.GetVertex(edge), second.GetVertex((edge + 1) % 4));
                float distance = Vector2.DistanceSquared(point, closest);
                if (distance < squared) { squared = distance; a = point; b = closest; firstFeature = first.GetVertexFeature(point); secondFeature = GetEdgeFeature(edge); }
                point = second.GetVertex(vertex);
                closest = ClosestOnSegment(point, first.GetVertex(edge), first.GetVertex((edge + 1) % 4));
                distance = Vector2.DistanceSquared(point, closest);
                if (distance < squared) { squared = distance; a = closest; b = point; firstFeature = GetEdgeFeature(edge); secondFeature = second.GetVertexFeature(point); }
            }
        float separation = MathF.Sqrt(squared);
        return new(new(a, first.Z), new(b, second.Z), new((b - a) / separation, 0), separation, firstFeature, secondFeature);
    }

    private static Vector2 ClosestOnSegment(Vector2 point, Vector2 first, Vector2 second)
    {
        Vector2 edge = second - first;
        return first + edge * Math.Clamp(Vector2.Dot(point - first, edge) / edge.LengthSquared(), 0, 1);
    }
    private static int GetEdgeFeature(int edge) => edge switch { 0 => 2, 1 => 1, 2 => 3, _ => 0 };
    private readonly record struct BoxAxis(int Index, Vector2 Normal, float Separation);
    private readonly record struct ClipPoint(Vector2 Position, int Feature);
    private readonly record struct ProjectedBox(Vector2 Center, Vector2 X, Vector2 Y, Vector2 HalfExtents, float Z)
    {
        internal Vector2 GetAxis(int axis) => axis == 0 ? X : Y;
        internal float GetRadius(Vector2 direction) => MathF.Abs(Vector2.Dot(direction, X)) * HalfExtents.X + MathF.Abs(Vector2.Dot(direction, Y)) * HalfExtents.Y;
        internal Vector2 GetVertex(int index) => Center + X * (index is 1 or 2 ? HalfExtents.X : -HalfExtents.X)
            + Y * (index >= 2 ? HalfExtents.Y : -HalfExtents.Y);
        internal int GetVertexFeature(Vector2 point) => 8 + (Vector2.Dot(point - Center, X) >= 0 ? 1 : 0) + (Vector2.Dot(point - Center, Y) >= 0 ? 2 : 0);
    }
}
