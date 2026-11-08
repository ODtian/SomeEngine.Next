// Connected cone regions, swing/twist limits and preferred-path correction adapted from Everything-Will-Be-IK.
// Copyright (c) 2016 Eron Gjoni. MIT license: external/ewbik/LICENSE.
using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>A unit direction in the limiting frame and a spherical cap radius in radians.</summary>
public readonly record struct KusudamaCone(Vector3 Axis, float Radius);
/// <summary>An own-bone constraint and its initial preferred-path angular weight.</summary>
public readonly record struct EwbikConstraint(int Bone, Kusudama Limit, float PreferenceWeight = 0);

/// <summary>Shared connected swing caps/corridors and a signed axial twist interval.</summary>
public sealed class Kusudama
{
    private readonly KusudamaCone[] _cones;
    private readonly Cone[] _caps;
    private readonly Corridor[] _corridors;
    public ReadOnlySpan<KusudamaCone> Cones => _cones;
    /// <summary>Unit rotation from the limiting frame to the bone's parent coordinates.</summary>
    public Quaternion LimitingFrame { get; }
    /// <summary>Unit bone-local axis whose direction defines swing, and about which twist is measured.</summary>
    public Vector3 PrimaryAxis { get; }
    public float MinimumTwist { get; }
    public float MaximumTwist { get; }

    /// <summary>Ordered caps with distinct, non-antipodal neighbors and a contiguous signed twist interval in [-pi, pi].</summary>
    public Kusudama(ReadOnlySpan<KusudamaCone> cones, Quaternion limitingFrame, Vector3 primaryAxis,
        float minimumTwist, float maximumTwist)
    {
        _cones = cones.ToArray();
        _caps = new Cone[cones.Length];
        _corridors = new Corridor[cones.Length - 1];
        for (int index = 0; index < cones.Length; index++)
        {
            KusudamaCone cone = cones[index];
            _caps[index] = new(cone.Axis, MathF.Cos(cone.Radius), MathF.Sin(cone.Radius));
            if (index + 1 < cones.Length) _corridors[index] = new(cone, cones[index + 1]);
        }
        LimitingFrame = limitingFrame;
        PrimaryAxis = primaryAxis;
        MinimumTwist = minimumTwist;
        MaximumTwist = maximumTwist;
    }

    internal Quaternion Apply(Quaternion localRotation, float angularStep, float preferenceWeight)
    {
        Quaternion relative = HardLimit(Quaternion.Conjugate(LimitingFrame) * localRotation);
        if (preferenceWeight > 0)
        {
            float step = angularStep * preferenceWeight;
            Vector3 direction = Vector3.Transform(PrimaryAxis, relative);
            Quaternion turn = FabrikMath.FromTo(direction, PreferredDirection(direction));
            float angle = 2 * MathF.Atan2(new Vector3(turn.X, turn.Y, turn.Z).Length(), turn.W);
            if (angle > step) turn = Quaternion.Slerp(Quaternion.Identity, turn, step / angle);
            relative = turn * relative;
            float twist = Twist(relative), middle = (MinimumTwist + MaximumTwist) * .5f;
            relative *= Quaternion.CreateFromAxisAngle(PrimaryAxis, Math.Clamp(middle - twist, -step, step));
            relative = HardLimit(relative);
        }
        return Quaternion.Normalize(LimitingFrame * relative);
    }

    private Quaternion HardLimit(Quaternion relative)
    {
        Vector3 direction = Vector3.Transform(PrimaryAxis, relative);
        relative = FabrikMath.FromTo(direction, LimitDirection(direction)) * relative;
        float twist = Twist(relative);
        relative *= Quaternion.CreateFromAxisAngle(PrimaryAxis, Math.Clamp(twist, MinimumTwist, MaximumTwist) - twist);
        return Quaternion.Normalize(relative);
    }

    private float Twist(Quaternion rotation) => MathF.IEEERemainder(
        2 * MathF.Atan2(Vector3.Dot(new(rotation.X, rotation.Y, rotation.Z), PrimaryAxis), rotation.W), 2 * MathF.PI);

