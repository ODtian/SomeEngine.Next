using SomeEngine.Job;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>
    /// Acquires topology-exclusive ownership for a synchronous whole-World read snapshot. This
    /// closes the unbound cross-thread mutation gap while also joining the Job resource frontier
    /// when the optional scheduling integration is installed.
    /// </summary>
    internal WorldJobAdmissionScope EnterReadSnapshotControlPlane()
    {
        if (JobExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                "A whole-World read snapshot cannot execute inside a Job callback. Declare the " +
                "query's typed read resources on the Job instead.");
        }

        return EnterJobAdmission(
            WorldJobAdmissionRequest.ForTopologyControlPlane(),
            allowClosing: false,
            allowReadSnapshotNesting: true);
    }

    [ThreadStatic]
    private static Dictionary<World, int>? s_readSnapshotCallbacks;

    private ReadSnapshotCallbackScope EnterReadSnapshotCallback()
    {
        Dictionary<World, int> callbacks =
            s_readSnapshotCallbacks ??= new Dictionary<World, int>();
        callbacks.TryGetValue(this, out int depth);
        callbacks[this] = checked(depth + 1);
        return new ReadSnapshotCallbackScope(this, Environment.CurrentManagedThreadId);
    }

    private void ThrowIfReadSnapshotMutation(in WorldJobAdmissionRequest request)
    {
        if (!request.CanWrite && !request.RequiresUnboundMutationGate)
        {
            return;
        }

        if (s_readSnapshotCallbacks?.ContainsKey(this) == true)
        {
            throw new InvalidOperationException(
                "The active read-snapshot callback cannot mutate or change control state on its " +
                "source World. Read through the declared query or another read-only World API.");
        }
    }

    private void ExitReadSnapshotCallback(int ownerThreadId)
    {
        if (ownerThreadId != Environment.CurrentManagedThreadId ||
            s_readSnapshotCallbacks is not { } callbacks ||
            !callbacks.TryGetValue(this, out int depth) ||
            depth <= 0)
        {
            throw new InvalidOperationException(
                "World read-snapshot callback scope is unbalanced or disposed on another thread.");
        }

        if (depth == 1)
            callbacks.Remove(this);
        else
            callbacks[this] = depth - 1;
    }

    private readonly ref struct ReadSnapshotCallbackScope
    {
        private readonly World _world;
        private readonly int _ownerThreadId;

        internal ReadSnapshotCallbackScope(World world, int ownerThreadId)
        {
            _world = world;
            _ownerThreadId = ownerThreadId;
        }

        public void Dispose() => _world.ExitReadSnapshotCallback(_ownerThreadId);
    }
}
