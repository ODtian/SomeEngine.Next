namespace SomeEngine.ECS.Systems;

/// <summary>
/// Creates concrete system contexts and receives lifecycle hooks around each enabled update.
/// </summary>
public interface ISystemDriver<TContext>
    where TContext : allows ref struct
{
    World? World => null;

    /// <summary>
    /// Acquires the newly advanced execution version for the current system update. The returned
    /// version must be newer than the pre-update baseline stored in <see cref="SystemState"/>.
    /// </summary>
    ulong AcquireSystemVersion(ref SystemState state);

    /// <summary>
    /// Creates the context passed to system lifecycle methods.
    /// </summary>
    TContext CreateContext(scoped ref SystemState state);

    /// <summary>
    /// Refreshes an externally owned context template for one system execution. Drivers whose
    /// contexts contain scoped capabilities can preserve those capabilities while replacing the
    /// per-system execution version. Ordinary drivers use a newly created context.
    /// </summary>
    void CreateContext(scoped ref SystemState state, ref TContext context)
    {
        context = CreateContext(ref state);
    }

    /// <summary>
    /// Runs after context creation and before system lifecycle calls.
    /// </summary>
    void BeforeUpdate(ref SystemState state, ref TContext context)
    {
    }

    /// <summary>
    /// Runs after <see cref="ISystem{TContext}.OnUpdate"/> and before version write-back.
    /// </summary>
    void AfterUpdate(ref SystemState state, ref TContext context)
    {
    }

}
