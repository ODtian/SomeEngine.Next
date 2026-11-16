namespace SomeEngine.ECS.Storage;

public interface IComponentOperations<T> where T : struct
{
    T Clone(World world, in T value);
    void Release(World world, ref T value);
}

internal interface IComponentColumnOperations<T> where T : struct
{
    void Release(World world, Span<T> values);
}

/// <summary>A component the World disposes when it releases the value.</summary>
public interface IDisposableComponent<T> : IComponentOperations<T>, IDisposable where T : struct, IDisposableComponent<T>
{
    T IComponentOperations<T>.Clone(World world, in T value) => value;
    void IComponentOperations<T>.Release(World world, ref T value)
    {
        value.Dispose();
        value = default;
    }
}

internal static class ComponentOperations
{
    internal static bool IsBuffer(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Buffer<>);
    internal static bool Declares<TMarker>(Type type)
        => typeof(TMarker).IsAssignableFrom(IsBuffer(type) ? type.GetGenericArguments()[0] : type);

    // The value type answers for itself: generated, disposable and engine values implement the operations.
    internal static IComponentOperations<T>? Create<T>() where T : struct
        => default(T) is IComponentOperations<T> operations ? operations : null;
}
