using System.Diagnostics.CodeAnalysis;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    private readonly object _useGate = new();
    private readonly Lock _resourceGate = new();
    private readonly Dictionary<WorldStorageResourceKey, JobResourceToken> _resources = new();
    private readonly Dictionary<JobResourceToken, WorldStorageResourceKey> _storage = new();
    private World? _world;
    private int _uses;
    private bool _closing;

    /// <summary>
    /// Selects the scheduler for one World. Disposing the World waits for its data users, but
    /// does not stop the shared scheduler or adopt the failure of every task that reads it.
    /// The task handle or its enclosing system execution remains responsible for that failure.
    /// </summary>
    public WorldJobs(Scheduler scheduler) =>
        Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    public Scheduler Scheduler { get; }

    public static World CreateWorld(int initialEntityCapacity = 256, Scheduler? scheduler = null) =>
        new(initialEntityCapacity, new WorldJobs(scheduler ?? JobSystem.Default));

    /// <summary>Returns the scheduler selected for this World's component jobs.</summary>
    public static Scheduler GetScheduler(World world) => WorldStorageJobResources.RequireJobs(world).Scheduler;

    /// <summary>Finds this World's selected scheduler so synchronous callers can retain direct execution for a synchronous World.</summary>
    public static bool TryGetScheduler(World world, [NotNullWhen(true)] out Scheduler? scheduler)
    {
        scheduler = (world.Execution as WorldJobs)?.Scheduler;
        return scheduler is not null;
    }

    /// <summary>Joins stage completions on the scheduler selected by this World.</summary>
    public static JobHandle CombineDependencies(World world, ReadOnlySpan<JobHandle> dependencies) =>
        WorldStorageJobResources.RequireJobs(world).Scheduler.CombineDependencies(dependencies);

    internal override void Attach(World world)
    {
        if (Interlocked.CompareExchange(ref _world, world, null) is not null)
            throw new InvalidOperationException("A WorldJobs instance can execute only the World it was created for.");
    }

    internal override bool IsExecutingCurrentScope =>
        Scheduler.Uses(Scheduler.GetCurrentScope(), this);

    internal JobSubmissionScope BeginSubmission() => JobSystem.EnterSubmissionScope(this);

    internal JobResourceToken Resource(WorldStorageResourceKey storage)
    {
        World world = _world ?? throw new InvalidOperationException("WorldJobs has not been attached to a World.");
        world.ThrowIfUnavailable();
        lock (_resourceGate)
        {
            if (_resources.TryGetValue(storage, out JobResourceToken resource))
                return resource;

            resource = Scheduler.CreateResourceToken($"ECS {storage.Kind} {storage.ComponentId}", this);
            try
            {
                _resources.Add(storage, resource);
                _storage.Add(resource, storage);
                return resource;
            }
            catch
            {
                _resources.Remove(storage);
                _storage.Remove(resource);
                Scheduler.ReleaseResourceToken(resource);
                throw;
            }
        }
    }

    internal bool TryDescribe(JobResourceAccess access, out WorldStorageResourceKey storage)
    {
        if (access.Kind != ResourceKind.Token)
        {
            storage = default;
            return false;
        }
        lock (_resourceGate)
            return _storage.TryGetValue(
                new JobResourceToken(access.Id, access.Version, access.Generation), out storage);
    }

    private void BeginUse()
    {
        lock (_useGate)
        {
            ObjectDisposedException.ThrowIf(_closing, _world!);
            _uses++;
        }
    }

    private void EndUse()
    {
        lock (_useGate)
        {
            _uses--;
            if (_uses == 0)
                Monitor.PulseAll(_useGate);
        }
    }

    bool IJobSubmissionObserver.TryBeginSubmission(JobHandle currentScope)
    {
        if (Scheduler.Uses(currentScope, this))
            return false;
        BeginUse();
        return true;
    }

    void IJobSubmissionObserver.CommitSubmission(JobHandle handle) { }
    void IJobSubmissionObserver.RollbackSubmission(JobHandle handle) => EndUse();
    void IJobSubmissionObserver.CompleteSubmission(JobHandle handle) => EndUse();

    internal override void Close()
    {
        lock (_useGate)
            _closing = true;

        while (true)
        {
            lock (_useGate)
            {
                if (_uses == 0)
                    return;
            }
            // The scheduler may have no dedicated workers. Helping executes the same accepted
            // tasks; completion still has one authority and no second per-World task tree.
            if (!Scheduler.HelpComplete())
            {
                lock (_useGate)
                    if (_uses != 0)
                        Monitor.Wait(_useGate, 1);
            }
        }
    }

    internal override void Release()
    {
        lock (_resourceGate)
        {
            foreach (JobResourceToken resource in _resources.Values)
                Scheduler.ReleaseResourceToken(resource);
            _resources.Clear();
            _storage.Clear();
        }
    }
}
