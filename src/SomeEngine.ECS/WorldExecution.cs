namespace SomeEngine.ECS;

/// <summary>
/// The execution mode selected when a World is created. A World without one is synchronous;
/// scheduling integrations provide an implementation without making ECS depend on a scheduler.
/// Execution mode cannot change while that World is alive.
/// </summary>
public abstract class WorldExecution
{
    internal WorldExecution() { }

    internal abstract void Attach(World world);
    internal abstract bool HasCurrentJobScope { get; }
    internal abstract bool IsExecutingCurrentScope { get; }
    internal abstract bool HasCurrentThreadScope(World world);
    internal abstract void Enter(World world, in WorldJobAdmissionRequest request);
    internal abstract void Exit(World world, in WorldJobAdmissionRequest request);
    internal abstract void ValidateCommandBufferAccess(World world);
    internal abstract void Close();
    internal abstract void Release();
}
