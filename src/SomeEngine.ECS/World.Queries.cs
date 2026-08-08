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
        return AcquireQueryRecord(definition).Handle;
    }

    internal QueryRecord AcquireQueryRecord(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        ThrowIfStructuralTransactionActive();
        return _queries.GetOrCreateRecord(definition, _tables.All);
    }

    internal QueryHandle ResolveGeneratedQuery(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        ThrowIfStructuralTransactionActive();
        return _queries.GetOrCreateGenerated(definition, _tables.All);
    }

    /// <summary>
    /// Releases one acquisition returned by <see cref="Query(QueryDefinition)"/>. Independently
    /// acquired handles for the same interned definition remain valid until their matching release;
    /// the final release invalidates every remaining copy and permits generation-safe slot reuse.
    /// </summary>
    public void ReleaseQuery(QueryHandle query)
    {
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        ThrowIfStructuralTransactionActive();
        _queries.Release(query);
    }

    internal void ExecuteQuery(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            execution);
    }

    public void ExecuteQuery(
        QueryHandle query,
        uint lastSystemVersion,
        QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        uint currentSystemVersion = AcquireAdmittedQueryVersion(query);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            execution);
    }

    public void ExecuteQuery(QueryHandle query, QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        AcquireAdmittedQueryVersions(
            query,
            out uint lastSystemVersion,
            out uint currentSystemVersion);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            execution);
    }

    /// <summary>
    /// Executes one read-only query as a synchronous, mutation-free World snapshot. Unlike an
    /// ordinary unbound query, the snapshot owns the whole topology frontier until the callback
    /// returns, so structural, component, and buffer writers cannot interleave with its rows.
    /// </summary>
    public void ExecuteReadSnapshot(QueryHandle query, QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope snapshotAdmission = EnterReadSnapshotControlPlane();
        RequireReadOnlySnapshotQuery(query);
        using ReadSnapshotCallbackScope callbackScope = EnterReadSnapshotCallback();
        ExecuteQuery(query, execution);
    }

    /// <summary>
    /// Executes a read-only snapshot using the owning system's last successful version. This is
    /// the change-filtered system path; it does not create or advance a per-query checkpoint.
    /// </summary>
    public void ExecuteReadSnapshot(
        QueryHandle query,
        uint lastSystemVersion,
        QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope snapshotAdmission = EnterReadSnapshotControlPlane();
        RequireReadOnlySnapshotQuery(query);
        using ReadSnapshotCallbackScope callbackScope = EnterReadSnapshotCallback();
        ExecuteQuery(query, lastSystemVersion, execution);
    }

    private void ExecuteQueryAdmitted(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        bool relationshipWrite,
        QueryExecution execution)
    {
        BeginQueryIteration(relationshipWrite);
        try
        {
            execution(new QueryCursor(
                this,
                _queries.Get(query).State,
                lastSystemVersion,
                currentSystemVersion));
        }
        catch (Exception bodyFault)
        {
            try
            {
                EndQueryIteration(relationshipWrite, completed: false);
            }
            catch (Exception rollbackFault)
            {
                throw new AggregateException(
                    relationshipWrite
                        ? "Query body and relationship topology rollback both failed."
                        : "Query body and runtime query-lease release both failed.",
                    bodyFault,
                    rollbackFault);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(bodyFault)
                .Throw();
            throw;
        }

        EndQueryIteration(relationshipWrite, completed: true);
    }

    internal void ExecuteQuery<TState>(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        ref TState state,
        QueryExecution<TState> execution)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            ref state,
            execution);
    }

    public void ExecuteQuery<TState>(
        QueryHandle query,
        uint lastSystemVersion,
        ref TState state,
        QueryExecution<TState> execution)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        uint currentSystemVersion = AcquireAdmittedQueryVersion(query);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            ref state,
            execution);
    }

    public void ExecuteQuery<TState>(
        QueryHandle query,
        ref TState state,
        QueryExecution<TState> execution)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        AcquireAdmittedQueryVersions(
            query,
            out uint lastSystemVersion,
            out uint currentSystemVersion);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            ref state,
            execution);
    }

    /// <summary>
    /// Executes one read-only query as a synchronous, mutation-free World snapshot, passing
    /// caller-owned state by reference to avoid a closure allocation.
    /// </summary>
    public void ExecuteReadSnapshot<TState>(
        QueryHandle query,
        ref TState state,
        QueryExecution<TState> execution)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope snapshotAdmission = EnterReadSnapshotControlPlane();
        RequireReadOnlySnapshotQuery(query);
        using ReadSnapshotCallbackScope callbackScope = EnterReadSnapshotCallback();
        ExecuteQuery(query, ref state, execution);
    }

    /// <summary>
    /// Executes a read-only snapshot with caller state and the owning system's last successful
    /// version. Change filters are evaluated against that one system version.
    /// </summary>
    public void ExecuteReadSnapshot<TState>(
        QueryHandle query,
        uint lastSystemVersion,
        ref TState state,
        QueryExecution<TState> execution)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope snapshotAdmission = EnterReadSnapshotControlPlane();
        RequireReadOnlySnapshotQuery(query);
        using ReadSnapshotCallbackScope callbackScope = EnterReadSnapshotCallback();
        ExecuteQuery(query, lastSystemVersion, ref state, execution);
    }

    private void RequireReadOnlySnapshotQuery(QueryHandle query)
    {
        QueryDefinition definition = _queries.Get(query).Definition;
        if (definition.CanWrite || definition.HasRelationshipWrite)
        {
            throw new InvalidOperationException(
                "World read snapshots require a query containing read-only data access terms.");
        }
    }

    private void ExecuteQueryAdmitted<TState>(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        bool relationshipWrite,
        ref TState state,
        QueryExecution<TState> execution)
        where TState : allows ref struct
    {
        BeginQueryIteration(relationshipWrite);
        try
        {
            execution(
                new QueryCursor(
                    this,
                    _queries.Get(query).State,
                    lastSystemVersion,
                    currentSystemVersion),
                ref state);
        }
        catch (Exception bodyFault)
        {
            try
            {
                EndQueryIteration(relationshipWrite, completed: false);
            }
            catch (Exception rollbackFault)
            {
                throw new AggregateException(
                    relationshipWrite
                        ? "Query body and relationship topology rollback both failed."
                        : "Query body and runtime query-lease release both failed.",
                    bodyFault,
                    rollbackFault);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(bodyFault)
                .Throw();
            throw;
        }

        EndQueryIteration(relationshipWrite, completed: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint AcquireAdmittedQueryVersion(QueryHandle query)
    {
        return _queries.Get(query).Definition.CanWrite
            ? unchecked(_clock.Acquire() + 1)
            : _clock.Tick;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AcquireAdmittedQueryVersions(
        QueryHandle query,
        out uint lastSystemVersion,
        out uint currentSystemVersion)
    {
        if (_queries.Get(query).Definition.CanWrite)
        {
            lastSystemVersion = _clock.Acquire();
            currentSystemVersion = unchecked(lastSystemVersion + 1);
            return;
        }

        // A read-only callback cannot publish change metadata. Its implicit window is empty and
        // therefore needs no artificial clock advance.
        lastSystemVersion = currentSystemVersion = _clock.Tick;
    }

    public void ExecuteReadWrite<TWrite, TRead>(
        QueryHandle query,
        QueryPairExecution<TWrite, TRead> execution)
        where TWrite : struct, IComponent
        where TRead : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        uint lastSystemVersion = _clock.Acquire();
        BeginQueryIteration(relationshipWrite);
        try
        {
            execution(new QueryPairEnumerator<TWrite, TRead>(this, query, lastSystemVersion));
        }
        catch (Exception bodyFault)
        {
            try
            {
                EndQueryIteration(relationshipWrite, completed: false);
            }
            catch (Exception rollbackFault)
            {
                throw new AggregateException(
                    relationshipWrite
                        ? "Query body and relationship topology rollback both failed."
                        : "Query body and runtime query-lease release both failed.",
                    bodyFault,
                    rollbackFault);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(bodyFault)
                .Throw();
            throw;
        }

        EndQueryIteration(relationshipWrite, completed: true);
    }

    public void ExecuteReadWrite<TWrite, TRead, TState>(
        QueryHandle query,
        ref TState state,
        QueryPairExecution<TWrite, TRead, TState> execution)
        where TWrite : struct, IComponent
        where TRead : struct, IComponent
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        uint lastSystemVersion = _clock.Acquire();
        BeginQueryIteration(relationshipWrite);
        try
        {
            execution(
                new QueryPairEnumerator<TWrite, TRead>(this, query, lastSystemVersion),
                ref state);
        }
        catch (Exception bodyFault)
        {
            try
            {
                EndQueryIteration(relationshipWrite, completed: false);
            }
            catch (Exception rollbackFault)
            {
                throw new AggregateException(
                    relationshipWrite
                        ? "Query body and relationship topology rollback both failed."
                        : "Query body and runtime query-lease release both failed.",
                    bodyFault,
                    rollbackFault);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(bodyFault)
                .Throw();
            throw;
        }

        EndQueryIteration(relationshipWrite, completed: true);
    }

    public QueryDefinition GetQueryDefinition(QueryHandle query)
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        return _queries.Get(query).Definition;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<ReadWriteMatch> AccessMatches<TWrite, TRead>(
        QueryHandle query,
        int writeComponentId,
        int readComponentId)
        where TWrite : struct, IComponent
        where TRead : struct, IComponent
    {
        return _queries.Get(query).State.AccessMatches<TWrite, TRead>(
            writeComponentId,
            readComponentId);
    }

    /// <summary>
    /// 获取当前 tick 并递增。用于 system 开始时获取"上次运行"的 tick。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint AcquireSystemTick()
    {
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        return _clock.Acquire();
    }

    /// <summary>
    /// Allocates and returns a change version for an admitted system or Job execution.
    /// Unlike <see cref="AcquireSystemTick"/>, which returns the previous baseline while
    /// advancing the clock, this method returns the newly advanced version that writes publish.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint AcquireSystemVersion()
    {
        using WorldJobAdmissionScope admission = EnterRootControlMutation();
        return unchecked(_clock.Acquire() + 1);
    }

    /// <summary>
    /// Allocates a write version for a trusted runtime adapter whose exact World-data owner has
    /// already been admitted. No nested World admission occurs, so a restricted callback can
    /// publish only through its runtime-owned capability without reopening ordinary World APIs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint AcquireAdmittedSystemVersion() => unchecked(_clock.Acquire() + 1);

    /// <summary>当前全局 tick（只读）。</summary>
    public uint CurrentTick
    {
        get
        {
            using WorldJobAdmissionScope admission = EnterJobTopologyRead();
            return _clock.Tick;
        }
    }

}

