using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

/// <summary>Identifies a component type throughout the current process.</summary>
/// <remarks>Values are assigned when types are first used and are valid within this process.</remarks>
public static class ComponentId<T> where T : struct
{
    public static ushort Value { get; } = checked((ushort)ComponentMetadata<T>.Id);
}