    private Vector3 LimitDirection(Vector3 direction)
    {
        Vector3 closest = default;
        float closestCosine = -2;
        foreach (Cone cone in _caps)
        {
            if (Vector3.Dot(direction, cone.Axis) >= cone.Cosine) return direction;
            Select(cone.Boundary(direction));
        }
        foreach (Corridor corridor in _corridors)
        {
            Cone circle = corridor.Circle(direction);
            if (!corridor.InWedge(direction, circle.Axis)) continue;
            if (Vector3.Dot(direction, circle.Axis) <= circle.Cosine) return direction;
            Select(circle.Boundary(direction));
        }
        return closest;

        void Select(Vector3 candidate)
        {
            float cosine = Vector3.Dot(candidate, direction);
            if (cosine > closestCosine) { closest = candidate; closestCosine = cosine; }
        }
    }

    private Vector3 PreferredDirection(Vector3 direction)
    {
        Vector3 closest = _caps[0].Axis;
        float closestCosine = Vector3.Dot(closest, direction);
        foreach (Cone cap in _caps) Select(cap.Axis);
        foreach (Corridor corridor in _corridors)
        {
            Cone circle = corridor.Circle(direction);
            if (!corridor.InWedge(direction, circle.Axis)) continue;
            Vector3 ray = direction - circle.Axis;
            float distance = -Vector3.Dot(corridor.Normal, circle.Axis) / Vector3.Dot(corridor.Normal, ray);
            Select(Vector3.Normalize(circle.Axis + ray * distance));
        }
        return closest;

        void Select(Vector3 candidate)
        {
            float cosine = Vector3.Dot(candidate, direction);
            if (cosine > closestCosine) { closest = candidate; closestCosine = cosine; }
        }
    }

    private readonly record struct Cone(Vector3 Axis, float Cosine, float Sine)
    {
        internal Vector3 Boundary(Vector3 direction)
        {
            Vector3 tangent = direction - Axis * Vector3.Dot(Axis, direction);
            tangent = tangent.LengthSquared() > 0 ? Vector3.Normalize(tangent) : FabrikMath.Perpendicular(Axis);
            return Vector3.Normalize(Axis * Cosine + tangent * Sine);
        }
    }

    private readonly struct Corridor
    {
        internal readonly Vector3 Normal;
        private readonly Vector3 _positiveFirst, _positiveSecond, _negativeFirst, _negativeSecond;
        private readonly Cone _positive, _negative;

        internal Corridor(KusudamaCone first, KusudamaCone second)
        {
            Vector3 firstAxis = first.Axis, secondAxis = second.Axis;
            Normal = Vector3.Normalize(Vector3.Cross(firstAxis, secondAxis));
            float radius = (MathF.PI - first.Radius - second.Radius) * .5f;
            float dot = Vector3.Dot(firstAxis, secondAxis), denominator = 1 - dot * dot;
            float a = MathF.Cos(first.Radius + radius), b = MathF.Cos(second.Radius + radius);
            Vector3 foot = firstAxis * ((a - dot * b) / denominator) + secondAxis * ((b - dot * a) / denominator);
            Vector3 height = Normal * MathF.Sqrt(1 - foot.LengthSquared());
            _positive = new(foot + height, MathF.Cos(radius), MathF.Sin(radius));
            _negative = new(foot - height, MathF.Cos(radius), MathF.Sin(radius));
            _positiveFirst = Vector3.Cross(_positive.Axis, firstAxis);
            _positiveSecond = Vector3.Cross(secondAxis, _positive.Axis);
            _negativeFirst = Vector3.Cross(firstAxis, _negative.Axis);
            _negativeSecond = Vector3.Cross(_negative.Axis, secondAxis);
        }

        internal Cone Circle(Vector3 direction) => Vector3.Dot(direction, Normal) >= 0 ? _positive : _negative;

        internal bool InWedge(Vector3 direction, Vector3 circle) => Vector3.Dot(circle, Normal) >= 0
            ? Vector3.Dot(direction, _positiveFirst) >= 0 && Vector3.Dot(direction, _positiveSecond) >= 0
            : Vector3.Dot(direction, _negativeFirst) >= 0 && Vector3.Dot(direction, _negativeSecond) >= 0;
    }
}
