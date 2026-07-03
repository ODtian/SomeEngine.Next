using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hierarchy;

public static class OrderedHierarchy
{
    public static void Attach(World world, Entity child, Entity parent)
        => Hierarchy.AttachParent(world, child, parent, null, AttachChild, DetachChild);

    public static void Attach(World world, Entity child, Entity parent, int insertIndex)
        => Hierarchy.AttachParent(world, child, parent, insertIndex, AttachChild, DetachChild);

    public static void Move(World world, Entity child, Entity parent)
        => Hierarchy.MoveParent(world, child, parent, null, AttachChild, DetachChild);

    public static void Move(World world, Entity child, Entity parent, int insertIndex)
        => Hierarchy.MoveParent(world, child, parent, insertIndex, AttachChild, DetachChild);

    public static void Reorder(World world, Entity child, int insertIndex)
        => Hierarchy.ReorderChild(world, child, insertIndex, AttachChild, DetachChild);

    public static void Detach(World world, Entity child)
        => Hierarchy.DetachParent(world, child, DetachChild);

    public static Entity GetParent(World world, Entity child) =>
        Hierarchy.GetParent(world, child);

    public static ReadOnlySpan<Entity> GetChildren(World world, Entity parent) =>
        Hierarchy.GetChildren(world, parent);

    public static void DestroySubtree(World world, Entity root) =>
        Hierarchy.DestroySubtree(world, root, DetachChild);

    public static void Update(World world) =>
        Hierarchy.Update(world, AttachChild, DetachChild);

    private static void AttachChild(
        World world,
        Entity parent,
        Entity child,
        int? insertIndex
    )
    {
        ref var childBuffer = ref world.Get<ChildBuffer>(parent);
        if (childBuffer.Children.IndexOf(child) >= 0)
            return;

        if (insertIndex is null)
        {
            childBuffer.Children.Add(child);
            return;
        }

        childBuffer.Children.Insert(insertIndex.Value, child);
    }

    private static void DetachChild(World world, Entity parent, Entity child)
    {
        if (!world.IsAlive(parent) || !world.Has<ChildBuffer>(parent))
            return;

        ref var childBuffer = ref world.Get<ChildBuffer>(parent);
        childBuffer.Children.RemoveStable(child);
    }
}

