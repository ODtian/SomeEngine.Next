using System.Runtime.ExceptionServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Job;
using SomeEngine.Render.Components;

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

    private struct MeshRegistrationData(Mesh mesh, MeshPayloadSource? source)
    {
        internal readonly Mesh Mesh = mesh;
        internal MeshPayloadSource? Source = source;
        internal ClusterBvhDestination Destination;
        internal uint FirstPage;
        internal int RegistrationIndex;
        internal PreparedMeshPages? Pages;
        internal ClusterBvhRegistration Bvh;
    }

    internal async ValueTask<ClusterMeshRegistrationResult[]> RegisterMeshesAsync(
        RenderMesh[] meshes,
        CancellationToken cancellationToken = default)
    {
        Scheduler scheduler = JobSystem.Default;
        var data = new MeshRegistrationData[meshes.Length];
        var results = new ClusterMeshRegistrationResult[meshes.Length];
        JobResourceAccess access = JobResourceAccess.Write(scheduler, data);
        bool operationEntered = false;
        bool admissionAcquired = false;
        ClusterMeshes? previousOwner = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            for (int index = 0; index < meshes.Length; index++)
                data[index] = new MeshRegistrationData(meshes[index].Mesh, meshes[index].PageSource?.Retain());
            if (IsRegistrationOwner)
                throw new InvalidOperationException("Cluster mesh registration cannot reenter the same residency epoch.");

            EnterRegistrationOperation();
            operationEntered = true;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _registrationShutdown.Token);
            CancellationToken token = linkedCancellation.Token;
            await _registrationAdmission.WaitAsync(token).ConfigureAwait(false);
            admissionAcquired = true;
            previousOwner = RegistrationOwner.Value;
            RegistrationOwner.Value = this;

            lock (_gate)
            {
                ThrowIfDisposed();
                if (_registrationsStopping)
                    throw new ObjectDisposedException(nameof(ClusterMeshes));
                uint firstPage = _pages.Count;
                int registrationIndex = _pages.RegistrationCount;
                for (int index = 0; index < data.Length; index++)
                {
                    ref MeshRegistrationData item = ref data[index];
                    ArgumentNullException.ThrowIfNull(item.Mesh);
                    bool pagesRegistered = _pages.TryRegistration(item.Mesh, out uint existingFirstPage, out uint existingPageCount);
                    bool rootRegistered = _bvh.TryRegisteredRoot(item.Mesh, out uint existingRoot);
                    if (pagesRegistered != rootRegistered)
                        throw new InvalidOperationException("Cluster mesh registration indexes are inconsistent.");
                    if (pagesRegistered)
                    {
                        uint[] lowestDetailPageIds = item.Source?.LowestDetailPageIndices.Span.ToArray()
                            .Select(localPage => checked(existingFirstPage + localPage)).ToArray() ?? [];
                        results[index] = new ClusterMeshRegistrationResult(
                            new ClusterMeshRegistration(item.Mesh, existingFirstPage, existingPageCount,
                                existingRoot, lowestDetailPageIds), Added: false);
                        continue;
                    }
                    int pageCount = item.Source?.Pages.Count ?? item.Mesh.PageCount;
                    int bvhLength = item.Source?.BvhLength ?? item.Mesh.BvhByteLength;
                    if (pageCount == 0 || bvhLength <= 0)
                        throw new InvalidDataException("A streamed cluster mesh must contain page metadata and BVH data.");
                    item.FirstPage = firstPage;
                    item.RegistrationIndex = registrationIndex++;
                    item.Destination = _bvh.AllocateRegistration(bvhLength);
                    firstPage = checked(firstPage + (uint)pageCount);
                }
            }

            scheduler.ExecuteParallel(new PrepareMeshes(this, data, token), data.Length, 1, [access]);

            lock (_gate)
            {
                ThrowIfDisposed();
                for (int index = 0; index < data.Length; index++)
                {
                    ref MeshRegistrationData item = ref data[index];
                    if (item.Destination.Memory.IsEmpty) continue;
                    token.ThrowIfCancellationRequested();
                    PreparedMeshPages pages = item.Pages!;
                    _pages.ReserveRegistration(pages);
                    _bvh.ReserveCommit(item.Bvh);
                    _pages.CommitRegistration(pages);
                    uint root = _bvh.Commit(item.Bvh);
                    item.Destination = default;
                    item.Source = null;
                    AdvanceRevision();
                    results[index] = new ClusterMeshRegistrationResult(
                        new ClusterMeshRegistration(item.Mesh, pages.FirstPageId, checked((uint)pages.PageCount),
                            root, pages.LowestDetailPageIds), Added: true);
                }
            }
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            try
            {
                JobHandle cleanup;
                try
                {
                    cleanup = scheduler.ScheduleFinally(new ReleaseMeshInputs(this, data, failure),
                        [access], JobScheduleOptions.Default, default);
                }
                catch
                {
                    ReleaseRegistrationData(data);
                    failure?.Throw();
                    throw;
                }
                cleanup.Complete();
            }
            finally
            {
                if (admissionAcquired)
                {
                    RegistrationOwner.Value = previousOwner;
                    _registrationAdmission.Release();
                }
                if (operationEntered) ExitRegistrationOperation();
            }
        }
        return results;
    }

    private readonly record struct PrepareMeshes(
        ClusterMeshes Meshes, MeshRegistrationData[] Data, CancellationToken Cancellation) : IJobParallelFor
    {
        public void Execute(int index)
        {
            ref MeshRegistrationData item = ref Data[index];
            if (item.Destination.Memory.IsEmpty) return;
            ClusterMeshes? previous = RegistrationOwner.Value;
            RegistrationOwner.Value = Meshes;
            try
            {
                Cancellation.ThrowIfCancellationRequested();
                MeshPayloadSource source = item.Source ??= MeshPayloadSource.FromMesh(item.Mesh);
                if (!source.Matches(item.Mesh))
                    throw new InvalidDataException("The explicit page source differs from the mesh content.");
                item.Pages = MeshPages.PrepareStreamedRegistration(item.Mesh, source, item.FirstPage, item.RegistrationIndex);
                item.Mesh.ReadBvh(item.Destination.Memory.Span);
                item.Bvh = ClusterBvh.Prepare(item.Mesh, item.Destination, item.FirstPage, source.Pages);
                Cancellation.ThrowIfCancellationRequested();
            }
            finally { RegistrationOwner.Value = previous; }
        }
    }

    private readonly record struct ReleaseMeshInputs(
        ClusterMeshes Meshes, MeshRegistrationData[] Data, ExceptionDispatchInfo? Failure) : IJob
    {
        public void Execute()
        {
            ClusterMeshes? previous = RegistrationOwner.Value;
            RegistrationOwner.Value = Meshes;
            try
            {
                Meshes.ReleaseRegistrationData(Data);
                Failure?.Throw();
            }
            finally { RegistrationOwner.Value = previous; }
        }
    }

    private void ReleaseRegistrationData(MeshRegistrationData[] data)
    {
        Exception? cleanupFailure = null;
        lock (_gate)
        {
            foreach (ref MeshRegistrationData item in data.AsSpan())
            {
                if (item.Destination.Memory.IsEmpty) continue;
                try
                {
                    Exception? error = _bvh.CancelRegistration(item.Destination);
                    cleanupFailure ??= error;
                }
                catch (Exception error) { cleanupFailure ??= error; }
                finally { item.Destination = default; AdvanceRevision(); }
            }
        }
        foreach (ref MeshRegistrationData item in data.AsSpan())
        {
            try { item.Source?.Dispose(); }
            catch (Exception error) { cleanupFailure ??= error; }
            finally { item = default; }
        }
        if (cleanupFailure is not null)
            RecordCleanupFailure(ClusterCleanupStage.Registration, cleanupFailure);
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
