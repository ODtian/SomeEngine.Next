using System.Numerics;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

public enum JointEnd : byte
{
    Source,
    Target,
}

/// <summary>An auxiliary distance bound held by its first original joint entity.</summary>
[BufferCapacity(4)]
public struct LongRangeJointBound : IBufferElement
{
    public Entity OtherJoint;
    public JointEnd FirstEnd;
    public JointEnd OtherEnd;
    public float Minimum;
    public float Maximum;
    public Vector2 Multiplier;
    public float Penalty;
    public Vector2 InitialError;
}

public readonly record struct JointLongRangeSettings(int Levels, float PenaltyScale,
    float MultiplierRetention, float InitialErrorFraction);
