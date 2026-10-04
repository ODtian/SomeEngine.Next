using SomeEngine.Job;

namespace SomeEngine.ECS;

public partial class World
{
    private const int LifetimeOpen = 0;
    private const int LifetimeClosing = 1;
    private const int LifetimeClosed = 2;

    private readonly object _lifetimeGate = new();
    private int _lifetimeState;
    private int _lifetimeOwnerThreadId;

    internal void ThrowIfUnavailable()
    {
        int state = Volatile.Read(ref _lifetimeState);
        if (state == LifetimeOpen)
            return;

        // A root admitted before closure may continue using its World while Dispose waits its
        // full scheduler scope. The closing thread may help execute unrelated jobs while
        // joining those roots; thread equality alone must not grant such a callback access.
        if (state == LifetimeClosing &&
            (_execution?.IsExecutingCurrentScope == true ||
             OwnsCurrentSerializationWriteLifetime() ||
             (!JobExecutionContext.IsActive &&
              Volatile.Read(ref _lifetimeOwnerThreadId) == Environment.CurrentManagedThreadId)))
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
            _execution?.Close();
            WaitForSerializationWriteLifetimesToDrain();
            WaitForUnboundJobAdmissionsToDrain();

            try
            {
                CaptureTeardownFailure(_commands.Dispose, exceptions);
                CaptureTeardownFailure(_hooks.Clear, exceptions);
                CaptureTeardownFailure(ReleasePublishedStorage, exceptions);
                if (_execution is not null)
                    CaptureTeardownFailure(_execution.Release, exceptions);
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
        EntityDestroying = null;
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
