using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

internal abstract class ComponentCopy
{
    internal abstract void Commit(World world);
}

internal sealed class ComponentCopy<T>(Entity entity, T value) : ComponentCopy where T : struct
{
    internal T Value = value;

    internal static bool HasComponent(World world, Entity entity) => default(T) is ISparseComponent
        ? world.HasSparse<T>(entity)
        : world.Has<T>(entity);

    internal override void Commit(World world)
    {
        bool exists = HasComponent(world, entity);
        if (exists && EqualityComparer<T>.Default.Equals(world.ReadComponentData<T>(entity), Value))
            return;
        if (default(T) is ISparseComponent)
        {
            if (exists)
                world.ReplaceSparse(entity, in Value);
            else
                world.AddSparse(entity, in Value);
        }
        else
        {
            if (exists)
                world.Replace(entity, in Value);
            else
                world.Add(entity, in Value);
        }
    }
}
