using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

/// <summary>Component rows borrowed for the bodies selected by a physics stage.</summary>
public readonly ref struct PhysicsBodies
{
    private readonly QueryCursor _bodies;
    private readonly EntityQuery _reads;
    private readonly EntityQuery _writes;
    private readonly EntityQuery _references;
    private readonly bool _entityQueries;

    public PhysicsBodies(QueryCursor bodies)
    {
        _bodies = bodies;
        _reads = _writes = _references = default;
        _entityQueries = false;
    }

    public PhysicsBodies(EntityQuery reads, EntityQuery writes, EntityQuery references)
    {
        _bodies = default;
        _reads = reads;
        _writes = writes;
        _references = references;
        _entityQueries = true;
    }

    public QueryRow GetRow(Entity entity)
    {
        QueryRow row;
        if (_entityQueries)
        {
            if (!_writes.TryGetRow(entity, out row)) _reads.TryGetRow(entity, out row);
        }
        else _bodies.TryGetRow(entity, out row);
        return row;
    }

    public bool TryReadReference<T>(Entity entity, out ComponentReference<T> component) where T : struct
    {
        if (_entityQueries) return _references.TryReadRef(entity, out component);
        return _bodies.TryReadRef(entity, out component);
    }

    public ref readonly BodyPose ReadPose(Entity entity) => ref GetRow(entity).ReadRef<BodyPose>();
    public ref BodyPose WritePose(Entity entity) => ref GetRow(entity).ReadWrite<BodyPose>();
    public ref readonly BodyMass ReadMass(Entity entity) => ref GetRow(entity).ReadRef<BodyMass>();
    public ref readonly BodyStepState ReadStepState(Entity entity) => ref GetRow(entity).ReadRef<BodyStepState>();
}
