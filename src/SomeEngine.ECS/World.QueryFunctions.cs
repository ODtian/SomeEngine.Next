using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS;

public partial class World
{
    internal unsafe void ExecuteQuery(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        delegate* managed<QueryCursor, void> execution)
    {
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            execution);
    }

    public unsafe void ExecuteQuery(
        QueryHandle query,
        uint lastSystemVersion,
        delegate* managed<QueryCursor, void> execution)
    {
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        uint currentSystemVersion = AcquireAdmittedQueryVersion(query);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            execution);
    }

    public unsafe void ExecuteQuery(QueryHandle query, delegate* managed<QueryCursor, void> execution)
    {
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

    private unsafe void ExecuteQueryAdmitted(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        bool relationshipWrite,
        delegate* managed<QueryCursor, void> execution)
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

    internal unsafe void ExecuteQuery<TState>(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        ref TState state,
        delegate* managed<QueryCursor, ref TState, void> execution)
        where TState : allows ref struct
    {
        using WorldJobAdmissionScope admission = EnterJobQuery(query, out bool relationshipWrite);
        ExecuteQueryAdmitted(
            query,
            lastSystemVersion,
            currentSystemVersion,
            relationshipWrite,
            ref state,
            execution);
    }

    public unsafe void ExecuteQuery<TState>(
        QueryHandle query,
        uint lastSystemVersion,
        ref TState state,
        delegate* managed<QueryCursor, ref TState, void> execution)
        where TState : allows ref struct
    {
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

    public unsafe void ExecuteQuery<TState>(
        QueryHandle query,
        ref TState state,
        delegate* managed<QueryCursor, ref TState, void> execution)
        where TState : allows ref struct
    {
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

    private unsafe void ExecuteQueryAdmitted<TState>(
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion,
        bool relationshipWrite,
        ref TState state,
        delegate* managed<QueryCursor, ref TState, void> execution)
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

}

