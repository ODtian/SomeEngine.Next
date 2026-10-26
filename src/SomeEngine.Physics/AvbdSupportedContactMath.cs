using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>The standard contact rows with the selected LRC auxiliary force in their friction projection.</summary>
public readonly struct AvbdSupportedContactMath : IAvbdConstraint<ContactPoint>
{
    public static PhysicsConstraintError GetActivityError(in ContactPoint point, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context) => ContactBodyMath.GetActivityError(in point, row, bodies, in context);

    public static int GetReadEntityCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        AvbdContactMath.GetReadEntityCount(in point, row, in context);
    public static Entity GetReadEntity(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        AvbdContactMath.GetReadEntity(in point, row, index, in context);
    public static int GetBodyCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        AvbdContactMath.GetBodyCount(in point, row, in context);
    public static Entity GetBody(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        AvbdContactMath.GetBody(in point, row, index, in context);
    public static bool TryGetBody(in ContactPoint point, scoped QueryRow row, int index, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Entity body) => AvbdContactMath.TryGetBody(in point, row, index, bodies, in context, out body);
    public static void Initialize(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) => AvbdContactMath.Initialize(in point, row, bodies, in context, retention);
    public static void AccumulateBodyEquation(in ContactPoint point, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdContactMath.AccumulateBodyEquation(in point, row, body, bodies, in context, initialErrorFraction,
            row.ReadRef<LongRangeContactSupport>().ExtraForceOnSecond, ref equation);
    public static void UpdateDuals(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction) =>
        AvbdContactMath.UpdateDuals(in point, row, bodies, in context, initialErrorFraction,
            row.ReadRef<LongRangeContactSupport>().ExtraForceOnSecond);
}
