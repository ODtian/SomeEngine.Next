using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

internal interface IRemovedValues
{
    ComponentType Type { get; }
    void Capture(World world, Entity entity, ref EntityRecord record, ulong version);
}

/// <summary>A value type that supplies how its removed values are kept.</summary>
public interface IRemovedValueSource
{
    internal IRemovedValues? CreateRemovedValues() => null;
}

/// <summary>Keeps the last value of a removed component as <see cref="Removed{T}"/> until the next update.</summary>
/// <remarks>The value type declares itself so the World creates its capture without reflection or registration.</remarks>
public interface IRemovedComponent<TSelf> : IRemovedComponent, IRemovedValueSource
    where TSelf : struct, IRemovedComponent<TSelf>
{
    IRemovedValues? IRemovedValueSource.CreateRemovedValues() => new RemovedValueOperations<TSelf>();
}

internal interface IRemovedValue<T> : IRemovedValues where T : struct
{
    void Capture(World world, Entity entity, in T value, ulong version);
}

internal sealed class RemovedValueOperations<T> : IRemovedValue<T> where T : struct
{
    public ComponentType Type => ComponentType<Removed<T>>.Value;
    public void Capture(World world, Entity entity, ref EntityRecord record, ulong version)
    {
        T value = ComponentType<T>.IsSharedType
            ? world.ReadSharedColumn<T>(record.Chunk!.Columns[ComponentType<T>.TypeId], record.Row)
            : ComponentType<T>.IsTagType ? default
            : ColumnEntry.At<T>(ref record.Chunk!.Columns[ComponentType<T>.TypeId], record.Row);
        world.PreserveRemoved(entity, ref record, value, version);
    }
    public void Capture(World world, Entity entity, in T value, ulong version)
    {
        ref EntityRecord record = ref world.Resolve(entity);
        world.PreserveRemoved(entity, ref record, value, version);
    }
}

public sealed partial class World
{
    internal void PreserveRemoved<T>(Entity entity, ref EntityRecord record, in T value, ulong version) where T : struct
        => AddKnown(entity, ref record, new Removed<T>(value, version));
}
