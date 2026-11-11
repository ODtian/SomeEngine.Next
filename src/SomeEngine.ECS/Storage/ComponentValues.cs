using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

/// <summary>Generates component value operations and entity reference handling from the value's members.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class GenerateComponentAttribute : Attribute;

public static class ComponentValues
{
    public static void RegisterRemoved<T>() where T : struct => Bind<T>();
    internal static ComponentType Bind<T>() where T : struct
    {
        ComponentType type = ComponentType<T>.Value;
        if (type.KeepsRemoved && type.RemovedValues is null)
            lock (type) type.RemovedValues ??= new RemovedValueOperations<T>();
        return type;
    }
    /// <summary>Registers the generated value operations before the component's first storage binding.</summary>
    public static void Register<T, TOperations>(in TOperations operations)
        where T : struct where TOperations : struct, IComponentOperations<T>
        => ComponentOperations.Register<T, TOperations>(operations);
    public static T Clone<T>(World world, in T value) where T : struct => ComponentType<T>.CloneValue(world, value);
    public static void Release<T>(World world, ref T value) where T : struct => ComponentType<T>.ReleaseValue(world, ref value);
    public static void Remap<T>(ref T value, in EntityMap entities) where T : struct
        => EntityFields<T>.Operations?.Remap(ref value, entities);
}
