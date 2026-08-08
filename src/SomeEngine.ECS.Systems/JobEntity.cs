using System.ComponentModel;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Marks an entity-oriented job whose <c>Execute</c> signature is implemented by the ECS source
/// generator. The marker itself exposes no storage; generated adapters create scoped row borrows
/// only after the runtime owner has been admitted.
/// </summary>
public interface IJobEntity;

public enum GeneratedQueryStorage : byte
{
    Table,
    Buffer,
    Sparse,
}

public enum GeneratedQueryMode : byte
{
    Read,
    ReadWrite,
}

/// <summary>A storable, non-borrowing description of one generated ECS access.</summary>
public readonly struct GeneratedQueryAccess
{
    private GeneratedQueryAccess(
        Type valueType,
        int componentId,
        GeneratedQueryStorage storage,
        GeneratedQueryMode mode,
        bool relationshipSource,
        bool relationshipTarget,
        bool isAliasFree,
        bool directAccess = true,
        QueryTermFilter filters = QueryTermFilter.None)
    {
        ValueType = valueType;
        ComponentId = componentId;
        Storage = storage;
        Mode = mode;
        IsRelationshipSource = relationshipSource;
        IsRelationshipTarget = relationshipTarget;
        IsAliasFree = isAliasFree;
        HasDirectAccess = directAccess;
        Filters = filters;
    }

    public Type ValueType { get; }

    public int ComponentId { get; }

    public GeneratedQueryStorage Storage { get; }

    public GeneratedQueryMode Mode { get; }

    public bool IsRelationshipSource { get; }

    public bool IsRelationshipTarget { get; }

    internal bool IsAliasFree { get; }

    public QueryTermFilter Filters { get; }

    public bool HasDirectAccess { get; }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static GeneratedQueryAccess Table<T>(GeneratedQueryMode mode)
        where T : struct, IComponent
    {
        bool isAliasFree = JobStorageTypeMetadata<T>.IsAliasFree;
        if (isAliasFree)
            ComponentRegistry.MarkJobAliasFree(ComponentMetadata<T>.Id);

        return new GeneratedQueryAccess(
            typeof(T),
            ComponentMetadata<T>.Id,
            GeneratedQueryStorage.Table,
            mode,
            ComponentMetadata<T>.IsRelationshipSource,
            ComponentMetadata<T>.IsRelationshipTarget,
            isAliasFree);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static GeneratedQueryAccess Buffer<T>(GeneratedQueryMode mode)
        where T : struct, IBufferElement =>
        new(
            typeof(T),
            BufferComponents.Header<T>(),
            GeneratedQueryStorage.Buffer,
            mode,
            relationshipSource: false,
            relationshipTarget: false,
            JobStorageTypeMetadata<T>.IsAliasFree);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static GeneratedQueryAccess Sparse<T>(GeneratedQueryMode mode)
        where T : struct, ISparseComponent =>
        new(
            typeof(T),
            ComponentMetadata<T>.Id,
            GeneratedQueryStorage.Sparse,
            mode,
            relationshipSource: false,
            relationshipTarget: false,
            JobStorageTypeMetadata<T>.IsAliasFree);

    /// <summary>
    /// Source-generator-only factory for a closed table component whose complete field graph was
    /// proven alias-free by Roslyn. Generated consumer code reaches this internal entry point via
    /// a compiler-supported <see cref="UnsafeAccessorAttribute"/> bridge.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static GeneratedQueryAccess GeneratedTable<T>(GeneratedQueryMode mode)
        where T : struct, IComponent
    {
        ComponentRegistry.MarkJobAliasFree(ComponentMetadata<T>.Id);
        return new GeneratedQueryAccess(
            typeof(T),
            ComponentMetadata<T>.Id,
            GeneratedQueryStorage.Table,
            mode,
            ComponentMetadata<T>.IsRelationshipSource,
            ComponentMetadata<T>.IsRelationshipTarget,
            isAliasFree: true);
    }

    /// <summary>Source-generator-emitted factory for a proven closed buffer element.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static GeneratedQueryAccess GeneratedBuffer<T>(GeneratedQueryMode mode)
        where T : struct, IBufferElement
    {
        return new GeneratedQueryAccess(
            typeof(T),
            BufferComponents.Header<T>(),
            GeneratedQueryStorage.Buffer,
            mode,
            relationshipSource: false,
            relationshipTarget: false,
            isAliasFree: true);
    }

    /// <summary>Source-generator-emitted factory for a proven closed sparse component.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static GeneratedQueryAccess GeneratedSparse<T>(GeneratedQueryMode mode)
        where T : struct, ISparseComponent
    {
        return new GeneratedQueryAccess(
            typeof(T),
            ComponentMetadata<T>.Id,
            GeneratedQueryStorage.Sparse,
            mode,
            relationshipSource: false,
            relationshipTarget: false,
            isAliasFree: true);
    }

    internal static GeneratedQueryAccess Filter(
        Type valueType,
        int componentId,
        GeneratedQueryStorage storage,
        QueryTermFilter filters) =>
        new(
            valueType,
            componentId,
            storage,
            GeneratedQueryMode.Read,
            relationshipSource: false,
            relationshipTarget: false,
            isAliasFree: true,
            directAccess: false,
            filters: filters);

    internal GeneratedQueryAccess WithFilters(QueryTermFilter filters) =>
        new(
            ValueType,
            ComponentId,
            Storage,
            Mode,
            IsRelationshipSource,
            IsRelationshipTarget,
            IsAliasFree,
            HasDirectAccess,
            Filters | filters);

    internal GeneratedQueryAccess WithExactFilters(QueryTermFilter filters) =>
        new(
            ValueType,
            ComponentId,
            Storage,
            Mode,
            IsRelationshipSource,
            IsRelationshipTarget,
            IsAliasFree,
            HasDirectAccess,
            filters);
}

/// <summary>
/// Immutable query and access metadata emitted once per closed generated job type. It contains no
/// World, row, chunk, ref, span, or release capability.
/// </summary>
public sealed class GeneratedQueryAccessDescriptor
{
    private readonly GeneratedQueryAccess[] _accesses;
    private readonly ConditionalWeakTable<QueryDefinition, GeneratedQueryAccessDescriptor>
        _composedFilters = new();
    private readonly ConditionalWeakTable<QueryDefinition, GeneratedQueryAccessDescriptor>.CreateValueCallback
        _composeFilter;
    private readonly bool _hasRelationshipWrite;
    private readonly bool _hasWorldWrites;
    private readonly bool _supportsParallel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public GeneratedQueryAccessDescriptor(
        QueryDefinition query,
        params ReadOnlySpan<GeneratedQueryAccess> accesses)
    {
        Query = query ?? throw new ArgumentNullException(nameof(query));
        _composeFilter = ComposeFilter;
        _accesses = NormalizeAndValidate(Query, accesses);

        var identities = new HashSet<(GeneratedQueryStorage Storage, int ComponentId)>();
        bool hasRelationshipWrite = false;
        bool hasWorldWrites = false;
        for (int i = 0; i < _accesses.Length; i++)
        {
            GeneratedQueryAccess access = _accesses[i];
            if (!identities.Add((access.Storage, access.ComponentId)))
            {
                throw new InvalidOperationException(
                    $"Generated query contains duplicate logical access to {access.ValueType.Name}.");
            }
            if (access.IsRelationshipTarget && access.Mode == GeneratedQueryMode.ReadWrite)
            {
                throw new InvalidOperationException(
                    $"Derived relationship component {access.ValueType.Name} is read-only.");
            }
            if (access.HasDirectAccess && !access.IsAliasFree)
            {
                throw new InvalidOperationException(
                    $"Generated IJobEntity direct access to {access.ValueType.Name} is not alias-free unmanaged storage. Declare a certified external resource/lookup adapter instead.");
            }
            hasRelationshipWrite |=
                access.Mode == GeneratedQueryMode.ReadWrite &&
                (access.IsRelationshipSource || access.IsRelationshipTarget);
            hasWorldWrites |=
                access.HasDirectAccess &&
                access.Mode == GeneratedQueryMode.ReadWrite;
        }
        _hasRelationshipWrite = hasRelationshipWrite;
        _hasWorldWrites = hasWorldWrites;
        _supportsParallel = !hasRelationshipWrite &&
                            !Array.Exists(
                                _accesses,
                                static access =>
                                    access.HasDirectAccess &&
                                    access.Mode == GeneratedQueryMode.ReadWrite &&
                                    (access.Filters & QueryTermFilter.ChunkChanged) != 0);
    }

    public QueryDefinition Query { get; }

    public int AccessCount => _accesses.Length;

    public bool HasRelationshipWrite => _hasRelationshipWrite;

    internal bool HasWorldWrites => _hasWorldWrites;

    public bool SupportsParallel
    {
        get => _supportsParallel;
    }

    public GeneratedQueryAccess GetAccess(int index)
    {
        if ((uint)index >= (uint)_accesses.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _accesses[index];
    }

    internal void RequireDirectAccess(
        GeneratedQueryStorage storage,
        int componentId,
        bool write)
    {
        for (int i = 0; i < _accesses.Length; i++)
        {
            GeneratedQueryAccess access = _accesses[i];
            if (!access.HasDirectAccess ||
                access.Storage != storage ||
                access.ComponentId != componentId)
            {
                continue;
            }

            if (write && access.Mode != GeneratedQueryMode.ReadWrite)
            {
                throw new InvalidOperationException(
                    $"Generated query did not declare write access to {access.ValueType.Name}.");
            }
            return;
        }

        throw new InvalidOperationException(
            $"Generated query did not declare direct {storage} access for component ID {componentId}.");
    }

    /// <summary>
    /// Composes filter-only query terms with the generated direct signature. Filters may select
    /// All/None/Any/Optional, Changed/Added/ChunkChanged, or enabled state, but may not smuggle in
    /// additional value access. Random lookup is intentionally not part of this surface.
    /// </summary>
    public GeneratedQueryAccessDescriptor WithFilter(QueryDefinition? filter)
    {
        if (filter is null || filter.Terms.Length == 0)
            return this;
        return _composedFilters.GetValue(filter, _composeFilter);
    }

    private GeneratedQueryAccessDescriptor ComposeFilter(QueryDefinition filter)
    {
        for (int i = 0; i < filter.Accesses.Length; i++)
        {
            if (filter.Accesses[i].Access != QueryAccess.None)
            {
                throw new InvalidOperationException(
                    "Generated IJobEntity filters cannot declare additional value access.");
            }
        }

        QueryDefinition query = QueryDefinition.Combine(Query, filter);
        return new GeneratedQueryAccessDescriptor(query, _accesses);
    }

    private static GeneratedQueryAccess[] NormalizeAndValidate(
        QueryDefinition query,
        ReadOnlySpan<GeneratedQueryAccess> supplied)
    {
        var accesses = new List<GeneratedQueryAccess>(supplied.Length);
        var direct = new Dictionary<(GeneratedQueryStorage Storage, int ComponentId), int>();
        for (int i = 0; i < supplied.Length; i++)
        {
            GeneratedQueryAccess access = supplied[i];
            if (!access.HasDirectAccess)
                continue;
            if (access.ValueType is null)
                throw new InvalidOperationException("Generated query contains an uninitialized access descriptor.");

            var key = (access.Storage, access.ComponentId);
            if (!direct.TryAdd(key, accesses.Count))
            {
                throw new InvalidOperationException(
                    $"Generated query contains duplicate direct access to {access.ValueType.Name}.");
            }
            accesses.Add(access.WithExactFilters(QueryTermFilter.None));
        }

        var logical = new Dictionary<
            (GeneratedQueryStorage Storage, int ComponentId),
            LogicalQueryAccess>();
        for (int i = 0; i < query.Terms.Length; i++)
        {
            QueryTerm term = query.Terms[i];
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(term.ComponentId);
            if (!TryLogicalStorage(
                    term.ComponentId,
                    in info,
                    out GeneratedQueryStorage storage,
                    out int componentId))
            {
                if (term.Access != QueryAccess.None)
                {
                    throw new InvalidOperationException(
                        $"Generated IJobEntity query access to {info.Type.Name} has no supported scoped storage adapter.");
                }
                continue;
            }

            var key = (storage, componentId);
            if (logical.TryGetValue(key, out LogicalQueryAccess existing))
            {
                existing.Access = QueryAccessExtensions.Merge(existing.Access, term.Access);
                existing.Filters |= term.Filters;
                logical[key] = existing;
            }
            else
            {
                logical.Add(
                    key,
                    new LogicalQueryAccess(info.Type, term.Access, term.Filters));
            }
        }

        foreach (var pair in logical)
        {
            LogicalQueryAccess queryAccess = pair.Value;
            bool hasValueAccess = queryAccess.Access != QueryAccess.None;
            if (hasValueAccess)
            {
                if (!direct.TryGetValue(pair.Key, out int accessIndex))
                {
                    throw new InvalidOperationException(
                        $"Generated query declares value access to {queryAccess.ValueType.Name} without a matching scoped direct-access descriptor.");
                }

                GeneratedQueryAccess access = accesses[accessIndex];
                GeneratedQueryMode expected = queryAccess.Access switch
                {
                    QueryAccess.Read => GeneratedQueryMode.Read,
                    QueryAccess.ReadWrite => GeneratedQueryMode.ReadWrite,
                    _ => throw new InvalidOperationException(
                        $"Generated IJobEntity access to {queryAccess.ValueType.Name} must be Read or ReadWrite; write-only row borrows are not supported."),
                };
                if (access.Mode != expected)
                {
                    throw new InvalidOperationException(
                        $"Generated query and scoped descriptor disagree about {(expected == GeneratedQueryMode.ReadWrite ? "write" : "read")} access to {access.ValueType.Name}.");
                }
                accesses[accessIndex] = access.WithExactFilters(queryAccess.Filters);
                continue;
            }

            if (queryAccess.Filters == QueryTermFilter.None)
                continue;
            if (direct.TryGetValue(pair.Key, out int filteredDirect))
            {
                accesses[filteredDirect] = accesses[filteredDirect]
                    .WithExactFilters(queryAccess.Filters);
            }
            else
            {
                direct.Add(pair.Key, accesses.Count);
                accesses.Add(GeneratedQueryAccess.Filter(
                    queryAccess.ValueType,
                    pair.Key.ComponentId,
                    pair.Key.Storage,
                    queryAccess.Filters));
            }
        }

        foreach (var pair in direct)
        {
            GeneratedQueryAccess access = accesses[pair.Value];
            if (!access.HasDirectAccess || access.Storage == GeneratedQueryStorage.Sparse)
                continue;
            if (!logical.TryGetValue(pair.Key, out LogicalQueryAccess queryAccess) ||
                queryAccess.Access == QueryAccess.None)
            {
                throw new InvalidOperationException(
                    $"Generated scoped descriptor declares direct access to {access.ValueType.Name}, but the query does not select and own that value family.");
            }
        }

        return accesses.Count == 0
            ? Array.Empty<GeneratedQueryAccess>()
            : accesses.ToArray();
    }

    private static bool TryLogicalStorage(
        int termComponentId,
        in ComponentInfo info,
        out GeneratedQueryStorage storage,
        out int componentId)
    {
        if (BufferRegistry.TryGetHeaderComponentId(termComponentId, out int headerId))
        {
            storage = GeneratedQueryStorage.Buffer;
            componentId = headerId;
            return true;
        }
        if (info.Storage == StoragePath.Table)
        {
            storage = GeneratedQueryStorage.Table;
            componentId = termComponentId;
            return true;
        }
        storage = default;
        componentId = 0;
        return false;
    }

    private struct LogicalQueryAccess
    {
        internal LogicalQueryAccess(
            Type valueType,
            QueryAccess access,
            QueryTermFilter filters)
        {
            ValueType = valueType;
            Access = access;
            Filters = filters;
        }

        internal Type ValueType;
        internal QueryAccess Access;
        internal QueryTermFilter Filters;
    }

    internal QueryHandle Resolve(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.ResolveGeneratedQuery(Query);
    }
}

/// <summary>
/// Explicit scheduling choices. A zero <see cref="RowsPerPacket"/> uses one stable physical chunk
/// per packet; a positive value mechanically partitions every chunk into fixed contiguous ranges.
/// </summary>
public readonly struct JobEntityScheduleOptions
{
    public JobEntityScheduleOptions(
        int rowsPerPacket = 0,
        uint lastSystemVersion = 0,
        JobScheduleOptions jobOptions = default,
        QueryDefinition? filter = null)
    {
        if (rowsPerPacket < 0)
            throw new ArgumentOutOfRangeException(nameof(rowsPerPacket));
        RowsPerPacket = rowsPerPacket;
        LastSystemVersion = lastSystemVersion;
        JobOptions = jobOptions;
        Filter = filter;
    }

    public static JobEntityScheduleOptions Default => default;

    public int RowsPerPacket { get; }

    public uint LastSystemVersion { get; }

    public JobScheduleOptions JobOptions { get; }

    public QueryDefinition? Filter { get; }
}

/// <summary>Generated-code contract; user jobs never receive an implementation instance.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IGeneratedJobEntityAdapter<TJob>
    where TJob : struct, IJobEntity
{
    void Execute(ref TJob job, ref JobEntityRow row);
}
