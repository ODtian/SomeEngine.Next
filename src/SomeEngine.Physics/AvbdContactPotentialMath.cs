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

namespace SomeEngine.Physics;

public readonly struct AvbdContactPotentialMath : IAvbdContactMath<AvbdContactState>
{
    public static void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref AvbdContactState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) =>
        AvbdContactMath.Initialize(in point, links, in material, ref state, bodies, in context, retention);
    public static void AccumulateBodyEquation(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, in AvbdContactState state, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdContactMath.AccumulatePotentialBodyEquation(in point, links, in material, in state, body, bodies,
            in context, initialErrorFraction, Vector3.Zero, false, ref equation);
    public static void UpdateDuals(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref AvbdContactState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref ContactResponse response) =>
        AvbdContactMath.UpdateDuals(in point, links, in material, ref state, bodies,
            in context, initialErrorFraction, Vector3.Zero, ref response);
}

public readonly struct AvbdSupportedContactPotentialMath : IAvbdContactMath<AvbdSupportedContactState>
{
    public static void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref AvbdSupportedContactState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) =>
        AvbdContactMath.Initialize(in point, links, in material, ref state.Contact, bodies, in context, retention);
    public static void AccumulateBodyEquation(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, in AvbdSupportedContactState state, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdContactMath.AccumulatePotentialBodyEquation(in point, links, in material, in state.Contact, body, bodies,
            in context, initialErrorFraction, state.Support.ExtraForceOnSecond, state.Support.Enabled, ref equation);
    public static void UpdateDuals(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref AvbdSupportedContactState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref ContactResponse response) =>
        AvbdContactMath.UpdateDuals(in point, links, in material, ref state.Contact, bodies,
            in context, initialErrorFraction, state.Support.ExtraForceOnSecond, ref response);
}
