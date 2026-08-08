using SomeEngine.Job;

namespace SomeEngine.ECS;

public partial class World
{
    private const int LifetimeOpen = 0;
    private const int LifetimeClosing = 1;
    private const int LifetimeClosed = 2;

    private readonly object _lifetimeGate = new();
    private readonly JobLifetime _jobLifetime = new(nameof(World));
    private int _lifetimeState;
    private int _lifetimeOwnerThreadId;

    internal IJobSubmissionObserver JobSubmissionObserver => _jobLifetime;

    internal int TrackedJobRootCount => _jobLifetime.TrackedRootCount;

    internal JobSubmissionScope EnterJobSubmissionScope()
    {
        ThrowIfUnavailable();
        return _jobLifetime.EnterSubmissionScope();
    }

    internal void ThrowIfUnavailable()
    {
        int state = Volatile.Read(ref _lifetimeState);
        if (state == LifetimeOpen)
            return;

        // A root admitted before closure may continue using its World while Dispose waits its
        // full scheduler scope. Unrelated roots cannot reach execution because submission
        // ownership rejects them before a scheduler state is published.
        if (state == LifetimeClosing &&
            (_jobLifetime.OwnsCurrentScope() ||
             OwnsCurrentSerializationWriteLifetime() ||
             Volatile.Read(ref _lifetimeOwnerThreadId) == Environment.CurrentManagedThreadId))
            return;

        throw new ObjectDisposedException(nameof(World));
    }

    public void Dispose()
    {
        ValidateDisposePreconditions();

        int threadId = Environment.CurrentManagedThreadId;
        lock (_lifetimeGate)
        {
            if (_lifetimeState == LifetimeClosed)
                return;
            if (_lifetimeState == LifetimeClosing)
            {
                if (_lifetimeOwnerThreadId == threadId)
                    return;
                while (_lifetimeState != LifetimeClosed)
                    Monitor.Wait(_lifetimeGate);
                return;
            }

            _lifetimeState = LifetimeClosing;
            _lifetimeOwnerThreadId = threadId;
        }

        DisposeCore();
    }

    internal void ValidateDisposePreconditions()
    {
        ThrowIfRestrictedWorldApi();
        if (JobExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                "World cannot be disposed from a running Job callback.");
        }
        ThrowIfCurrentThreadHasWorldAdmission();
        ThrowIfCurrentThreadHasSerializationWriteLifetime();
    }

    private void DisposeCore()
    {
        var exceptions = new List<Exception>();
        try
        {
            _jobLifetime.CloseAndComplete(exceptions);
            WaitForSerializationWriteLifetimesToDrain();
            WaitForUnboundJobAdmissionsToDrain();

            try
            {
                using WorldJobAdmissionScope admission = EnterJobAdmission(
                    WorldJobAdmissionRequest.ForTopologyControlPlane(),
                    allowClosing: true);
                CaptureTeardownFailure(_commands.Dispose, exceptions);
                CaptureTeardownFailure(_hooks.Clear, exceptions);
                CaptureTeardownFailure(ReleasePublishedStorage, exceptions);
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }
        finally
        {
            lock (_lifetimeGate)
            {
                _lifetimeOwnerThreadId = 0;
                _lifetimeState = LifetimeClosed;
                Monitor.PulseAll(_lifetimeGate);
            }
        }

        if (exceptions.Count != 0)
            throw new AggregateException("World disposal failed.", exceptions);
    }

    private void ReleasePublishedStorage()
    {
        WorldStructureRoot empty = WorldStructureRoot.Create(
            this,
            initialEntityCapacity: 1,
            _hooks);
        Volatile.Write(
            ref _publishedStructure,
            new WorldStructurePublication(empty, NextStructureEpoch()));
    }

    private static void CaptureTeardownFailure(
        Action teardown,
        List<Exception> exceptions)
    {
        try
        {
            teardown();
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }
    }
}
