using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

public struct LongRangeBodySupport : IComponent
{
    public float MinimumHeight;
    public float InitialError;
    public float Reaction;
    public bool Enabled;
    public bool Moving;
}

public struct LongRangeContactSupport : IComponent
{
    public float MinimumHeight;
    public float InitialError;
    public float HeightShift;
    public float Reaction;
    public float Load;
    public bool Enabled;
    public bool UpperIsSecond;
    /// <summary>The LRC contribution to world force on the second side, in N.</summary>
    public Vector3 ExtraForceOnSecond;
}

[BufferCapacity(3)]
public readonly record struct LongRangeSupportLoad(Entity Contact, float Weight) : IBufferElement;

/// <summary>A body's local height equation and response through its current six-dimensional factorization.</summary>
[BufferCapacity(4)]
public struct LongRangeSupportEquation : IBufferElement
{
    public Entity Contact;
    public Vector3 LinearGradient;
    public Vector3 AngularGradient;
    public Vector3 TranslationResponse;
    public Vector3 RotationResponse;
    public float HeightError;
    public float Reaction;
}

public readonly record struct ContactSupportSettings(Vector3 UpDirection, float DistanceTolerance,
    float MotionTolerance, float AngularMotionTolerance, float StickDistance,
    float MultiplierRetention, float InitialErrorFraction, float RelativePivotTolerance);
