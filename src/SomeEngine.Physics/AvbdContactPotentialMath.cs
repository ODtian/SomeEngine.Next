// Contact potential metrics and positive angular curvature follow Giles AVBD through
// codex/job331-380:tools/SomeEngine.Physics.Research/Native/AvbdContact.cpp,
// researchPairedContactEquation.
// Copyright (c) 2026 Chris Giles. Permission to use, copy, modify, distribute and sell
// this software and its documentation for any purpose is hereby granted without fee,
// provided that the above copyright notice appear in all copies. The software is provided
// "as is" without express or implied warranty; the author makes no representations about
// its suitability for any purpose.
using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>Clamped contact potential metrics with a positive angular curvature bound.</summary>
public readonly struct AvbdContactPotentialMath : IAvbdConstraint<ContactPoint>
{
    public static int GetBodyCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        AvbdContactMath.GetBodyCount(in point, row, in context);
    public static Entity GetBody(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        AvbdContactMath.GetBody(in point, row, index, in context);
    public static bool TryGetBody(in ContactPoint point, scoped QueryRow row, int index, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Entity body) => AvbdContactMath.TryGetBody(in point, row, index, bodies, in context, out body);
    public static int GetReadEntityCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        AvbdContactMath.GetReadEntityCount(in point, row, in context);
    public static Entity GetReadEntity(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        AvbdContactMath.GetReadEntity(in point, row, index, in context);
    public static PhysicsConstraintError GetActivityError(in ContactPoint point, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context) => AvbdContactMath.GetActivityError(in point, row, bodies, in context);
    public static void Initialize(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) => AvbdContactMath.Initialize(in point, row, bodies, in context, retention);
    public static void AccumulateBodyEquation(in ContactPoint point, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdContactMath.AccumulatePotentialBodyEquation(in point, row, body, bodies, in context, initialErrorFraction,
            Vector3.Zero, false, ref equation);
    public static void UpdateDuals(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction) => AvbdContactMath.UpdateDuals(in point, row, bodies, in context, initialErrorFraction);
}

/// <summary>The same contact potential with the selected long-range support's force and height response.</summary>
public readonly struct AvbdSupportedContactPotentialMath : IAvbdConstraint<ContactPoint>
{
    public static int GetBodyCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        AvbdContactPotentialMath.GetBodyCount(in point, row, in context);
    public static Entity GetBody(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        AvbdContactPotentialMath.GetBody(in point, row, index, in context);
    public static bool TryGetBody(in ContactPoint point, scoped QueryRow row, int index, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Entity body) => AvbdContactPotentialMath.TryGetBody(in point, row, index, bodies, in context, out body);
    public static int GetReadEntityCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        AvbdContactPotentialMath.GetReadEntityCount(in point, row, in context);
    public static Entity GetReadEntity(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        AvbdContactPotentialMath.GetReadEntity(in point, row, index, in context);
    public static PhysicsConstraintError GetActivityError(in ContactPoint point, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context) => AvbdContactPotentialMath.GetActivityError(in point, row, bodies, in context);
    public static void Initialize(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) => AvbdContactPotentialMath.Initialize(in point, row, bodies, in context, retention);
    public static void AccumulateBodyEquation(in ContactPoint point, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly LongRangeContactSupport support = ref row.ReadRef<LongRangeContactSupport>();
        AvbdContactMath.AccumulatePotentialBodyEquation(in point, row, body, bodies, in context, initialErrorFraction,
            support.ExtraForceOnSecond, support.Enabled, ref equation);
    }
    public static void UpdateDuals(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction) => AvbdSupportedContactMath.UpdateDuals(in point, row, bodies, in context, initialErrorFraction);
}
