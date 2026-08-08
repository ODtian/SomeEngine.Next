using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>
    /// Entity destruction must call this before component removal/free. It detaches the entity
    /// from every registered domain and orphans canonical direct children in each domain.
    /// </summary>
    internal void OnHierarchyEntityDestroying(Entity entity)
    {
        _hierarchy.OnEntityDestroying(entity);
    }

    /// <summary>
    /// Owner-bound Parent query writers call this before releasing their resource grant.
    /// </summary>
    internal void ValidateDeferredHierarchyWrites()
    {
        _hierarchy.ValidateDeferredWrites();
    }

    /// <summary>
    /// Owner-bound Parent query writers call this on body fault/cancel before releasing access.
    /// </summary>
    internal void RollbackDeferredHierarchyWrites()
    {
        _hierarchy.RollbackDeferredWrites();
    }
}
