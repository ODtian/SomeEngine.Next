using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Registry;

/// <summary>
/// Whether a value can be borrowed by reference from inside an ECS Job. Alias-free storage holds no managed
/// reference, pointer, native-sized handle or fixed buffer, directly or in a nested value.
/// </summary>
/// <remarks>
/// The answer comes from the type itself: managed references are a runtime fact, and a value that holds native
/// handles declares <see cref="INativeHandleValue"/>. The ECS generator turns an undeclared handle into a build error,
/// so only a type no generator run ever saw can be misjudged as alias-free.
/// </remarks>
internal static class JobStorageTypeMetadata<T>
    where T : struct
{
    internal static readonly bool IsAliasFree = !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
        && !typeof(INativeHandleValue).IsAssignableFrom(typeof(T))
        && (default(T) is not IJobStorageWrapper wrapper || wrapper.IsAliasFree);

    internal static void RequireAliasFree(string storageKind)
    {
        if (!IsAliasFree)
        {
            throw new InvalidOperationException(
                $"{storageKind} Job access to {typeof(T).Name} requires alias-free unmanaged " +
                "by-value storage; managed references, byrefs, pointers, native-sized handles, " +
                "fixed buffers, and recursive external aliases are not supported.");
        }
    }
}

/// <summary>An engine value that embeds another value and is alias-free exactly when that value is.</summary>
internal interface IJobStorageWrapper
{
    bool IsAliasFree { get; }
}
