using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Cluster;

internal readonly record struct ClusterMeshRegistrationResult(
    ClusterMeshRegistration Registration,
    bool Added);

internal sealed partial class ClusterMeshes
{
    private static readonly AsyncLocal<ClusterMeshes?> RegistrationOwner = new();

    private readonly SemaphoreSlim _registrationAdmission = new(1, 1);
    private readonly CancellationTokenSource _registrationShutdown = new();
    private TaskCompletionSource _registrationIdle = CompletedIdleSource();
    private int _registrationOperationCount;
    private bool _registrationsStopping;

    internal bool IsRegistrationOwner
        => ReferenceEquals(RegistrationOwner.Value, this);

    internal int ActiveRegistrationOperations
    {
        get
        {
            lock (_gate)
                return _registrationOperationCount;
        }
    }

    internal async ValueTask<ClusterMeshRegistration> AddMeshAsync(
        AssetHandle<Mesh> handle,
        Mesh mesh,
        CancellationToken cancellationToken = default)
        => (await RegisterMeshAsync(handle, mesh, cancellationToken).ConfigureAwait(false)).Registration;

    internal async ValueTask<ClusterMeshRegistrationResult> RegisterMeshAsync(
        AssetHandle<Mesh> handle,
        Mesh mesh,
        CancellationToken cancellationToken = default)
        => await RegisterMeshCoreAsync(
            handle,
            mesh,
            assetRead: null,
            cancellationToken).ConfigureAwait(false);

    internal async ValueTask<ClusterMeshRegistrationResult> RegisterMeshAsync(
        AssetHandle<Mesh> handle,
        AssetRead<Mesh> assetRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetRead);
        Mesh mesh;
        try
        {
            mesh = assetRead.Value;
        }
        catch
        {
            assetRead.Dispose();
            throw;
        }

        return await RegisterMeshCoreAsync(
            handle,
            mesh,
            assetRead,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ClusterMeshRegistrationResult> RegisterMeshCoreAsync(
        AssetHandle<Mesh> handle,
        Mesh mesh,
        AssetRead<Mesh>? assetRead,
        CancellationToken cancellationToken)
    {
        bool operationEntered = false;
        bool admissionAcquired = false;
        ClusterMeshes? previousOwner = null;
        MeshPayloadSource? source = null;
        bool ownsSource = false;
        ClusterBvhDestination bvhDestination = default;
        bool destinationAllocated = false;
        Exception? cleanupFailure = null;
        try
        {
            ArgumentNullException.ThrowIfNull(mesh);
            if (!handle.IsValid)
                throw new InvalidOperationException("Runtime mesh handle must be valid before cluster registration.");
            if (assetRead is not null && assetRead.Revision != handle.Revision)
                throw new InvalidOperationException("Cluster mesh read revision does not match its handle.");
            if (IsRegistrationOwner)
            {
                throw new InvalidOperationException(
                    "Cluster mesh registration cannot reenter the same residency epoch.");
            }

            EnterRegistrationOperation();
            operationEntered = true;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _registrationShutdown.Token);
            CancellationToken token = linkedCancellation.Token;
            await _registrationAdmission.WaitAsync(token).ConfigureAwait(false);
            admissionAcquired = true;
            previousOwner = RegistrationOwner.Value;
            RegistrationOwner.Value = this;

            PreparedMeshPages pageRegistration;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_registrationsStopping)
                    throw new ObjectDisposedException(nameof(ClusterMeshes));

                bool pagesRegistered = _pages.TryRegistration(
                    handle,
                    out uint existingFirstPage,
                    out uint existingPageCount,
                    out ulong registeredRevision);
                bool rootRegistered = _bvh.TryRegisteredRoot(handle, out uint existingRoot);
                if (pagesRegistered != rootRegistered)
                {
                    throw new InvalidOperationException(
                        "Cluster mesh registration indexes are inconsistent.");
                }
                if (pagesRegistered)
                {
                    RequireCurrentRevision(handle, registeredRevision);
                    return new ClusterMeshRegistrationResult(
                        new ClusterMeshRegistration(
                            handle,
                            existingFirstPage,
                            existingPageCount,
                            existingRoot),
                        Added: false);
                }

                bool hasSource;
                if (assetRead is null)
                {
                    hasSource = mesh.TryRetainPayloadSource(out source);
                    ownsSource = hasSource;
                }
                else
                {
                    hasSource = mesh.TryBorrowPayloadSource(out source);
                }
                if (!hasSource || source is null)
                {
                    throw new InvalidOperationException(
                        "Cluster runtime accepts only range-streamed meshes; materialized payload compatibility is not supported.");
                }
                if (source.Pages.Count == 0 || source.BvhLength <= 0)
                    throw new InvalidDataException("A streamed cluster mesh must contain page metadata and BVH data.");

                pageRegistration = _pages.PrepareStreamedRegistration(
                    handle,
                    source,
                    ownsSource,
                    assetRead);
                bvhDestination = _bvh.AllocateRegistration(source.BvhLength);
                destinationAllocated = true;
            }

