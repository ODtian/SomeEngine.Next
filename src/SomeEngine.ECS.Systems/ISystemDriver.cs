namespace SomeEngine.ECS.Systems;

/// <summary>
/// Creates concrete system contexts and receives lifecycle hooks around each enabled update.
/// </summary>
public interface ISystemDriver<TContext>
    where TContext : allows ref struct
{
    /// <summary>
    /// Acquires the newly advanced execution version for the current system update. The returned
    /// version must be newer than the pre-update baseline stored in <see cref="SystemSlot"/>.
    /// </summary>
    uint AcquireSystemVersion(ref SystemSlot slot);

    /// <summary>
    /// Creates the context passed to system lifecycle methods.
    /// </summary>
    TContext CreateContext(scoped ref SystemSlot slot);

    /// <summary>
    /// Refreshes an externally owned context template for one system invocation. Drivers whose
    /// contexts contain scoped capabilities can preserve those capabilities while replacing the
    /// per-system execution version. Ordinary drivers use a newly created context.
    /// </summary>
    void CreateContext(scoped ref SystemSlot slot, ref TContext context)
    {
        context = CreateContext(ref slot);
    }

    /// <summary>
    /// Runs after context creation and before system lifecycle calls.
    /// </summary>
    void BeforeUpdate(ref SystemSlot slot, ref TContext context)
    {
    }

    /// <summary>
    /// Runs after <see cref="ISystem{TContext}.OnUpdate"/> and before version write-back.
    /// </summary>
    void AfterUpdate(ref SystemSlot slot, ref TContext context)
    {
    }

}

