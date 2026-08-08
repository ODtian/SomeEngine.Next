using SomeEngine.Job;

namespace SomeEngine.ECS;

public partial class World
{
    [ThreadStatic]
    private static Dictionary<World, int>? t_serializationWriteLifetimes;

    private int _activeSerializationWriteLifetimes;

    /// <summary>
    /// Keeps the retained serialization root and World-owned controls alive for the complete
    /// output. Topology admission is released after the same-image successor handoff; this lifetime
    /// still lets an already admitted encoder finish deterministically if disposal starts.
    /// </summary>
    internal SerializationWriteLifetimeScope EnterSerializationWriteLifetime()
    {
        int threadId = Environment.CurrentManagedThreadId;
        lock (_lifetimeGate)
        {
            if (_lifetimeState != LifetimeOpen)
                throw new ObjectDisposedException(nameof(World));
            _activeSerializationWriteLifetimes = checked(
                _activeSerializationWriteLifetimes + 1);
        }

        try
        {
            Dictionary<World, int> lifetimes =
                t_serializationWriteLifetimes ??= new Dictionary<World, int>();
            lifetimes.TryGetValue(this, out int depth);
            lifetimes[this] = checked(depth + 1);
            return new SerializationWriteLifetimeScope(this, threadId);
        }
        catch
        {
            lock (_lifetimeGate)
            {
                _activeSerializationWriteLifetimes--;
                if (_activeSerializationWriteLifetimes == 0)
                    Monitor.PulseAll(_lifetimeGate);
            }
            throw;
        }
    }

    private bool OwnsCurrentSerializationWriteLifetime()
    {
        Dictionary<World, int>? lifetimes = t_serializationWriteLifetimes;
        return lifetimes is not null &&
            lifetimes.TryGetValue(this, out int depth) &&
            depth > 0;
    }

    private void ThrowIfCurrentThreadHasSerializationWriteLifetime()
    {
        if (OwnsCurrentSerializationWriteLifetime())
        {
            throw new InvalidOperationException(
                "World cannot be disposed from inside an active serialization codec or output callback.");
        }
    }

    private void WaitForSerializationWriteLifetimesToDrain()
    {
        lock (_lifetimeGate)
        {
            while (_activeSerializationWriteLifetimes != 0)
                Monitor.Wait(_lifetimeGate);
        }
    }

    /// <summary>
    /// Acquires topology-exclusive ownership for validation, root pinning, and same-image successor
    /// publication without changing the topology-fact revision. The caller releases this short
    /// scope before external output and keeps only the retained read root plus lifetime.
    /// </summary>
    internal WorldJobAdmissionScope EnterSerializationWriteControlPlane()
    {
        // A Job callback must not hold caller-controlled external I/O while carrying
        // scheduler-scoped resource ownership.
        if (JobExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                "Whole-World serialization cannot execute inside a Job callback. Validate and " +
                "write the external stream after returning to the synchronous owner.");
        }

        return EnterJobAdmission(WorldJobAdmissionRequest.ForTopologyControlPlane());
    }

    internal void ThrowIfSerializationWriteInsideStructuralCandidate()
    {
        bool ownsPreCandidateTransaction =
            Volatile.Read(ref _structuralTransactionActive) != 0 &&
            Monitor.IsEntered(_unboundMutationGate);
        if (ownsPreCandidateTransaction ||
            FindStructuralCandidate(this, t_candidateContext) is not null)
        {
            throw new InvalidOperationException(
                "World serialization cannot start inside an active structural transaction. " +
                "Finish or roll back the transaction before writing external output.");
        }
    }

    private void ExitSerializationWriteLifetime(int ownerThreadId)
    {
        if (ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "Serialization write lifetime must be released by its owning thread.");
        }

        Dictionary<World, int>? lifetimes = t_serializationWriteLifetimes;
        if (lifetimes is null ||
            !lifetimes.TryGetValue(this, out int depth) ||
            depth <= 0)
        {
            throw new InvalidOperationException(
                "Serialization write lifetime ownership was lost before release.");
        }

        if (depth == 1)
            lifetimes.Remove(this);
        else
            lifetimes[this] = depth - 1;

        lock (_lifetimeGate)
        {
            if (_activeSerializationWriteLifetimes <= 0)
            {
                throw new InvalidOperationException(
                    "Serialization write lifetime count underflowed.");
            }
            _activeSerializationWriteLifetimes--;
            if (_activeSerializationWriteLifetimes == 0)
                Monitor.PulseAll(_lifetimeGate);
        }
    }

    internal readonly struct SerializationWriteLifetimeScope : IDisposable
    {
        private readonly World? _world;
        private readonly int _ownerThreadId;

        internal SerializationWriteLifetimeScope(World world, int ownerThreadId)
        {
            _world = world;
            _ownerThreadId = ownerThreadId;
        }

        public void Dispose() =>
            _world?.ExitSerializationWriteLifetime(_ownerThreadId);
    }
}
