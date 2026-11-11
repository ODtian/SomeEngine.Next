using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public enum EndpointEditKind { Retarget, Create, Destroy }

public readonly record struct EndpointEdit(Entity Edge, Entity Source, Entity Target, EndpointEditKind Kind = EndpointEditKind.Retarget)
{
    public static Entity CreatedInBatch(int index) => new(-index - 1, 0);
    public static EndpointEdit Create(Entity source, Entity target) => new(Entity.Null, source, target, EndpointEditKind.Create);
    public static EndpointEdit Destroy(Entity edge) => new(edge, Entity.Null, Entity.Null, EndpointEditKind.Destroy);
}
