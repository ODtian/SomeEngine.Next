using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly partial struct AvbdDistanceRangeMath
{


    public static PhysicsConstraintError GetActivityError(in DistanceRange range, in RelationEndpoints<DistanceRange> endpoints,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        Geometry geometry = Read(in range, in endpoints, bodies, false);
        return new(MathF.Max(0, MathF.Max(range.Minimum - geometry.Distance, geometry.Distance - range.Maximum)), 0);
    }
    public static void Initialize(in DistanceRange range, in RelationEndpoints<DistanceRange> endpoints, ref AvbdDistanceRangeState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        Geometry start = Read(in range, in endpoints, bodies, true);
        state.Multiplier *= retention;
        state.InitialError = new(MathF.Max(0, start.Distance - range.Maximum), MathF.Max(0, range.Minimum - start.Distance));
    }
    public static void AccumulateBodyEquation(in DistanceRange range, in RelationEndpoints<DistanceRange> endpoints, ref AvbdDistanceRangeState state, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        Geometry geometry = Read(in range, in endpoints, bodies, false);
        DistanceRangeMath.Evaluate(range.Minimum, range.Maximum, range.Stiffness, state.Penalty,
            state.Multiplier, state.InitialError, initialErrorFraction, geometry.Distance, out float force, out float stiffness, out _);
        bool second = body == endpoints.Target;
        DistanceRangeMath.Accumulate(geometry.Delta, geometry.Distance, second ? geometry.SecondOffset : geometry.FirstOffset,
            second ? 1 : -1, force, stiffness, ref equation);
    }
    public static void UpdateDuals(in DistanceRange range, in RelationEndpoints<DistanceRange> endpoints, ref AvbdDistanceRangeState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        Geometry geometry = Read(in range, in endpoints, bodies, false);
        DistanceRangeMath.Evaluate(range.Minimum, range.Maximum, range.Stiffness, state.Penalty,
            state.Multiplier, state.InitialError, initialErrorFraction, geometry.Distance, out _, out _, out Vector2 multiplier);
        state.Multiplier = multiplier;
    }
    public static void GetBodyForce(in DistanceRange range, in RelationEndpoints<DistanceRange> endpoints, ref AvbdDistanceRangeState state, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        Geometry geometry = Read(in range, in endpoints, bodies, false);
        bool second = body == endpoints.Target;
        Vector3 direction = geometry.Distance > 0 ? geometry.Delta / geometry.Distance : Vector3.Zero;
        force = direction * ((second ? -1 : 1) * (state.Multiplier.X - state.Multiplier.Y));
        torque = Vector3.Cross(second ? geometry.SecondOffset : geometry.FirstOffset, force);
    }

    private static Geometry Read(in DistanceRange range, in RelationEndpoints<DistanceRange> endpoints, scoped PhysicsBodies bodies, bool start)
    {
        DistanceRangeMath.ReadAnchor(endpoints.Source, range.FirstAnchor, bodies, start, out Vector3 first, out Vector3 firstOffset);
        DistanceRangeMath.ReadAnchor(endpoints.Target, range.SecondAnchor, bodies, start, out Vector3 second, out Vector3 secondOffset);
        Vector3 delta = second - first;
        return new(delta, delta.Length(), firstOffset, secondOffset);
    }
    private readonly record struct Geometry(Vector3 Delta, float Distance, Vector3 FirstOffset, Vector3 SecondOffset);
}
