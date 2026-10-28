using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>One manifold point. Normal points first-to-second; local points belong to the geometry frames.</summary>
public struct CollisionContact : IComponent
{
    public Entity First;
    public Entity Second;
    public int FirstFeature;
    public int SecondFeature;
    public int PointIndex;
    public Vector3 FirstLocalPoint;
    public Vector3 SecondLocalPoint;
    /// <summary>The midpoint of the two world-space geometric witnesses.</summary>
    public Vector3 WorldPoint;
    public Vector3 Normal;
    public float Separation;
    /// <summary>Number of first-side entries when both sides are stored consecutively in a ContactBody buffer.</summary>
    public int FirstBodyCount;
    public ContactPrediction Prediction;
}

/// <summary>An actual simulated endpoint, its body-local contact anchor and interpolation weight.</summary>
[BufferCapacity(4)]
public readonly record struct ContactBody(Entity Body, Vector3 LocalPoint, float Weight,
    ContactNormalReference NormalReference = ContactNormalReference.MaterialPoint) : IBufferElement
{
    public Vector3 NormalLocalPoint => NormalReference == ContactNormalReference.BodyOrigin ? Vector3.Zero : LocalPoint;
}

public interface ICollisionContactOutput
{
    void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second);
}
