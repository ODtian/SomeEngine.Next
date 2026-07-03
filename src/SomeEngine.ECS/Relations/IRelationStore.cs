using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal interface IRelationStore
{
    int RelationTagId { get; }

    bool HasOutgoing(Entity source);

    void OnEntityDestroyed(Entity entity, uint version, List<RelationDrop> drops);

    void RemoveAllOutgoing(SomeEngine.ECS.Owners.Relations relations, Entity source);

    void AddOutgoingCopy(SomeEngine.ECS.Owners.Relations relations, Entity source, Entity target);
}

internal readonly record struct RelationDrop(Entity Source, int TagId);

