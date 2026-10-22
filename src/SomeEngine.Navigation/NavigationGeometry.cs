using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationBounds(Vector3 Minimum, Vector3 Maximum)
{
    public bool Contains(Vector2 point) => point.X >= Minimum.X && point.X < Maximum.X
        && point.Y >= Minimum.Z && point.Y < Maximum.Z;
}

public readonly record struct NavigationTriangle(int A, int B, int C, int Area);

/// <summary>Shared positions and topology. Supplied arrays remain immutable during use.</summary>
public sealed partial class NavigationGeometry
{
    public ReadOnlyMemory<Vector3> Positions { get; }
    public ReadOnlyMemory<NavigationTriangle> Triangles { get; }
    public NavigationBounds Bounds { get; }

    public NavigationGeometry(Vector3[] positions, NavigationTriangle[] triangles)
        : this((ReadOnlyMemory<Vector3>)positions, triangles) { }

    public NavigationGeometry(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<NavigationTriangle> triangles)
    {
        Positions = positions;
        Triangles = triangles;
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in positions.Span)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        Bounds = new(minimum, maximum);
    }
}
