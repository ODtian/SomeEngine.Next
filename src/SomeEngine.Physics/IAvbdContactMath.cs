using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>Concrete AVBD equations over one contact geometry and its numerical state.</summary>
public interface IAvbdContactMath<TState> where TState : unmanaged
{
    static abstract void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref TState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention);
    static abstract void AccumulateBodyEquation(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, in TState state, Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation);
    static abstract void UpdateDuals(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        in ContactMaterial material, ref TState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, ref ContactResponse response);
}

public struct AvbdSupportedContactState
{
    public AvbdContactState Contact;
    public LongRangeContactSupport Support;
}
