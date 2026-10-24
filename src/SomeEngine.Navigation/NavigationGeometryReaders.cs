using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>Reads the navigation triangles of an existing collision or terrain source.</summary>
public interface INavigationTriangleReader<TSource>
{
    static abstract int GetTriangleCount(in TSource source);
    static abstract NavigationGeometryTriangle GetTriangle(in TSource source, int index);
}

public readonly record struct NavigationGeometryTriangle(Vector3 A, Vector3 B, Vector3 C, int Area);

/// <summary>Reads a terrain's grid vertices and each quad's surface classification.</summary>
public interface INavigationHeightfieldReader<TSource>
{
    static abstract int GetWidth(in TSource source);
    static abstract int GetDepth(in TSource source);
    static abstract Vector3 GetPosition(in TSource source, int x, int z);
    static abstract int GetArea(in TSource source, int x, int z);
}

public sealed partial class NavigationGeometry
{
    public static NavigationGeometry ReadTriangles<TSource, TReader>(in TSource source)
        where TReader : struct, INavigationTriangleReader<TSource>
    {
        int count = TReader.GetTriangleCount(source);
        var positions = new Vector3[count * 3];
        var triangles = new NavigationTriangle[count];
        for (int index = 0; index < count; index++)
        {
            NavigationGeometryTriangle triangle = TReader.GetTriangle(source, index);
            int vertex = index * 3;
            positions[vertex] = triangle.A;
            positions[vertex + 1] = triangle.B;
            positions[vertex + 2] = triangle.C;
            triangles[index] = new(vertex, vertex + 1, vertex + 2, triangle.Area);
        }
        return new(positions, triangles);
    }

    public static NavigationGeometry ReadHeightfield<TSource, TReader>(in TSource source)
        where TReader : struct, INavigationHeightfieldReader<TSource>
    {
        int width = TReader.GetWidth(source), depth = TReader.GetDepth(source);
        var positions = new Vector3[width * depth];
        var triangles = new NavigationTriangle[(width - 1) * (depth - 1) * 2];
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
            positions[z * width + x] = TReader.GetPosition(source, x, z);
        for (int z = 0; z < depth - 1; z++)
        for (int x = 0; x < width - 1; x++)
        {
            int vertex = z * width + x, triangle = (z * (width - 1) + x) * 2;
            int area = TReader.GetArea(source, x, z);
            triangles[triangle] = new(vertex, vertex + width, vertex + width + 1, area);
            triangles[triangle + 1] = new(vertex, vertex + width + 1, vertex + 1, area);
        }
        return new(positions, triangles);
    }
}
