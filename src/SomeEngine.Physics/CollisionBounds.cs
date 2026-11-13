using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>An axis-aligned collision range in world coordinates.</summary>
public readonly struct CollisionBounds(Vector3 min, Vector3 max) : ITrackedComponent
{
    public Vector3 Min { get; } = min;

    public Vector3 Max { get; } = max;

    public bool IsFinite => float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Min.Z)
        && float.IsFinite(Max.X) && float.IsFinite(Max.Y) && float.IsFinite(Max.Z);

    public bool Overlaps(in CollisionBounds other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X &&
        Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
        Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public static CollisionBounds Union(in CollisionBounds first, in CollisionBounds second) =>
        new(Vector3.Min(first.Min, second.Min), Vector3.Max(first.Max, second.Max));
}

/// <summary>Reads collision geometry from a concrete component type.</summary>
public readonly struct CollisionBoundsReader : ISpatialBoundsReader<CollisionBounds>
{
    public SpatialBounds Read(in CollisionBounds component) => new(component.Min, component.Max);
}
