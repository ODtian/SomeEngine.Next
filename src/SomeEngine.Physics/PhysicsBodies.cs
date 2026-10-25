using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>Component rows borrowed for the bodies selected by a physics stage.</summary>
public readonly ref struct PhysicsBodies(QueryCursor bodies)
{
    private readonly QueryCursor _bodies = bodies;

    public QueryRow GetRow(Entity entity)
    {
        _bodies.TryGetRow(entity, out QueryRow row);
        return row;
    }

    public ref readonly BodyPose ReadPose(Entity entity) => ref GetRow(entity).ReadRef<BodyPose>();
    public ref BodyPose WritePose(Entity entity) => ref GetRow(entity).ReadWrite<BodyPose>();
    public ref readonly BodyMass ReadMass(Entity entity) => ref GetRow(entity).ReadRef<BodyMass>();
    public ref readonly BodyStepState ReadStepState(Entity entity) => ref GetRow(entity).ReadRef<BodyStepState>();
}
