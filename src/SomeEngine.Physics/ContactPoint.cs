using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>A manifold point whose protected undirected endpoints are the two geometry entities.</summary>
[RelationSchema(RelationDirection.Undirected)]
public struct ContactPoint : IComponent
{
    public int FirstFeature;
    public int SecondFeature;
    public int PointIndex;
    public Vector3 FirstLocalPoint;
    public Vector3 SecondLocalPoint;
    public Vector3 WorldPoint;
    public Vector3 Normal;
    public float Separation;
    /// <summary>The weighted second-minus-first body-anchor vector at the geometry sample time.</summary>
    public Vector3 BodyAnchorDelta;
    public int FirstBodyCount;
    public bool WarmStart;
    public ContactPrediction Prediction;
}

/// <summary>A protected point-to-simulated-body relationship for one weighted anchor occurrence.</summary>
[RelationSchema(RelationDirection.Directed)]
public struct ContactParticipant : IComponent;

[BufferCapacity(4)]
public readonly record struct ContactBodyLink(RelationEdge<ContactParticipant> Participant,
    Vector3 LocalPoint, float Weight) : IBufferElement;

public struct ContactMaterial : IComponent
{
    public float NormalStiffness;
    public float NormalDamping;
    public float StaticFriction;
    public float DynamicFriction;
    public float Restitution;
    public float RestitutionThreshold;
}

public struct AvbdContactState : IComponent
{
    public float NormalMultiplier;
    public Vector3 MultiplierNormal;
    public float NormalPenalty;
    public float InitialSeparation;
    public Vector3 InitialDelta;
    public Vector3 TangentMultiplier;
    public float TangentPenalty;
    public float FrictionRegularizationSpeed;
}

/// <summary>Common velocity input and applied physical action for one contact and time interval.</summary>
public struct ContactResponse : IComponent
{
    public float InitialNormalVelocity;
    /// <summary>World-space physical force on the second side, in N.</summary>
    public Vector3 ForceOnSecond;
    /// <summary>Applied compressive normal impulse on the second side, in N·s.</summary>
    public float RestitutionImpulse;
    public double StartTime;
    public double EndTime;
}
