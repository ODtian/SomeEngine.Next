using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

internal interface IControlledComponent : IComponent;
internal interface IComputedComponent : IControlledComponent;

internal interface IComponentInitialization<T> where T : struct
{
    void ValidateCreate(World world, in T value);
    void Initialize(World world, Entity entity);
}

internal static class ComponentInitializers
{
    internal static IComponentInitialization<T>? Create<T>() where T : struct
        => typeof(IComponentInitialization<T>).IsAssignableFrom(typeof(T))
            ? (IComponentInitialization<T>)(object)new T() : null;
}

public sealed partial class World
{
    // Computed rows begin absent; the maintaining service supplies their members.
    internal void ClearComputed<T>(Entity entity) where T : struct
    {
        ref EntityRecord record = ref Resolve(entity);
        Storage.Chunk chunk = record.Chunk!;
        int type = Storage.ComponentType<T>.TypeId;
        if (Storage.ComponentType<T>.HasValueOperationsType)
            Storage.ComponentType<T>.ReleaseValue(this, ref Storage.ColumnEntry.At<T>(ref chunk.Columns[type], record.Row));
        Storage.ColumnEntry.At<T>(ref chunk.Columns[type], record.Row) = default;
        EnsurePresence(chunk, type)[record.Row >> 6] &= ~(1UL << (record.Row & 63));
        if (Storage.ComponentType<T>.TracksWritesType)
            chunk.Changes![chunk.Columns[type].ColumnPlusOne - 1].ClearRow(record.Row, Storage.ComponentType<T>.TracksAddedType);
    }
}
