using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

/// <summary>Generates component value operations and entity reference handling from the value's members.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class GenerateComponentAttribute : Attribute;

public static class ComponentValues
{
    internal static ComponentType Bind<T>() where T : struct => ComponentType<T>.Value;
    public static T Clone<T>(World world, in T value) where T : struct => ComponentType<T>.CloneValue(world, value);
    public static void Release<T>(World world, ref T value) where T : struct => ComponentType<T>.ReleaseValue(world, ref value);
    public static void Remap<T>(ref T value, in EntityMap entities) where T : struct
        => EntityFields<T>.Operations?.Remap(ref value, entities);
}
