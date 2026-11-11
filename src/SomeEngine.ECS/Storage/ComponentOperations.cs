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

internal static class ComponentOperations
{
    private static class Registered<T> where T : struct { internal static IComponentOperations<T>? Operations; }
    internal static void Register<T, TOperations>(in TOperations operations)
        where T : struct where TOperations : struct, IComponentOperations<T>
        => Registered<T>.Operations = operations;
    internal static bool IsBuffer(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Buffer<>);
    internal static bool Declares<TMarker>(Type type)
        => typeof(TMarker).IsAssignableFrom(IsBuffer(type) ? type.GetGenericArguments()[0] : type);

    internal static IComponentOperations<T>? Create<T>() where T : struct
    {
        if (Registered<T>.Operations is { } registered) return registered;
        Type type = typeof(T);
        if (typeof(IComponentOperations<T>).IsAssignableFrom(type))
            return (IComponentOperations<T>)(object)new T();
        if (!typeof(IDisposable).IsAssignableFrom(type)) return null;
        return (IComponentOperations<T>)Activator.CreateInstance(typeof(DisposableOperations<>).MakeGenericType(type))!;
    }
}

internal sealed class DisposableOperations<T> : IComponentOperations<T> where T : struct, IDisposable
{
    public T Clone(World world, in T value) => value;
    public void Release(World world, ref T value) { value.Dispose(); value = default; }
}
