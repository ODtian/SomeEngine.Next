using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

internal enum SerializationChangeKind : byte
{
    EntityCreated,
    EntityDestroyed,
    ComponentAdded,
    ComponentRemoved,
    ComponentChanged,
    TagAdded,
    TagRemoved,
    EnabledChanged,
    SharedChanged,
    SharedAdded,
    SharedRemoved,
    BufferChanged,
    BufferAdded,
    BufferRemoved,
    SparseAdded,
    SparseRemoved,
    SparseChanged,
    RelationAdded,
    RelationRemoved,
    RelationChanged,
}

internal readonly record struct SerializationChangeEvent(
    SerializationChangeKind Kind,
    Entity Entity,
    int ComponentId,
    Entity Target,
    uint Version);

