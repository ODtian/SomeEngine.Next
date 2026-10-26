using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>A radial interval in meters between the two bodies' local anchors.</summary>
[RelationSchema(RelationDirection.Undirected)]
public struct DistanceRange : IComponent
{
    public Vector3 FirstAnchor;
    public Vector3 SecondAnchor;
    public float Minimum;
    public float Maximum;
    /// <summary>Elastic stiffness outside the interval, in N/m; infinity selects a hard interval.</summary>
    public float Stiffness;
}

public struct AvbdDistanceRangeState : IComponent
{
    /// <summary>Upper/lower radial reactions, in N; the equal-bound case uses a signed first value.</summary>
    public Vector2 Multiplier;
    public float Penalty;
    public Vector2 InitialError;
}
