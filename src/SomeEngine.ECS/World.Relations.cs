using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    public void AddRelation<T>(Entity source, Entity target, in T value)
        where T : struct, IRelation
    {
        _relations.Add(source, target, in value);
    }

    public void ReplaceRelation<T>(Entity source, Entity target, in T value)
        where T : struct, IRelation
    {
        _relations.Replace(source, target, in value);
    }

    public void RemoveRelation<T>(Entity source, Entity target)
        where T : struct, IRelation
    {
        _relations.Remove<T>(source, target);
    }

    public bool HasRelation<T>(Entity source, Entity target)
        where T : struct, IRelation
    {
        return _relations.Has<T>(source, target);
    }

    public ReadOnlySpan<RelationEntry<T>> GetRelations<T>(Entity source)
        where T : struct, IRelation
    {
        return _relations.Get<T>(source);
    }

    public ReadOnlySpan<Entity> GetRelationSources<T>(Entity target)
        where T : struct, IRelation
    {
        return _relations.Sources<T>(target);
    }

    public void RemoveAllRelations<T>(Entity source)
        where T : struct, IRelation
    {
        _relations.RemoveAll<T>(source);
    }

    public ReadOnlySpan<RelationChange<T>> RelationChanges<T>(uint lastVersion)
        where T : struct, IRelation
    {
        return _relations.Changes<T>(lastVersion);
    }

    internal void DropRelationTag<T>(Entity source)
        where T : struct, IRelation
    {
        _relations.DropTag(source, ComponentMetadata<RelationTag<T>>.Id);
    }
}

