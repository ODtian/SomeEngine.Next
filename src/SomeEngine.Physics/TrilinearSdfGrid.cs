using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

public sealed class SdfGridData(int xCount, int yCount, int zCount, ReadOnlyMemory<float> distances)
{
    public int XCount { get; } = xCount;
    public int YCount { get; } = yCount;
    public int ZCount { get; } = zCount;
    public ReadOnlyMemory<float> Distances { get; } = distances;
}

public readonly record struct SdfGridSampling(Vector3 Origin, Vector3 CellSize);

/// <summary>Trilinear distance and gradient sampling with outer-cell linear extrapolation.</summary>
public readonly record struct TrilinearSdfGrid(SdfGridData Data, SdfGridSampling Sampling) : ISdfField<TrilinearSdfGrid>
{
    public static CollisionBounds Bounds(in TrilinearSdfGrid field) => new(field.Sampling.Origin,
        field.Sampling.Origin + field.Sampling.CellSize * new Vector3(field.Data.XCount - 1, field.Data.YCount - 1, field.Data.ZCount - 1));

    public static SdfSample Sample(in TrilinearSdfGrid field, Vector3 point)
    {
        SdfGridData data = field.Data;
        Vector3 coordinate = (point - field.Sampling.Origin) / field.Sampling.CellSize;
        int x = Math.Clamp((int)MathF.Floor(coordinate.X), 0, data.XCount - 2);
        int y = Math.Clamp((int)MathF.Floor(coordinate.Y), 0, data.YCount - 2);
        int z = Math.Clamp((int)MathF.Floor(coordinate.Z), 0, data.ZCount - 2);
        Vector3 fraction = coordinate - new Vector3(x, y, z);
        int first = x + data.XCount * (y + data.YCount * z);
        int plane = data.XCount * data.YCount;
        ReadOnlySpan<float> values = data.Distances.Span;
        float lowerLeft = values[first], lowerRight = values[first + 1];
        float upperLeft = values[first + data.XCount], upperRight = values[first + data.XCount + 1];
        float backLowerLeft = values[first + plane], backLowerRight = values[first + plane + 1];
        float backUpperLeft = values[first + plane + data.XCount], backUpperRight = values[first + plane + data.XCount + 1];
        float lower = Lerp(lowerLeft, lowerRight, fraction.X), upper = Lerp(upperLeft, upperRight, fraction.X);
        float backLower = Lerp(backLowerLeft, backLowerRight, fraction.X), backUpper = Lerp(backUpperLeft, backUpperRight, fraction.X);
        float front = Lerp(lower, upper, fraction.Y), back = Lerp(backLower, backUpper, fraction.Y);
        Vector3 gradient = new(
            Lerp(Lerp(lowerRight - lowerLeft, upperRight - upperLeft, fraction.Y),
                Lerp(backLowerRight - backLowerLeft, backUpperRight - backUpperLeft, fraction.Y), fraction.Z) / field.Sampling.CellSize.X,
            Lerp(upper - lower, backUpper - backLower, fraction.Z) / field.Sampling.CellSize.Y,
            (back - front) / field.Sampling.CellSize.Z);
        int feature = x + (data.XCount - 1) * (y + (data.YCount - 1) * z);
        return new(Lerp(front, back, fraction.Z), gradient, feature);
    }

    public static void CollectResources(in TrilinearSdfGrid field, ref JobResourceAccessBuilder resources) =>
        resources.Read(field.Data.Distances);

    private static float Lerp(float first, float second, float parameter) => first + (second - first) * parameter;
}
