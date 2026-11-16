using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

internal static class ReferencePatchers
{
    internal static IReferencePatcher<T>? DefaultValue<T>() where T : struct
    {
        if (typeof(T) == typeof(Entity)) return (IReferencePatcher<T>)(object)new EntityReferencePatcher();
        return EntityFields<T>.Operations is null ? null : new GeneratedReferencePatcher<T>();
    }

    internal static IReferencePatcher<T> Bind<T, TPatcher>(TPatcher patcher)
        where TPatcher : struct, IReferencePatcher<T> => new BoundReferencePatcher<T, TPatcher>(patcher);

    internal static IReferencePatcher<Removed<T>>? Removed<T>(IReferencePatcher<T>? patcher) where T : struct
        => patcher is IGeneratedReferencePatcher<T> ? new GeneratedReferencePatcher<Removed<T>>()
            : patcher is null ? null : new RemovedReferencePatcher<T>(patcher);
}

internal interface IGeneratedReferencePatcher<T>
{
    void Remap(ref T value, in EntityMap entities);
}

internal sealed class EntityReferencePatcher : IReferencePatcher<Entity>
{
    public void Remap(ref Entity value, IReferenceRemapper remapper)
    { remapper.TryMap(value, out Entity mapped); value = mapped; }
}

internal sealed class BoundReferencePatcher<T, TPatcher>(TPatcher patcher) : IReferencePatcher<T>
    where TPatcher : struct, IReferencePatcher<T>
{
    public void Remap(ref T value, IReferenceRemapper remapper)
    { TPatcher item = patcher; item.Remap(ref value, remapper); }
}

internal sealed class GeneratedReferencePatcher<T> : IReferencePatcher<T>, IGeneratedReferencePatcher<T> where T : struct
{
    public void Remap(ref T value, in EntityMap entities) => EntityFields<T>.Operations!.Remap(ref value, entities);

    public void Remap(ref T value, IReferenceRemapper remapper)
    {
        var mapper = new LoadedEntityMapper(remapper);
        var entities = EntityMap.Create(ref mapper);
        EntityFields<T>.Operations!.Remap(ref value, entities);
    }
}

internal sealed class BufferReferencePatcher<T>(IReferencePatcher<T> element) : IReferencePatcher<Buffer<T>> where T : struct
{
    public void Remap(ref Buffer<T> value, IReferenceRemapper remapper)
    {
        for (int i = 0; i < value.Count; i++) element.Remap(ref value.Items![i], remapper);
    }
}

internal sealed class RemovedReferencePatcher<T>(IReferencePatcher<T> previous) : IReferencePatcher<Removed<T>> where T : struct
{
    public void Remap(ref Removed<T> value, IReferenceRemapper remapper)
    {
        T item = value.Value;
        previous.Remap(ref item, remapper);
        value = new(item, value.Version);
    }
}
