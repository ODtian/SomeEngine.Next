using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

internal abstract class ComponentCopy
{
    internal abstract void Commit(World world, ChildChanges changes);
}

internal sealed class ComponentCopy<T>(Entity entity, T value) : ComponentCopy where T : struct
{
    internal T Value = value;

    internal static bool HasComponent(World world, Entity entity) => world.HasComponentData<T>(entity);

    internal override void Commit(World world, ChildChanges changes)
    {
        bool exists = HasComponent(world, entity);
        if (exists && EqualityComparer<T>.Default.Equals(world.ReadComponentData<T>(entity), Value))
            return;
        changes.RememberComponent<T>(world, entity);
        if (exists)
            world.ReplaceComponentData(entity, in Value);
        else
            world.AddComponentData(entity, in Value);
    }
}
