using System.Reflection;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Registry;

/// <summary>
/// Generated or lazily derived type data for values borrowed directly by an ECS Job.
/// Alias-free storage cannot contain a managed reference, byref, pointer, native-sized handle,
/// fixed buffer, or a recursively nested value with one of those shapes.
/// </summary>
internal static class JobStorageTypeMetadata<T>
    where T : struct
{
    private static int s_aliasFree;

    internal static bool IsAliasFree
    {
        get
        {
            int value = Volatile.Read(ref s_aliasFree);
            if (value == 0)
            {
                value = !RuntimeHelpers.IsReferenceOrContainsReferences<T>() &&
                    JobStorageTypeShape.IsAliasFree(typeof(T)) ? 1 : -1;
                Interlocked.CompareExchange(ref s_aliasFree, value, 0);
            }
            return value > 0;
        }
    }

    internal static bool GetIsAliasFree() => IsAliasFree;

    internal static void Register(bool isAliasFree) =>
        Volatile.Write(ref s_aliasFree, isAliasFree ? 1 : -1);

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

internal static class JobStorageTypeShape
{
    internal static bool IsAliasFree(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return IsAliasFree(type, new HashSet<Type>());
    }

    private static bool IsAliasFree(Type type, HashSet<Type> visited)
    {
        if (type.IsPointer ||
            type.IsByRef ||
            type.IsFunctionPointer ||
            type.IsByRefLike ||
            type == typeof(IntPtr) ||
            type == typeof(UIntPtr))
        {
            return false;
        }

        if (!type.IsValueType)
            return false;
        if (type.IsPrimitive || type.IsEnum || !visited.Add(type))
            return true;

        const BindingFlags fields =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;
        foreach (FieldInfo field in type.GetFields(fields))
        {
            if (field.IsDefined(typeof(FixedBufferAttribute), inherit: false) ||
                !IsAliasFree(field.FieldType, visited))
            {
                return false;
            }
        }
        return true;
    }
}
