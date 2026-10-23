using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

/// <summary>
/// Runtime-owned query body. Any ref/span obtained from the cursor is scoped to this callback;
/// normal return (including an inner-loop break) commits, while an exception rolls back
/// relationship topology writes.
/// </summary>
public delegate void QueryExecution(QueryCursor cursor);

/// <summary>
/// Runtime-owned query body with caller-provided state. Passing state by reference lets hot
/// query paths use a static callback without allocating a closure.
/// </summary>
public delegate void QueryExecution<TState>(QueryCursor cursor, ref TState state)
    where TState : allows ref struct;

/// <summary>Executes a query within its callback-scoped component access.</summary>
public interface IQueryExecution
{
    void Execute(QueryCursor query);
}

public readonly ref struct QueryCursor
{
    private readonly World _world;
    private readonly QueryState _plan;
    internal QueryCursor(
        World world,
        QueryState plan,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        _world = world;
        _plan = plan;
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    public uint LastSystemVersion { get; }

    public uint CurrentSystemVersion { get; }

    internal World Owner => _world;
    internal QueryState State => _plan;

    /// <summary>Finds one entity within this query, applying the same filters and access rules as row iteration.</summary>
    public bool TryGetRow(Entity entity, out QueryRow row) =>
        _world.TryGetEntityQueryRow(_plan, entity, LastSystemVersion, CurrentSystemVersion, out row);

    /// <summary>Visits each changed table chunk once, combining known membership changes with read-column versions.</summary>
    public QueryChangedChunkEnumerator ChangedChunks(EntityTableChangeBatch changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!ReferenceEquals(changes.Changes.RequireWorld(), _world))
            throw new ArgumentException("Table changes belong to another World.", nameof(changes));
        return new(_world, _plan, changes.Entities, LastSystemVersion, CurrentSystemVersion);
    }

    public QueryChunkEnumerator<NoSharedFilter> Chunks =>
        new(
            _world,
            _plan,
            LastSystemVersion,
            CurrentSystemVersion,
            default,
            matchRowFilters: true);

    public QueryRowEnumerator<NoSharedFilter> Rows =>
        new(_world, _plan, LastSystemVersion, CurrentSystemVersion, default);

    public QueryRowEnumerator<SingleSharedFilter> RowsWithShared<T>(in T value)
        where T : struct, ISharedComponent
    {
        return new QueryRowEnumerator<SingleSharedFilter>(
            _world,
            _plan,
            LastSystemVersion,
            CurrentSystemVersion,
            CreateSharedFilter<T>(in value));
    }

    internal QueryRowEnumerator<SingleSharedFilter> RowsWithShared(QuerySharedFilter filter)
    {
        filter.RequireWorld(_world);
        return new QueryRowEnumerator<SingleSharedFilter>(
            _world,
            _plan,
            LastSystemVersion,
            CurrentSystemVersion,
            new SingleSharedFilter(filter));
    }

    public QueryChunkEnumerator<SingleSharedFilter> ChunksWithShared<T>(in T value)
        where T : struct, ISharedComponent
    {
        return new QueryChunkEnumerator<SingleSharedFilter>(
            _world,
            _plan,
            LastSystemVersion,
            CurrentSystemVersion,
            CreateSharedFilter<T>(in value),
            matchRowFilters: true);
    }

    internal QueryChunkEnumerator<SingleSharedFilter> ChunksWithShared(QuerySharedFilter filter)
    {
        filter.RequireWorld(_world);
        return new QueryChunkEnumerator<SingleSharedFilter>(
            _world,
            _plan,
            LastSystemVersion,
            CurrentSystemVersion,
            new SingleSharedFilter(filter),
            matchRowFilters: true);
    }

    private SingleSharedFilter CreateSharedFilter<T>(in T value)
        where T : struct, ISharedComponent
    {
        _world.RequireJobSharedRead<T>("Shared-component dynamic filter");
        int componentId = ComponentMetadata<T>.Id;
        if (_world.Shared.TryIndex(componentId, value, out int sharedIndex))
        {
            return new SingleSharedFilter(
                new QuerySharedFilter(_world, componentId, sharedIndex));
        }

        return SingleSharedFilter.NoMatch;
    }
}

