using System.Numerics;

namespace SomeEngine.Physics;

internal static class CollisionManifolds
{
    internal static int Create<TFirst, TSecond>(in TFirst first, in BodyPose firstPose,
        in TSecond second, in BodyPose secondPose, in CollisionDistance distance,
        float contactDistance, float tolerance, Span<CollisionDistance> points)
        where TFirst : struct, ICollisionGeometry<TFirst>
        where TSecond : struct, ICollisionGeometry<TSecond>
    {
        Span<CollisionFeaturePoint> firstFace = stackalloc CollisionFeaturePoint[TFirst.FaceCapacity(in first)];
        Span<CollisionFeaturePoint> secondFace = stackalloc CollisionFeaturePoint[TSecond.FaceCapacity(in second)];
        int firstCount = TFirst.GetFace(in first, in firstPose, distance.Normal, firstFace);
        int secondCount = TSecond.GetFace(in second, in secondPose, -distance.Normal, secondFace);
        int count = 0;
        if (firstCount >= 3)
            count = Clip(firstFace[..firstCount], secondFace[..secondCount], distance.Normal,
                contactDistance, tolerance, points);
        else if (secondCount >= 3)
        {
            count = Clip(secondFace[..secondCount], firstFace[..firstCount], -distance.Normal,
                contactDistance, tolerance, points);
            for (int index = 0; index < count; index++) points[index] = points[index].Reverse();
        }
        else if (firstCount == 2 && secondCount == 2)
            count = Lines(firstFace, secondFace, distance.Normal, tolerance, points);
        if (count > 0) return count;
        points[0] = distance;
        return 1;
    }

