namespace SomeEngine.ECS.Storage;

public interface IEntityReferenceOperations<T> where T : struct
{
    void Remap(ref T value, in EntityMap entities);
}

internal static class EntityFields<T> where T : struct
{
    internal static readonly IEntityReferenceOperations<T>? Operations = EntityReferenceOperations.Create<T>();
}

internal static class EntityReferenceOperations
{
    private static bool Contains(Type type)
        => type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Buffer<>) || type.GetGenericTypeDefinition() == typeof(Removed<>))
            ? Contains(type.GetGenericArguments()[0])
            : type.GetInterfaces().Any(value => value.IsGenericType && value.GetGenericTypeDefinition() == typeof(IEntityReferenceValue<>));

    internal static IEntityReferenceOperations<T>? Create<T>() where T : struct
    {
        Type type = typeof(T);
        if (!Contains(type)) return null;
        if (typeof(IEntityReferenceOperations<T>).IsAssignableFrom(type))
            return (IEntityReferenceOperations<T>)(object)new T();
        Type operations = typeof(IEntityReferenceValue<T>).IsAssignableFrom(type) ? typeof(ValueReferenceOperations<>).MakeGenericType(type)
            : type.GetGenericTypeDefinition() == typeof(Buffer<>) ? typeof(BufferReferenceOperations<>).MakeGenericType(type.GetGenericArguments())
            : typeof(RemovedReferenceOperations<>).MakeGenericType(type.GetGenericArguments());
        return (IEntityReferenceOperations<T>)Activator.CreateInstance(operations)!;
    }
}

internal sealed class ValueReferenceOperations<T> : IEntityReferenceOperations<T> where T : struct, IEntityReferenceValue<T>
{
    public void Remap(ref T value, in EntityMap entities) => T.Remap(ref value, entities);
}

internal sealed class BufferReferenceOperations<T> : IEntityReferenceOperations<Buffer<T>> where T : struct
{
    public void Remap(ref Buffer<T> value, in EntityMap entities)
    {
        for (int i = 0; i < value.Count; i++) EntityFields<T>.Operations!.Remap(ref value.Items![i], entities);
    }
}

internal sealed class RemovedReferenceOperations<T> : IEntityReferenceOperations<Removed<T>> where T : struct
{
    public void Remap(ref Removed<T> value, in EntityMap entities)
    {
        T old = value.Value;
        EntityFields<T>.Operations!.Remap(ref old, entities);
        value = new(old, value.Version);
    }
}
