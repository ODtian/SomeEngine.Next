using System.Numerics;

namespace SomeEngine.Navigation;

public sealed partial class NavigationMesh
{
    public static bool TryGetHeight(NavigationMesh space, Vector3 position, out float height)
    {
        int polygon = space.FindPolygon(position);
        if (polygon >= 0)
            return space.TrySampleHeight(polygon, position, out height);
        height = position.Y;
        return false;
    }

    /// <summary>Finds the polygon beneath this XZ position on the nearest height layer.</summary>
    public int FindPolygon(Vector3 position)
    {
        int closest = -1;
        float closestHeight = float.PositiveInfinity;
        for (int index = 0; index < Polygons.Length; index++)
        {
            if (!TrySampleHeight(index, position, out float height)) continue;
            float distance = MathF.Abs(position.Y - height);
            if (distance >= closestHeight) continue;
            closestHeight = distance;
            closest = index;
        }
        return closest;
    }

    /// <summary>Samples the polygon's surface at an XZ position within its boundary.</summary>
    public bool TrySampleHeight(int polygonIndex, Vector3 position, out float height)
    {
        NavigationPolygon polygon = Polygons.Span[polygonIndex];
        if (polygon.DetailCount > 0)
        {
            foreach (NavigationDetailTriangle triangle in DetailTriangles.Span.Slice(polygon.DetailStart, polygon.DetailCount))
            {
                if (TryTriangleHeight(position, DetailPositions.Span[triangle.A],
                        DetailPositions.Span[triangle.B], DetailPositions.Span[triangle.C], out height))
                    return true;
            }
            height = 0;
            return false;
        }

        ReadOnlySpan<int> vertices = PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount);
        Vector3 first = Positions.Span[vertices[0]];
        for (int index = 1; index + 1 < vertices.Length; index++)
        {
            if (TryTriangleHeight(position, first, Positions.Span[vertices[index]],
                    Positions.Span[vertices[index + 1]], out height))
                return true;
        }
        height = 0;
        return false;
    }

    private static bool TryTriangleHeight(Vector3 point, Vector3 first, Vector3 second,
        Vector3 third, out float height)
    {
        float secondX = second.X - first.X, secondZ = second.Z - first.Z;
        float thirdX = third.X - first.X, thirdZ = third.Z - first.Z;
        float pointX = point.X - first.X, pointZ = point.Z - first.Z;
        float area = secondX * thirdZ - secondZ * thirdX;
        if (area == 0)
        {
            height = 0;
            return false;
        }
        float secondWeight = (pointX * thirdZ - pointZ * thirdX) / area;
        float thirdWeight = (secondX * pointZ - secondZ * pointX) / area;
        const float tolerance = 0.00001f;
        if (secondWeight < -tolerance || thirdWeight < -tolerance || secondWeight + thirdWeight > 1 + tolerance)
        {
            height = 0;
            return false;
        }
        height = first.Y + secondWeight * (second.Y - first.Y) + thirdWeight * (third.Y - first.Y);
        return true;
    }
}
