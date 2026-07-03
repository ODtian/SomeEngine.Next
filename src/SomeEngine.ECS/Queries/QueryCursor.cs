using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Queries;

public readonly struct QueryCursor
{
    private readonly World _world;
    private readonly QueryHandle _handle;

    internal QueryCursor(
        World world,
        QueryHandle handle,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        _world = world;
        _handle = handle;
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    public uint LastSystemVersion { get; }

    public uint CurrentSystemVersion { get; }

    public QueryChunkEnumerator<NoSharedFilter> Chunks =>
        new(_world, _handle, LastSystemVersion, CurrentSystemVersion, default, matchRowFilters: true);

    public QueryRowEnumerator<NoSharedFilter> Rows =>
        new(_world, _handle, LastSystemVersion, CurrentSystemVersion, default);

    public QueryRowEnumerator<SingleSharedFilter> RowsWithShared<T>(in T value)
        where T : struct, ISharedComponent
    {
        return new QueryRowEnumerator<SingleSharedFilter>(
            _world,
            _handle,
            LastSystemVersion,
            CurrentSystemVersion,
            CreateSharedFilter<T>(in value));
    }

    public QueryRowEnumerator<SingleSharedFilter> RowsWithShared(QuerySharedFilter filter) =>
        new(_world, _handle, LastSystemVersion, CurrentSystemVersion, new SingleSharedFilter(filter));

    public QueryRowEnumerator<SpanSharedFilter> RowsWithShared(ReadOnlySpan<QuerySharedFilter> filters) =>
        new(_world, _handle, LastSystemVersion, CurrentSystemVersion, new SpanSharedFilter(filters));

    public QueryChunkEnumerator<SingleSharedFilter> ChunksWithShared<T>(in T value)
        where T : struct, ISharedComponent
    {
        return new QueryChunkEnumerator<SingleSharedFilter>(
            _world,
            _handle,
            LastSystemVersion,
            CurrentSystemVersion,
            CreateSharedFilter<T>(in value),
            matchRowFilters: true);
    }

    public QueryChunkEnumerator<SingleSharedFilter> ChunksWithShared(QuerySharedFilter filter) =>
        new(
            _world,
            _handle,
            LastSystemVersion,
            CurrentSystemVersion,
            new SingleSharedFilter(filter),
            matchRowFilters: true);

    public QueryChunkEnumerator<SpanSharedFilter> ChunksWithShared(ReadOnlySpan<QuerySharedFilter> filters) =>
        new(
            _world,
            _handle,
            LastSystemVersion,
            CurrentSystemVersion,
            new SpanSharedFilter(filters),
            matchRowFilters: true);

    private SingleSharedFilter CreateSharedFilter<T>(in T value)
        where T : struct, ISharedComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        if (_world.Shared.TryIndex(componentId, value, out int sharedIndex))
            return new SingleSharedFilter(new QuerySharedFilter(componentId, sharedIndex));

        return SingleSharedFilter.NoMatch;
    }
}

