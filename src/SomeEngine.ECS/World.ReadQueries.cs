using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS;

public partial class World
{
    [ThreadStatic]
    private static Dictionary<(World World, int ExecutionDepth), int>? s_queryReadCallbacks;
    /// <summary>Holds the query's topology and declared columns through a read-only callback.</summary>
    public void ExecuteReadQuery(QueryHandle query, QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterDeclaredQueryRead(query);
        using QueryReadCallbackScope callback = EnterQueryReadCallback();
        ExecuteQuery(query, execution);
    }

    public void ExecuteReadQuery(QueryHandle query, uint lastSystemVersion, QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterDeclaredQueryRead(query);
        using QueryReadCallbackScope callback = EnterQueryReadCallback();
        ExecuteQuery(query, lastSystemVersion, execution);
    }

    public void ExecuteReadQuery<TState>(QueryHandle query, ref TState state, QueryExecution<TState> execution)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterDeclaredQueryRead(query);
        using QueryReadCallbackScope callback = EnterQueryReadCallback();
        ExecuteQuery(query, ref state, execution);
    }

    public void ExecuteReadQuery<TState>(QueryHandle query, uint lastSystemVersion, ref TState state,
        QueryExecution<TState> execution) where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterDeclaredQueryRead(query);
        using QueryReadCallbackScope callback = EnterQueryReadCallback();
        ExecuteQuery(query, lastSystemVersion, ref state, execution);
    }

    private WorldJobAdmissionScope EnterDeclaredQueryRead(QueryHandle query)
    {
        RequireReadOnlySnapshotQuery(query);
        return EnterJobAdmission(WorldJobAdmissionRequest.ForReadQuery(_queries.Get(query).Definition.JobStorageAccesses),
            allowClosing: false, allowReadSnapshotNesting: true);
    }

    private QueryReadCallbackScope EnterQueryReadCallback()
    {
        var callbacks = s_queryReadCallbacks ??= new();
        var key = (this, JobExecutionContext.Depth);
        callbacks.TryGetValue(key, out int depth);
        callbacks[key] = depth + 1;
        return new(this, key.Item2);
    }

    private readonly ref struct QueryReadCallbackScope(World world, int executionDepth)
    {
        public void Dispose()
        {
            var key = (world, executionDepth);
            var callbacks = s_queryReadCallbacks!;
            int depth = callbacks[key] - 1;
            if (depth == 0) callbacks.Remove(key);
            else callbacks[key] = depth;
        }
    }
}
