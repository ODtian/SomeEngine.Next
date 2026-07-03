namespace SomeEngine.ECS.Systems;

/// <summary>
/// Creates concrete system contexts and receives lifecycle hooks around each enabled update.
/// </summary>
public interface ISystemDriver<TContext>
{
    /// <summary>
    /// Acquires the version baseline for the current system update.
    /// </summary>
    uint AcquireSystemVersion(ref SystemSlot slot);

    /// <summary>
    /// Creates the context passed to system lifecycle methods.
    /// </summary>
    TContext CreateContext(ref SystemSlot slot);

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

    /// <summary>
    /// Runs after version write-back for a successful update, and after destroy context use.
    /// </summary>
    void Complete(ref SystemSlot slot, ref TContext context)
    {
    }
}

