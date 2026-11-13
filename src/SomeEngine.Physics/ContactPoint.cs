using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>The numerical geometry of one point, assembled from the contact columns.</summary>
public struct ContactPoint
{
    public int PairIndex;
    public int FirstFeature;
    public int SecondFeature;
    public int PointIndex;
    public Vector3 FirstLocalPoint;
    public Vector3 SecondLocalPoint;
    public Vector3 WorldPoint;
    public Vector3 Normal;
    public float Separation;
    /// <summary>The weighted second-minus-first normal-reference vector at the geometry sample time.</summary>
    public Vector3 BodyAnchorDelta;
    public int FirstBodyCount;
    public bool WarmStart;
    public ContactPrediction Prediction;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct ContactBodyLink(Entity Body,
    Vector3 LocalPoint, float Weight,
    ContactNormalReference NormalReference = ContactNormalReference.MaterialPoint)
{
    public Vector3 NormalLocalPoint => NormalReference == ContactNormalReference.BodyOrigin ? Vector3.Zero : LocalPoint;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ContactMaterial : ITrackedComponent
{
    public float NormalStiffness;
    public float NormalDamping;
    public float StaticFriction;
    public float DynamicFriction;
    public float Restitution;
    public float RestitutionThreshold;
}

public struct AvbdContactState
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
[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct ContactResponse
{
    [FieldOffset(0)] public float InitialNormalVelocity;
    /// <summary>World-space physical force on the second side, in N.</summary>
    [FieldOffset(16)] public Vector3 ForceOnSecond;
    /// <summary>Applied compressive normal impulse on the second side, in N·s.</summary>
    [FieldOffset(4)] public float RestitutionImpulse;
    [FieldOffset(32)] public double StartTime;
    [FieldOffset(40)] public double EndTime;
}