    private static int Clip(ReadOnlySpan<CollisionFeaturePoint> reference, ReadOnlySpan<CollisionFeaturePoint> incident,
        Vector3 normal, float contactDistance, float tolerance, Span<CollisionDistance> points)
    {
        int capacity = reference.Length + incident.Length + 4;
        Span<CollisionFeaturePoint> polygon = stackalloc CollisionFeaturePoint[capacity];
        Span<CollisionFeaturePoint> scratch = stackalloc CollisionFeaturePoint[capacity];
        incident.CopyTo(polygon);
        int count = incident.Length;
        Vector3 center = default;
        foreach (CollisionFeaturePoint point in reference) center += point.Point;
        center /= reference.Length;
        for (int edge = 0; edge < reference.Length && count > 0; edge++)
        {
            Vector3 origin = reference[edge].Point;
            Vector3 side = Vector3.Cross(normal, reference[(edge + 1) % reference.Length].Point - origin);
            if (Vector3.Dot(side, center - origin) < 0) side = -side;
            if (incident.Length == 2)
            {
                float first = Vector3.Dot(side, polygon[0].Point - origin) + tolerance;
                float second = Vector3.Dot(side, polygon[count - 1].Point - origin) + tolerance;
                if (first < 0 && second < 0) return 0;
                if (first < 0) polygon[0] = Interpolate(polygon[0], polygon[count - 1], first / (first - second));
                else if (second < 0)
                    polygon[count - 1] = Interpolate(polygon[0], polygon[count - 1], first / (first - second));
                continue;
            }
            int written = 0;
            CollisionFeaturePoint previous = polygon[count - 1];
            float previousDistance = Vector3.Dot(side, previous.Point - origin) + tolerance;
            for (int index = 0; index < count; index++)
            {
                CollisionFeaturePoint current = polygon[index];
                float currentDistance = Vector3.Dot(side, current.Point - origin) + tolerance;
                bool previousInside = previousDistance >= 0;
                bool currentInside = currentDistance >= 0;
                if (previousInside != currentInside)
                    scratch[written++] = Interpolate(previous, current, previousDistance / (previousDistance - currentDistance));
                if (currentInside) scratch[written++] = current;
                previous = current;
                previousDistance = currentDistance;
            }
            scratch[..written].CopyTo(polygon);
            count = written;
        }
        Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(reference[1].Point - reference[0].Point,
            reference[2].Point - reference[0].Point));
        if (Vector3.Dot(faceNormal, normal) < 0) faceNormal = -faceNormal;
        float projection = Vector3.Dot(faceNormal, normal);
        Span<CollisionDistance> candidates = stackalloc CollisionDistance[capacity];
        int contactCount = 0;
        for (int index = 0; index < count; index++)
        {
            CollisionFeaturePoint point = polygon[index];
            float separation = Vector3.Dot(faceNormal, point.Point - reference[0].Point) / projection;
            if (separation > contactDistance + tolerance) continue;
            candidates[contactCount++] = new(point.Point - normal * separation, point.Point, normal,
                separation, reference[0].Feature, point.Feature);
        }
        return Reduce(candidates[..contactCount], points);
    }

    private static CollisionFeaturePoint Interpolate(CollisionFeaturePoint first, CollisionFeaturePoint second,
        float parameter) => new(Vector3.Lerp(first.Point, second.Point, parameter), first.Feature);

    internal static int Reduce(ReadOnlySpan<CollisionDistance> candidates, Span<CollisionDistance> points)
    {
        if (candidates.Length <= 4)
        {
            candidates.CopyTo(points);
            return candidates.Length;
        }
        Span<int> selected = stackalloc int[4];
        selected[0] = 0;
        for (int index = 1; index < candidates.Length; index++)
            if (candidates[index].Separation < candidates[selected[0]].Separation) selected[0] = index;
        for (int point = 1; point < 4; point++)
        {
            float farthest = -1;
            for (int index = 0; index < candidates.Length; index++)
            {
                bool selectedEarlier = false;
                for (int previous = 0; previous < point; previous++)
                    selectedEarlier |= selected[previous] == index;
                if (selectedEarlier) continue;
                float score = float.PositiveInfinity;
                for (int previous = 0; previous < point; previous++)
                    score = MathF.Min(score, Vector3.DistanceSquared(candidates[index].FirstPoint,
                        candidates[selected[previous]].FirstPoint));
                if (point == 2)
                    score = Vector3.Cross(candidates[selected[1]].FirstPoint - candidates[selected[0]].FirstPoint,
                        candidates[index].FirstPoint - candidates[selected[0]].FirstPoint).LengthSquared();
                if (score > farthest)
                {
                    farthest = score;
                    selected[point] = index;
                }
            }
        }
        for (int index = 0; index < 4; index++) points[index] = candidates[selected[index]];
        return 4;
    }

    internal static int Lines(ReadOnlySpan<CollisionFeaturePoint> first, ReadOnlySpan<CollisionFeaturePoint> second,
        Vector3 normal, float tolerance, Span<CollisionDistance> points)
    {
        Vector3 firstEdge = first[1].Point - first[0].Point;
        Vector3 secondEdge = second[1].Point - second[0].Point;
        float firstLength = firstEdge.Length();
        float secondLengthSquared = secondEdge.LengthSquared();
        if (Vector3.Cross(firstEdge, secondEdge).LengthSquared() >
            tolerance * tolerance * firstLength * firstLength * secondLengthSquared) return 0;
        Vector3 axis = firstEdge / firstLength;
        float secondStart = Vector3.Dot(second[0].Point - first[0].Point, axis);
        float secondEnd = Vector3.Dot(second[1].Point - first[0].Point, axis);
        float minimum = MathF.Max(0, MathF.Min(secondStart, secondEnd));
        float maximum = MathF.Min(firstLength, MathF.Max(secondStart, secondEnd));
        if (maximum < minimum) return 0;
        int count = maximum - minimum <= tolerance ? 1 : 2;
        for (int index = 0; index < count; index++)
        {
            Vector3 firstPoint = first[0].Point + axis * (index == 0 ? minimum : maximum);
            float parameter = Vector3.Dot(firstPoint - second[0].Point, secondEdge) / secondLengthSquared;
            Vector3 secondPoint = second[0].Point + secondEdge * parameter;
            points[index] = new(firstPoint, secondPoint, normal, Vector3.Dot(secondPoint - firstPoint, normal),
                first[index].Feature, second[index].Feature);
        }
        return count;
    }
}
