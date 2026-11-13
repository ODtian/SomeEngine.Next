using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster;

internal readonly record struct ClusterMeshRequest(Mesh Mesh, MeshPayloadSource? PageSource);

internal readonly record struct ClusterMeshRegistrationResult(
    ClusterMeshRegistration Registration,
    bool Added);

internal sealed partial class ClusterMeshes
{
    private static readonly AsyncLocal<ClusterMeshes?> RegistrationContext = new();

    private readonly CancellationTokenSource _registrationShutdown = new();
    private TaskCompletionSource _registrationIdle = CompletedIdleSource();
    private int _registrationOperationCount;
    private bool _registrationsStopping;

    internal bool IsRegistrationContext
        => ReferenceEquals(RegistrationContext.Value, this);

    internal int ActiveRegistrationOperations
    {
        get
        {
            lock (_gate)
                return _registrationOperationCount;
        }
    }

    private sealed class RegistrationAllocation(MeshPageRanges pages, ClusterBvhDestination destination)
    {
        internal MeshPageRanges Pages { get; } = pages;
        internal ClusterBvhDestination Destination { get; } = destination;
        internal bool Stored;
    }

    internal async ValueTask<ClusterMeshRegistrationResult[]> RegisterMeshesAsync(
        IReadOnlyList<ClusterMeshRequest> requests, CancellationToken cancellationToken = default)
    {
        EnterRegistrationOperation();
        ClusterMeshes? previousContext = RegistrationContext.Value;
        RegistrationContext.Value = this;
        var allocations = new RegistrationAllocation?[requests.Count];
        var results = new ClusterMeshRegistrationResult[requests.Count];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _registrationShutdown.Token);
        CancellationToken token = stop.Token;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                uint firstPage = _pages.Count;
                int registrationIndex = _pages.RegistrationCount;
                for (int index = 0; index < requests.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    ClusterMeshRequest request = requests[index];
                    if (_pages.TryRegistration(request.Mesh, out uint existingPage, out uint existingCount))
                    {
                        _bvh.TryRegisteredRoot(request.Mesh, out uint root);
                        results[index] = new(new(request.Mesh, existingPage, existingCount, root,
                            request.Mesh.LowestDetailPageIndices.Select(page => existingPage + page).ToArray()), Added: false);
                        continue;
                    }
                    MeshPayloadSource source = request.PageSource?.Retain() ?? MeshPayloadSource.FromMesh(request.Mesh);
                    try
                    {
                        MeshPageRanges pages = _pages.PrepareStreamedRegistration(request.Mesh, source, firstPage, registrationIndex);
                        ClusterBvhDestination destination = _bvh.AllocateRegistration(source.BvhLength);
                        allocations[index] = new(pages, destination);
                        firstPage = checked(firstPage + (uint)pages.PageCount);
                        registrationIndex++;
                    }
                    catch { source.Dispose(); throw; }
                }
            }

            await Task.Run(() => Parallel.For(0, allocations.Length,
                new ParallelOptions { CancellationToken = token }, index =>
                {
                    if (allocations[index] is { } allocation)
                        allocation.Pages.Mesh.ReadBvh(allocation.Destination.Memory.Span);
                }), token).ConfigureAwait(false);

            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                for (int index = 0; index < allocations.Length; index++)
                {
                    if (allocations[index] is not { } allocation) continue;
                    MeshPageRanges pages = allocation.Pages;
                    ClusterBvhRegistration bvh = _bvh.Prepare(pages.Mesh, allocation.Destination,
                        pages.FirstPageId, pages.Source.Pages);
                    _pages.ReserveRegistration(pages);
                    _bvh.ReserveCommit(bvh);
                    ClusterPageFixups.Group[] groups = _fixups.ReserveRegistration(bvh);
                    _pages.CommitRegistration(pages);
                    uint root = _bvh.Commit(bvh);
                    _fixups.Register(groups);
                    allocation.Stored = true;
                    results[index] = new(new(pages.Mesh, pages.FirstPageId, (uint)pages.PageCount,
                        root, pages.LowestDetailPageIds), Added: true);
                    AdvanceRevision();
                }
            }
            return results;
        }
        finally
        {
            foreach (RegistrationAllocation? allocation in allocations)
            {
                if (allocation is null || allocation.Stored) continue;
                Exception? failure;
                lock (_gate) failure = _bvh.CancelRegistration(allocation.Destination);
                allocation.Pages.Source.Dispose();
                if (failure is not null) RecordCleanupFailure(ClusterCleanupStage.Registration, failure);
            }
            RegistrationContext.Value = previousContext;
            ExitRegistrationOperation();
        }
    }

    internal Task StopRegistrations()
    {
        Task idle;
        bool cancel = false;
        lock (_gate)
        {
            if (!_registrationsStopping)
            {
                _registrationsStopping = true;
                cancel = true;
            }
            idle = _registrationIdle.Task;
        }
        if (cancel)
        {
            try
            {
                _registrationShutdown.Cancel();
            }
            catch (Exception error)
            {
                // Cancellation callbacks are external code. Their failure is diagnostic only;
                // it must not strand the admission owner or prevent shutdown from awaiting idle.
                RecordCleanupFailure(ClusterCleanupStage.Registration, error);
            }
        }
        return idle;
    }

    internal bool IsMeshRegistered(Mesh mesh)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            bool pagesRegistered = _pages.TryRegistration(
                mesh,
                out _,
                out _);
            bool rootRegistered = _bvh.IsRegistered(mesh);
            if (pagesRegistered != rootRegistered)
                throw new InvalidOperationException("Cluster mesh registration indexes are inconsistent.");
            return pagesRegistered;
        }
    }

    internal void EnsureReadyForDisposal()
    {
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            if (_activePageStreams != 0)
            {
                throw new InvalidOperationException(
                    "A Cluster epoch cannot be disposed while a page stream still owns its lifecycle.");
            }
            if (_stagingLoads.Count != 0)
                throw new InvalidOperationException("A Cluster epoch cannot be disposed while staged page IO is active.");
            if (_registrationOperationCount != 0)
                throw new InvalidOperationException("A Cluster epoch cannot be disposed while mesh registration IO is active.");
        }
    }

    private void EnterRegistrationOperation()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_registrationsStopping)
                throw new ObjectDisposedException(nameof(ClusterMeshes));
            if (_registrationOperationCount == 0)
            {
                _registrationIdle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _registrationOperationCount = checked(_registrationOperationCount + 1);
        }
    }

    private void ExitRegistrationOperation()
    {
        TaskCompletionSource? completed = null;
        lock (_gate)
        {
            if (_registrationOperationCount <= 0)
                throw new InvalidOperationException("Cluster registration operation count is already zero.");
            _registrationOperationCount--;
            if (_registrationOperationCount == 0)
                completed = _registrationIdle;
        }
        completed?.TrySetResult();
    }

    private static TaskCompletionSource CompletedIdleSource()
    {
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }
}
