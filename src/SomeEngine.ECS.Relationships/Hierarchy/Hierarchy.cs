using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hierarchy;

public static class Hierarchy<TDomain> where TDomain : struct, IHierarchyDomain
{
    internal static HierarchyData<TDomain> Data(World world)
    {
        if (world.FindLifecycle<HierarchyData<TDomain>>() is { } found) return found;
        world.RequireStructure();
        var data = new HierarchyData<TDomain>(world);
        world.AddLifecycle(data);
        data.Initialize();
        return data;
    }

    public static Entity GetParent(World world, Entity child)
    {
        using WorldAccess access = world.OpenStructure(false);
        return HierarchyData<TDomain>.ReadParent(world, child);
    }

    public static ChildrenRead GetChildren(World world, Entity parent)
    {
        world.BeginStructureRead();
        return new(world, HierarchyData<TDomain>.ReadChildren(world, parent).Entities);
    }

    public static DescendantEntities<TDomain> GetDescendants(World world, Entity root) => new(world, root);

    public static void SetParent(World world, Entity child, Entity parent, int insertAt = -1)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).SetParent(child, parent, false, insertAt);
    }

    public static void SetParentDeferred(World world, Entity child, Entity parent, int insertAt = -1)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).SetParent(child, parent, true, insertAt);
    }

    public static void Detach(World world, Entity child) => SetParent(world, child, Entity.Null);
    public static void DetachDeferred(World world, Entity child) => SetParentDeferred(world, child, Entity.Null);
    public static void Maintain(World world)
    {
        using WorldAccess access = world.OpenStructure();
        world.FindLifecycle<HierarchyData<TDomain>>()?.Maintain();
    }
    public static void SetParents(World world, ReadOnlySpan<ParentEdit> edits, bool deferred = false)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).SetParents(edits, deferred);
    }
    public static void SetOrdered(World world, Entity parent, bool ordered, ReadOnlySpan<Entity> order = default)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).SetOrdered(parent, ordered, order);
    }
    public static bool IsOrdered(World world, Entity parent)
    {
        using WorldAccess access = world.OpenStructure(false);
        return world.FindLifecycle<HierarchyData<TDomain>>()?.IsOrdered(parent) ?? false;
    }
    public static void DestroySubtree(World world, Entity root)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).DestroySubtree(root);
    }
}

public static class Hierarchy
{
    public static Entity GetParent(World world, Entity child) => Hierarchy<DefaultHierarchyDomain>.GetParent(world, child);
    public static ChildrenRead GetChildren(World world, Entity parent) => Hierarchy<DefaultHierarchyDomain>.GetChildren(world, parent);
    public static DescendantEntities<DefaultHierarchyDomain> GetDescendants(World world, Entity root)
        => Hierarchy<DefaultHierarchyDomain>.GetDescendants(world, root);
    public static void SetParent(World world, Entity child, Entity parent, int insertAt = -1)
        => Hierarchy<DefaultHierarchyDomain>.SetParent(world, child, parent, insertAt);
    public static void SetParentDeferred(World world, Entity child, Entity parent, int insertAt = -1)
        => Hierarchy<DefaultHierarchyDomain>.SetParentDeferred(world, child, parent, insertAt);
    public static void Detach(World world, Entity child) => Hierarchy<DefaultHierarchyDomain>.Detach(world, child);
    public static void Maintain(World world) => Hierarchy<DefaultHierarchyDomain>.Maintain(world);
    public static void DestroySubtree(World world, Entity root) => Hierarchy<DefaultHierarchyDomain>.DestroySubtree(world, root);
}
