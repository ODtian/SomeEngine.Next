using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Queries;

/// <summary>
/// Runtime-owned query body. Any ref/span obtained from the cursor is scoped to this callback;
/// normal return (including an inner-loop break) commits, while an exception rolls back
/// relationship topology writes.
/// </summary>
public delegate void QueryExecution(scoped QueryCursor cursor);

/// <summary>
/// Runtime-owned query body with caller-provided state. Passing state by reference lets hot
/// query paths use a static callback without allocating a closure.
/// </summary>
public delegate void QueryExecution<TState>(scoped QueryCursor cursor, ref TState state)
    where TState : allows ref struct;

/// <summary>Executes a query within its callback-scoped component access.</summary>
public interface IQueryExecution
{
    void Execute(scoped QueryCursor query);
}

internal readonly struct QueryData
{
    internal QueryData(
        World world,
        QueryState state,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        World = world;
        State = state;
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    internal World World { get; }
    internal QueryState State { get; }
    internal uint LastSystemVersion { get; }
    internal uint CurrentSystemVersion { get; }
}

public readonly ref struct QueryCursor
{
    private readonly ref readonly QueryData _data;

    internal QueryCursor(in QueryData data) => _data = ref data;

    public uint LastSystemVersion => _data.LastSystemVersion;

    public uint CurrentSystemVersion => _data.CurrentSystemVersion;

    /// <summary>Reports whether every member requires this component through All or the sole Any term.</summary>
    public bool Requires<T>() where T : struct => _data.State.Definition.Requires<T>();

    internal World Owner => _data.World;
    internal QueryState State => _data.State;
    internal QueryData Data => _data;

    /// <summary>Finds one entity within this query, applying the same filters and access rules as row iteration.</summary>
    public bool TryGetRow(Entity entity, out QueryRow row) =>
        _data.World.TryGetEntityQueryRow(_data.State, entity, LastSystemVersion, CurrentSystemVersion, out row);

    /// <summary>Finds a member and borrows one present readable component within this query's filters.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryReadRef<T>(Entity entity, out ComponentReference<T> component) where T : struct
    {
        component = World.ReadEntityQueryRef<T>(in _data, entity);
        return component.HasValue;
    }

    /// <summary>Checks membership using this query's component, row and version filters.</summary>
    public bool Contains(Entity entity) => _data.World.ContainsEntityQuery(_data.State, entity, LastSystemVersion);

    /// <summary>Visits each changed table chunk once, combining known membership changes with read-column versions.</summary>
    public QueryChangedChunkEnumerator ChangedChunks(EntityTableChangeBatch changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!ReferenceEquals(changes.Changes.RequireWorld(), _data.World))
            throw new ArgumentException("Table changes belong to another World.", nameof(changes));
        return new(_data.World, _data.State, changes.Entities, LastSystemVersion, CurrentSystemVersion);
    }

    public QueryChunkEnumerator<NoSharedFilter> Chunks =>
        new(
            _data.World,
            _data.State,
            LastSystemVersion,
            CurrentSystemVersion,
            default,
            matchRowFilters: true);

    public QueryRowEnumerator<NoSharedFilter> Rows =>
        new(_data.World, _data.State, LastSystemVersion, CurrentSystemVersion, default);

    /// <summary>Visits the same member rows while binding optional component columns at chunk boundaries.</summary>
    public ComponentRowEnumerator<TFirst, TSecond> ReadRows<TFirst, TSecond>()
        where TFirst : struct
        where TSecond : struct => new(Rows);

    /// <summary>Visits the same member rows with writable component and buffer columns bound per chunk.</summary>
    public ComponentBufferRowEnumerator<TComponent, TBuffer> ReadWriteRows<TComponent, TBuffer>()
        where TComponent : struct
        where TBuffer : struct, IBufferElement => new(_data.World, Rows);

    public QueryRowEnumerator<SingleSharedFilter> RowsWithShared<T>(in T value)
        where T : struct, ISharedComponent
    {
        return new QueryRowEnumerator<SingleSharedFilter>(
            _data.World,
            _data.State,
            LastSystemVersion,
            CurrentSystemVersion,
            CreateSharedFilter<T>(in value));
    }

    internal QueryRowEnumerator<SingleSharedFilter> RowsWithShared(QuerySharedFilter filter)
    {
        filter.RequireWorld(_data.World);
        return new QueryRowEnumerator<SingleSharedFilter>(
            _data.World,
            _data.State,
            LastSystemVersion,
            CurrentSystemVersion,
            new SingleSharedFilter(filter));
    }

    public QueryChunkEnumerator<SingleSharedFilter> ChunksWithShared<T>(in T value)
        where T : struct, ISharedComponent
    {
        return new QueryChunkEnumerator<SingleSharedFilter>(
            _data.World,
            _data.State,
            LastSystemVersion,
            CurrentSystemVersion,
            CreateSharedFilter<T>(in value),
            matchRowFilters: true);
    }

    internal QueryChunkEnumerator<SingleSharedFilter> ChunksWithShared(QuerySharedFilter filter)
    {
        filter.RequireWorld(_data.World);
        return new QueryChunkEnumerator<SingleSharedFilter>(
            _data.World,
            _data.State,
            LastSystemVersion,
            CurrentSystemVersion,
            new SingleSharedFilter(filter),
            matchRowFilters: true);
    }

    private SingleSharedFilter CreateSharedFilter<T>(in T value)
        where T : struct, ISharedComponent
    {
        _data.World.RequireJobSharedRead<T>("Shared-component dynamic filter");
        int componentId = ComponentMetadata<T>.Id;
        if (_data.World.Shared.TryIndex(componentId, value, out int sharedIndex))
        {
            return new SingleSharedFilter(
                new QuerySharedFilter(_data.World, componentId, sharedIndex));
        }

        return SingleSharedFilter.NoMatch;
    }
}

