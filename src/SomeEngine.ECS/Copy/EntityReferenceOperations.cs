namespace SomeEngine.ECS.Storage;

public interface IEntityReferenceOperations<T> where T : struct
{
    void Remap(ref T value, in EntityMap entities);
}

internal static class EntityFields<T> where T : struct
{
    internal static readonly IEntityReferenceOperations<T>? Operations = EntityReferenceOperations.Create<T>();
}

/// <summary>Remaps the entity references inside an engine wrapper only when its element holds any.</summary>
internal interface IEntityReferenceWrapper
{
    bool HoldsEntityReferences { get; }
}

internal static class EntityReferenceOperations
{
    // The value type answers for itself: engine wrappers and hand-written types implement the operations, and
    // IEntityReferenceValue<T> types supply them through the interface. Nothing is looked up by reflection.
    internal static IEntityReferenceOperations<T>? Create<T>() where T : struct
    {
        object value = default(T);
        if (value is IEntityReferenceOperations<T> operations)
            return operations is IEntityReferenceWrapper { HoldsEntityReferences: false } ? null : operations;
        return value is IEntityReferenceSource<T> source ? source.ReferenceOperations : null;
    }
}

internal sealed class ValueReferenceOperations<T> : IEntityReferenceOperations<T> where T : struct, IEntityReferenceValue<T>
{
    internal static readonly ValueReferenceOperations<T> Instance = new();
    public void Remap(ref T value, in EntityMap entities) => T.Remap(ref value, entities);
}