            await source.ReadBvhIntoAsync(bvhDestination.Memory, token).ConfigureAwait(false);

            lock (_gate)
            {
                ThrowIfDisposed();
                token.ThrowIfCancellationRequested();
                if (_registrationsStopping)
                    throw new OperationCanceledException("Cluster registration shutdown was requested.", token);

                ClusterBvhRegistration bvh = _bvh.Prepare(
                    handle,
                    bvhDestination,
                    pageRegistration.FirstPageId,
                    source.Pages);

                // This is the only cross-subsystem preflight. Every validation and capacity
                // reservation completes before either canonical registry is mutated.
                _pages.ReserveRegistration(pageRegistration);
                _bvh.ReserveCommit(bvh);

                _pages.CommitRegistration(pageRegistration);
                uint rootNode = _bvh.Commit(bvh);
                destinationAllocated = false;
                source = null;
                assetRead = null;
                AdvanceRevision();
                return new ClusterMeshRegistrationResult(
                    new ClusterMeshRegistration(
                        handle,
                        pageRegistration.FirstPageId,
                        checked((uint)pageRegistration.PageCount),
                        rootNode),
                    Added: true);
            }
        }
        finally
        {
            if (destinationAllocated)
            {
                lock (_gate)
                {
                    try
                    {
                        cleanupFailure = _bvh.CancelRegistration(bvhDestination);
                    }
                    catch (Exception error)
                    {
                        cleanupFailure = error;
                    }
                    finally
                    {
                        AdvanceRevision();
                    }
                }
            }
            if (ownsSource && source is not null)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception error)
                {
                    cleanupFailure ??= error;
                }
            }
            assetRead?.Dispose();
            if (cleanupFailure is not null)
                RecordCleanupFailure(ClusterCleanupStage.Registration, cleanupFailure);

            if (admissionAcquired)
            {
                RegistrationOwner.Value = previousOwner;
                _registrationAdmission.Release();
            }
            if (operationEntered)
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

    internal bool IsMeshRegistered(AssetHandle<Mesh> mesh)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            bool pagesRegistered = _pages.TryRegistration(
                mesh,
                out _,
                out _,
                out ulong registeredRevision);
            bool rootRegistered = _bvh.IsRegistered(mesh);
            if (pagesRegistered != rootRegistered)
                throw new InvalidOperationException("Cluster mesh registration indexes are inconsistent.");
            if (pagesRegistered)
                RequireCurrentRevision(mesh, registeredRevision);
            return pagesRegistered;
        }
    }

    private static void RequireCurrentRevision(
        AssetHandle<Mesh> mesh,
        ulong registeredRevision)
    {
        if (registeredRevision != mesh.Revision)
        {
            throw new InvalidOperationException(
                $"Mesh '{mesh.AssetId}' changed from revision {registeredRevision} to " +
                $"revision {mesh.Revision}. Recreate the Cluster residency epoch before using " +
                "the replacement; stale GPU geometry is never reused.");
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
            if (_directLoads.Count != 0)
                throw new InvalidOperationException("A Cluster epoch cannot be disposed while direct page IO is active.");
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
