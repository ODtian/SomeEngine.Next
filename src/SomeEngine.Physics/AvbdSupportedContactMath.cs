using System.Numerics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

public readonly struct AvbdSupportedContactMath : IAvbdContactMath<AvbdSupportedContactState>
{
    public static void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref AvbdSupportedContactState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention) =>
        AvbdContactMath.Initialize(in point, links, in material, ref state.Contact, bodies, in context, retention);
    public static void AccumulateBodyEquation(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, in AvbdSupportedContactState state, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdContactMath.AccumulateBodyEquation(in point, links, in material, in state.Contact, body, bodies,
            in context, initialErrorFraction, state.Support.ExtraForceOnSecond, ref equation);
    public static void UpdateDuals(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref AvbdSupportedContactState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref ContactResponse response) =>
        AvbdContactMath.UpdateDuals(in point, links, in material, ref state.Contact, bodies,
            in context, initialErrorFraction, state.Support.ExtraForceOnSecond, ref response);
}
