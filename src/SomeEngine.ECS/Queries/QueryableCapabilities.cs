using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

[Flags]
public enum QueryableCapabilities : ushort
{
    None = 0,
    Match = 1 << 0,
    DataRead = 1 << 1,
    DataWrite = 1 << 2,
    ChangeFilter = 1 << 3,
    EnableFilter = 1 << 4,
    SharedFilter = 1 << 5,
    BufferBacking = 1 << 6,
}

public readonly struct QueryableTypeInfo
{
    private QueryableTypeInfo(
        Type type,
        int componentId,
        StoragePath storage,
        QueryableCapabilities capabilities)
    {
        Type = type;
        ComponentId = componentId;
        Storage = storage;
        Capabilities = capabilities;
    }

    public Type Type { get; }

    public int ComponentId { get; }

    public StoragePath Storage { get; }

    public QueryableCapabilities Capabilities { get; }

    public static QueryableTypeInfo For<T>() where T : struct
    {
        Type type = typeof(T);
        if (default(T) is IBufferElement)
            throw new InvalidOperationException(
                $"Buffer element type {type.Name} cannot be queried directly. " +
                $"Use QueryDefinition().Buffer<{type.Name}>() / ChangedBuffer<{type.Name}>() " +
                "and QueryRow.Buffer<T>() for element access.");

        int id = ComponentMetadata<T>.Id;
        StoragePath storage = ComponentMetadata<T>.Storage;
        QueryableCapabilities capabilities = storage switch
        {
            StoragePath.Table => TableCapabilities(
                ComponentMetadata<T>.IsEnableable,
                ComponentMetadata<T>.IsRelationshipTarget),
            StoragePath.Tag => QueryableCapabilities.Match,
            StoragePath.Shared => QueryableCapabilities.Match | QueryableCapabilities.SharedFilter,
            StoragePath.Sparse => QueryableCapabilities.None,
            _ => QueryableCapabilities.None,
        };

        return new QueryableTypeInfo(type, id, storage, capabilities);
    }

    internal static QueryableTypeInfo ForComponentId(int componentId)
    {
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
        QueryableCapabilities capabilities = info.Storage switch
        {
            StoragePath.Table => TableCapabilities(
                info.IsEnableable,
                info.IsRelationshipTarget),
            StoragePath.Tag => QueryableCapabilities.Match,
            StoragePath.Shared => QueryableCapabilities.Match | QueryableCapabilities.SharedFilter,
            _ => QueryableCapabilities.None,
        };

        return new QueryableTypeInfo(typeof(object), componentId, info.Storage, capabilities);
    }

    private static QueryableCapabilities TableCapabilities(
        bool isEnableable,
        bool isRelationshipTarget)
    {
        var capabilities = QueryableCapabilities.Match |
                           QueryableCapabilities.DataRead |
                           QueryableCapabilities.ChangeFilter;
        if (!isRelationshipTarget)
            capabilities |= QueryableCapabilities.DataWrite;
        if (isEnableable)
            capabilities |= QueryableCapabilities.EnableFilter;

        return capabilities;
    }

    internal void Validate(QueryTermKind kind, QueryAccess access, QueryTermFilter filters)
    {
        if ((Capabilities & QueryableCapabilities.Match) == 0)
            throw new InvalidOperationException(
                $"Component ID {ComponentId} with storage path {Storage} is not queryable through archetype queries.");

        if (access.CanRead() && (Capabilities & QueryableCapabilities.DataRead) == 0)
            throw new InvalidOperationException(
                $"{Type.Name} does not expose table data for query read access.");

        if (access.CanWrite() && (Capabilities & QueryableCapabilities.DataWrite) == 0)
            throw new InvalidOperationException(
                $"{Type.Name} does not expose table data for query write access.");

        if ((filters & (QueryTermFilter.Added | QueryTermFilter.Changed | QueryTermFilter.ChunkChanged)) != 0 &&
            (Capabilities & QueryableCapabilities.ChangeFilter) == 0)
        {
            throw new InvalidOperationException(
                $"{Type.Name} cannot be used with a change filter because it has no table change version.");
        }

        if ((filters & (QueryTermFilter.Enabled | QueryTermFilter.Disabled)) != 0 &&
            (Capabilities & QueryableCapabilities.EnableFilter) == 0)
        {
            throw new InvalidOperationException(
                $"{Type.Name} cannot be used with Enabled<T> / Disabled<T> because it is not enableable.");
        }

        if (kind == QueryTermKind.None && access != QueryAccess.None)
            throw new InvalidOperationException("Excluded query terms cannot request data access.");
    }
}

