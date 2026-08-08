using System.Diagnostics;

namespace SomeEngine.ECS;

/// <summary>
/// Runtime-owned structural candidate lifetime. Callers must already hold topology-write
/// admission. Disposing without Commit discards the complete candidate and its hook command
/// overlay; Commit performs the single root+epoch publication after all allocations are prepared.
/// </summary>
internal ref struct StructuralMutationScope
{
    private World? _world;
    private World.StructuralTransactionScope _transaction;
    private World.StructuralCandidateScope _candidate;
    private readonly WorldStructurePublication _publication;
    private bool _transactionActive;
    private bool _candidateActive;
    private bool _overlayActive;
    private readonly long _startedTimestamp;

    private StructuralMutationScope(
        World world,
        World.StructuralTransactionScope transaction,
        World.StructuralCandidateScope candidate,
        WorldStructurePublication publication,
        long startedTimestamp)
    {
        _world = world;
        _transaction = transaction;
        _candidate = candidate;
        _publication = publication;
        _transactionActive = true;
        _candidateActive = true;
        _overlayActive = true;
        _startedTimestamp = startedTimestamp;
    }

    internal long PublicationEpoch => _publication.Epoch;

    internal static StructuralMutationScope Begin(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        long startedTimestamp = Stopwatch.GetTimestamp();
        World.StructuralTransactionScope transaction = world.BeginStructuralTransaction();
        world.StructuralMetrics.Started();
        bool overlayActive = false;
        try
        {
            world.BeginCommandOverlay();
            overlayActive = true;
            long epoch = world.NextStructureEpoch();
            long prepareTimestamp = Stopwatch.GetTimestamp();
            WorldStructureRoot candidate = world.PublishedStructureRoot.CloneDetached(
                world,
                world.HookStore,
                out WorldStructureCloneMetrics cloneMetrics);
            WorldStructurePublication publication = world.PrepareStructurePublication(
                candidate,
                epoch);
            World.StructuralCandidateScope candidateScope =
                world.EnterStructuralCandidate(candidate);
            world.StructuralMetrics.Prepared(
                Stopwatch.GetTimestamp() - prepareTimestamp,
                cloneMetrics);
            return new StructuralMutationScope(
                world,
                transaction,
                candidateScope,
                publication,
                startedTimestamp);
        }
        catch
        {
            if (overlayActive)
                world.EndCommandOverlay(published: false);
            transaction.Dispose();
            world.StructuralMetrics.Aborted(Stopwatch.GetTimestamp() - startedTimestamp);
            throw;
        }
    }

    internal void Commit()
    {
        World world = RequireActive();
        long commitTimestamp = Stopwatch.GetTimestamp();

        // Every operation below the publication write is precondition-only or preallocated.
        // User code, clone work and queue growth have all completed before World publishes.
        world.PrepareCommandOverlayPublication();
        world.PublishStructuralCandidate(_publication);

        _candidate.Dispose();
        _candidateActive = false;
        world.EndCommandOverlay(published: true);
        _overlayActive = false;
        _transaction.Dispose();
        _transactionActive = false;
        world.StructuralMetrics.Published(
            Stopwatch.GetTimestamp() - commitTimestamp,
            Stopwatch.GetTimestamp() - _startedTimestamp);
        _world = null;
    }

    public void Dispose()
    {
        World? world = _world;
        if (world is null)
            return;

        if (_candidateActive)
        {
            _candidate.Dispose();
            _candidateActive = false;
        }
        if (_overlayActive)
        {
            world.EndCommandOverlay(published: false);
            _overlayActive = false;
        }
        if (_transactionActive)
        {
            _transaction.Dispose();
            _transactionActive = false;
        }

        world.StructuralMetrics.Aborted(Stopwatch.GetTimestamp() - _startedTimestamp);
        _world = null;
    }

    private readonly World RequireActive()
    {
        if (_world is null || !_candidateActive || !_overlayActive || !_transactionActive)
            throw new InvalidOperationException("Structural mutation scope is no longer active.");
        return _world;
    }
}
