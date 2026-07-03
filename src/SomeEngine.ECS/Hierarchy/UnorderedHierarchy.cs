using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hierarchy;

public static class UnorderedHierarchy
{
    public static void Attach(World world, Entity child, Entity parent)
        => Hierarchy.AttachParent(world, child, parent, null, AttachChild, DetachChild);

    public static void Move(World world, Entity child, Entity parent)
        => Hierarchy.MoveParent(world, child, parent, null, AttachChild, DetachChild);

    public static void Detach(World world, Entity child)
        => Hierarchy.DetachParent(world, child, DetachChild);

    public static Entity GetParent(World world, Entity child) =>
        Hierarchy.GetParent(world, child);

    public static ReadOnlySpan<Entity> GetChildren(World world, Entity parent) =>
        Hierarchy.GetChildren(world, parent);

    public static void DestroySubtree(World world, Entity root) =>
        Hierarchy.DestroySubtree(world, root, DetachChild);

    public static void Update(World world) =>
        Hierarchy.UpdateUnordered(world);

    private static void AttachChild(World world, Entity parent, Entity child, int? insertIndex)
        => Hierarchy.AttachUnorderedChild(world, parent, child);

    private static void DetachChild(World world, Entity parent, Entity child)
        => Hierarchy.DetachUnorderedChild(world, parent, child);
}

