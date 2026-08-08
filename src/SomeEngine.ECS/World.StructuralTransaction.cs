namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>
    /// Executes a trusted value-only mutation while holding one topology frontier. This does not
    /// create or publish a detached root; callers must validate every structural precondition
    /// before their first write and must not invoke user code from the mutation.
    /// </summary>
    protected TResult ExecuteValueMutation<TState, TResult>(
        TState state,
        Func<TState, TResult> execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobTopologyRead();
        return execution(state);
    }

    /// <summary>
    /// Lets a trusted World subtype apply a coherent mutation to a detached root. Successful
    /// execution publishes that root once; an exception discards component, buffer, entity,
    /// index, clock, and hook-overlay changes together.
    /// </summary>
    protected void ExecuteStructuralTransaction<TState>(
        TState state,
        Action<TState> execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        using StructuralMutationScope mutation = BeginStructuralMutation();
        execution(state);
        mutation.Commit();
    }
}
