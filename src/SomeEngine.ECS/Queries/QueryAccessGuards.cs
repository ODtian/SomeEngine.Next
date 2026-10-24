using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

internal static class QueryAccessGuards
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RequireFieldAccess<T>(QueryArchetypeMatch match, bool write)
        where T : struct, IComponent
    {
        bool declared = match.TryGetAccess(ComponentMetadata<T>.Id, out var access);
        DevelopmentCheck.Require(declared,
            $"{typeof(T).Name} field access requires a declared query data term.");
        DevelopmentCheck.Require(write ? access.Access.CanWrite() : access.Access.CanRead(),
            $"{typeof(T).Name} field access requires the corresponding query permission.");
        return access.ColumnIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RequireAccess<T>(
        QueryArchetypeMatch match,
        bool read,
        bool write)
        where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        if (!match.TryGetAccess(componentId, out var access))
            throw UndeclaredAccess<T>();

        return RequireAccess<T>(in access, read, write);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryRequireReadAccess<T>(QueryArchetypeMatch match, out int column) where T : struct
    {
        int componentId = ComponentMetadata<T>.Id;
        if (match.TryGetAccess(componentId, out var access))
        {
            column = RequireAccess<T>(in access, read: true, write: false);
            return true;
        }
        if (match.Archetype.HasComponent(componentId))
            throw UndeclaredAccess<T>();
        column = 0;
        return false;
    }

    private static InvalidOperationException UndeclaredAccess<T>() => new(
        $"{typeof(T).Name} was not declared as a data access term in this query.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RequireAccess<T>(in QueryColumnAccess access, bool read, bool write) where T : struct
    {

        if (read && !access.Access.CanRead())
            throw new InvalidOperationException($"{typeof(T).Name} was not declared for query read access.");

        if (write && !access.Access.CanWrite())
            throw new InvalidOperationException($"{typeof(T).Name} was not declared for query write access.");

        return access.ColumnIndex;
    }

    internal static void RequireBufferAccess<T>(
        QueryArchetypeMatch match,
        bool read,
        bool write,
        out int headerColumn,
        out int inlineColumn)
        where T : struct, IBufferElement
    {
        int headerId = BufferComponents.Header<T>();
        int inlineId = BufferComponents.Inline<T>();

        headerColumn = RequireBufferColumn<T>(match, headerId, read, write);
        inlineColumn = RequireBufferColumn<T>(match, inlineId, read, write);
    }

    private static int RequireBufferColumn<T>(
        QueryArchetypeMatch match,
        int componentId,
        bool read,
        bool write)
        where T : struct, IBufferElement
    {
        if (!match.TryGetAccess(componentId, out var access))
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} buffer was not declared as a data access term in this query.");
        }

        if (read && !access.Access.CanRead())
            throw new InvalidOperationException($"{typeof(T).Name} buffer was not declared for query read access.");

        if (write && !access.Access.CanWrite())
            throw new InvalidOperationException($"{typeof(T).Name} buffer was not declared for query write access.");

        return access.ColumnIndex;
    }
}

