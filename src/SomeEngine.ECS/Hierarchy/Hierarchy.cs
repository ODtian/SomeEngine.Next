using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hierarchy;

/// <summary>
/// Typed Parent/Children operations for one independent hierarchy domain.
/// </summary>
public static class Hierarchy<TDomain>
    where TDomain : IHierarchyDomain
{
    /// <summary>
    /// Sets canonical Parent and synchronously applies the inverse Children transition.
    /// </summary>
    public static void SetParent(World world, Entity child, Entity parent)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().SetParent(child, parent, insertIndex: null, immediate: true);
    }

    /// <summary>
    /// Sets canonical Parent and inserts the child at an explicit index of an ordered parent.
    /// </summary>
    public static void SetParent(World world, Entity child, Entity parent, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().SetParent(child, parent, insertIndex, immediate: true);
    }

    /// <summary>
    /// Removes canonical Parent and synchronously updates the previous parent's Children.
    /// </summary>
    public static void Detach(World world, Entity child)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().Detach(child, immediate: true);
    }

    /// <summary>
    /// Sets canonical Parent while leaving Children at its last-applied generation.
    /// </summary>
    public static void SetParentDeferred(World world, Entity child, Entity parent)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().SetParent(child, parent, insertIndex: null, immediate: false);
    }

    /// <summary>
    /// Sets canonical Parent immediately and records an exact insertion index for deferred
    /// Children maintenance on an ordered parent.
    /// </summary>
    public static void SetParentDeferred(
        World world,
        Entity child,
        Entity parent,
        int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().SetParent(child, parent, insertIndex, immediate: false);
    }

    /// <summary>
    /// Removes canonical Parent while leaving Children at its last-applied generation.
    /// </summary>
    public static void DetachDeferred(World world, Entity child)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().Detach(child, immediate: false);
    }

    /// <summary>
    /// Applies every valid deferred Parent transition through the shared transition kernel.
    /// </summary>
    public static void Maintain(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().Maintain();
    }

    public static Entity GetParent(World world, Entity child)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission =
            world.EnterJobComponent<Parent<TDomain>>(WorldStorageAccess.Read);
        if (world.Hierarchy.TryDomain<TDomain>(out var store))
            return store.GetParent(child);

        world.Hierarchy.EnsureAlive(child, "child");
        return Entity.Null;
    }

    /// <summary>
    /// Captures a safe, read-only snapshot of the currently applied direct-child generation.
    /// </summary>
    public static HierarchyChildrenSnapshot<TDomain> GetChildren(World world, Entity parent)
    {
        ArgumentNullException.ThrowIfNull(world);
        World.ThrowIfRestrictedWorldApi();
        return world.Hierarchy.GetChildren<TDomain>(parent);
    }

    public static ChildOrderPolicy GetChildOrderPolicy(World world, Entity parent)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyRead();
        if (world.Hierarchy.TryDomain<TDomain>(out var store))
            return store.GetOrderPolicy(parent);

        world.Hierarchy.EnsureAlive(parent, "parent");
        return ChildOrderPolicy.Unordered;
    }

    public static void SetChildOrderPolicy(
        World world,
        Entity parent,
        ChildOrderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().SetOrderPolicy(parent, policy);
    }

    public static void SetChildOrderPolicy(
        World world,
        Entity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<Entity> permutation)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().SetOrderPolicy(parent, policy, permutation);
    }

    public static void Reorder(World world, Entity child, int insertIndex)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().Reorder(child, insertIndex);
    }

    /// <summary>
    /// Explicitly destroys root and every canonical descendant in this domain.
    /// Other registered domains are cleaned through the ordinary entity-destroy hook.
    /// </summary>
    public static void DestroySubtree(World world, Entity root)
    {
        ArgumentNullException.ThrowIfNull(world);
        using WorldJobAdmissionScope admission = world.EnterJobTopologyWrite();
        world.Hierarchy.Domain<TDomain>().DestroySubtree(world, root);
    }
}

/// <summary>
/// Convenience facade for <see cref="DefaultHierarchyDomain"/>. It owns no separate state.
/// </summary>
public static class Hierarchy
{
    public static void SetParent(World world, Entity child, Entity parent) =>
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, child, parent);

    public static void SetParent(World world, Entity child, Entity parent, int insertIndex) =>
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, child, parent, insertIndex);

    public static void Detach(World world, Entity child) =>
        Hierarchy<DefaultHierarchyDomain>.Detach(world, child);

    public static void SetParentDeferred(World world, Entity child, Entity parent) =>
        Hierarchy<DefaultHierarchyDomain>.SetParentDeferred(world, child, parent);

    public static void SetParentDeferred(
        World world,
        Entity child,
        Entity parent,
        int insertIndex) =>
        Hierarchy<DefaultHierarchyDomain>.SetParentDeferred(world, child, parent, insertIndex);

    public static void DetachDeferred(World world, Entity child) =>
        Hierarchy<DefaultHierarchyDomain>.DetachDeferred(world, child);

    public static void Maintain(World world) =>
        Hierarchy<DefaultHierarchyDomain>.Maintain(world);

    public static Entity GetParent(World world, Entity child) =>
        Hierarchy<DefaultHierarchyDomain>.GetParent(world, child);

    public static HierarchyChildrenSnapshot<DefaultHierarchyDomain> GetChildren(
        World world,
        Entity parent) =>
        Hierarchy<DefaultHierarchyDomain>.GetChildren(world, parent);

    public static ChildOrderPolicy GetChildOrderPolicy(World world, Entity parent) =>
        Hierarchy<DefaultHierarchyDomain>.GetChildOrderPolicy(world, parent);

    public static void SetChildOrderPolicy(
        World world,
        Entity parent,
        ChildOrderPolicy policy) =>
        Hierarchy<DefaultHierarchyDomain>.SetChildOrderPolicy(world, parent, policy);

    public static void SetChildOrderPolicy(
        World world,
        Entity parent,
        ChildOrderPolicy policy,
        ReadOnlySpan<Entity> permutation) =>
        Hierarchy<DefaultHierarchyDomain>.SetChildOrderPolicy(world, parent, policy, permutation);

    public static void Reorder(World world, Entity child, int insertIndex) =>
        Hierarchy<DefaultHierarchyDomain>.Reorder(world, child, insertIndex);

    public static void DestroySubtree(World world, Entity root) =>
        Hierarchy<DefaultHierarchyDomain>.DestroySubtree(world, root);
}
