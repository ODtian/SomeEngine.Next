using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

internal abstract class ComponentCopy
{
    internal bool Written = true;
    internal abstract void Commit(World world, ChildChanges changes, bool reuseData);
    internal abstract void Clear();
}

internal sealed class ComponentCopy<T>(Entity entity, T value) : ComponentCopy where T : struct
{
    private ChildChanges.ChildComponent<T>? _previousData;
    internal T Value = value;

    internal static bool HasComponent(World world, Entity entity) => world.HasComponentData<T>(entity);

    internal override void Clear()
    {
        Value = default;
        Written = false;
        _previousData?.Clear();
    }

    internal override void Commit(World world, ChildChanges changes, bool reuseData)
    {
        bool exists = HasComponent(world, entity);
        if (exists && EqualityComparer<T>.Default.Equals(world.ReadComponentData<T>(entity), Value))
            return;
        if (reuseData)
            changes.RememberComponent(world, entity, ref _previousData);
        else
            changes.RememberComponent<T>(world, entity);
        if (exists)
            world.ReplaceComponentData(entity, in Value);
        else
            world.AddComponentData(entity, in Value);
    }
}
