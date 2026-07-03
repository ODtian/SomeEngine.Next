using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

public partial class World
{
    public QueryDefinitionBuilder QueryDefinition() => new();

    public QueryHandle Query(QueryDefinitionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Query(builder.Build());
    }

    public QueryHandle Query(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return _queries.Query(definition, _tables.All);
    }

    public QueryCursor RunQuery(QueryHandle query, uint lastSystemVersion, uint currentSystemVersion) =>
        new(this, query, lastSystemVersion, currentSystemVersion);

    public QueryCursor RunQuery(QueryHandle query, uint lastSystemVersion) =>
        RunQuery(query, lastSystemVersion, _clock.Tick);

    public QueryCursor RunQuery(QueryHandle query) =>
        RunQuery(query, AcquireSystemTick(), _clock.Tick);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueryPairEnumerator<TWrite, TRead> RunReadWrite<TWrite, TRead>(QueryHandle query)
        where TWrite : struct, IComponent
        where TRead : struct, IComponent
    {
        uint lastSystemVersion = AcquireSystemTick();
        return new QueryPairEnumerator<TWrite, TRead>(
            this,
            query,
            lastSystemVersion);
    }

    public QueryDefinition GetQueryDefinition(QueryHandle query) => _queries.Definition(query);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueryState GetQueryState(QueryHandle query)
    {
        return _queries.State(query);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadWriteMatches AccessMatches<TWrite, TRead>(
        QueryHandle query,
        int writeComponentId,
        int readComponentId)
        where TWrite : struct, IComponent
        where TRead : struct, IComponent
    {
        return _queries.Access<TWrite, TRead>(query, writeComponentId, readComponentId);
    }

    public QueryBuilder CreateQuery() => new QueryBuilder(this);

    /// <summary>
    /// 获取当前 tick 并递增。用于 system 开始时获取"上次运行"的 tick。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint AcquireSystemTick()
    {
        return _clock.Acquire();
    }

    /// <summary>当前全局 tick（只读）。</summary>
    public uint CurrentTick => _clock.Tick;

    internal QueryView RegisterQuery(QueryDefinition definition)
    {
        var handle = Query(definition);
        return new QueryView(this, handle, definition);
    }

    private void OnArchetype(Archetype archetype)
    {
        _queries.OnArchetype(archetype);
    }
}

