using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.ECS.Spatial;

/// <summary>An axis-aligned spatial range in world coordinates.</summary>
public readonly struct SpatialBounds(Vector3 min, Vector3 max) : ITrackedComponent
{
    public Vector3 Min { get; } = min;

    public Vector3 Max { get; } = max;

    public bool IsFinite => float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Min.Z)
        && float.IsFinite(Max.X) && float.IsFinite(Max.Y) && float.IsFinite(Max.Z);

    public bool Overlaps(in SpatialBounds other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public static SpatialBounds Union(in SpatialBounds first, in SpatialBounds second) =>
        new(Vector3.Min(first.Min, second.Min), Vector3.Max(first.Max, second.Max));
}

/// <summary>Reads spatial bounds from a concrete component type.</summary>
public interface ISpatialBoundsReader<TComponent>
{
    SpatialBounds Read(in TComponent component);
}

public readonly struct SpatialBoundsReader : ISpatialBoundsReader<SpatialBounds>
{
    public SpatialBounds Read(in SpatialBounds component) => component;
}
