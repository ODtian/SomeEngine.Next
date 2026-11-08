using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Core.ECS;

public sealed class GameWorld : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace = new("ecs", "GameWorld.Update");
    private static readonly SomeEngine.Diagnostics.TraceSite SystemsUpdateTrace = new("ecs", "GameWorld.Systems.Update");
    private const int LifetimeOpen = 0;
    private const int LifetimeClosing = 1;
    private const int LifetimeClosed = 2;

    private readonly object _lifetimeGate = new();
    private int _lifetimeState;
    private int _lifetimeOwnerThreadId;
    private int _updatingThread;
    private readonly SystemGroup<ImmediateSystemContext> _transforms;

    public World World { get; }
    public SystemGroup<ImmediateSystemContext> Systems { get; }
    public GameWorld(WorldExecution? execution = null)
    {
        World = new World(execution: execution ?? new WorldJobs(JobSystem.Default));
        Systems = new SystemGroup<ImmediateSystemContext>(
            new ImmediateSystemDriver(World));
        _transforms = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
        _transforms.Add(new TransformSystem());
    }

    public void Update(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaTime));
        int thread = Environment.CurrentManagedThreadId;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_lifetimeState != LifetimeOpen, this);
            if (_updatingThread != 0)
                throw new InvalidOperationException("GameWorld is already updating.");
            _updatingThread = thread;
        }
        using var scope = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        try
        {
            var context = new ImmediateSystemContext(World, 0, 0, deltaTime);
            using (SomeEngine.Diagnostics.EngineTrace.Begin(SystemsUpdateTrace))
                Systems.Update(ref context);
            Systems.Complete();
            World.Flush();
            using (Profiler.BeginScope("GameWorld.Transforms.Update"))
                _transforms.Update(ref context);
            _transforms.Complete();
        }
        finally
        {
            lock (_lifetimeGate)
            {
                _updatingThread = 0;
                Monitor.PulseAll(_lifetimeGate);
            }
        }
    }

    public void Dispose()
    {
        if (JobSystem.IsExecutingJob)
        {
            throw new InvalidOperationException(
                "GameWorld cannot be disposed from a running Job callback.");
        }
        Systems.ValidateDisposePreconditions(World);
        _transforms.ValidateDisposePreconditions(World);

        int threadId = Environment.CurrentManagedThreadId;
        lock (_lifetimeGate)
        {
            if (_updatingThread == threadId)
                throw new InvalidOperationException("GameWorld cannot be disposed from its update callback.");
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
            while (_updatingThread != 0)
                Monitor.Wait(_lifetimeGate);
        }

        var exceptions = new List<Exception>();
        try
        {
            try
            {
                Systems.Dispose();
            }
            catch (AggregateException exception)
            {
                exceptions.AddRange(exception.Flatten().InnerExceptions);
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }

            try
            {
                _transforms.Dispose();
            }
            catch (AggregateException exception)
            {
                exceptions.AddRange(exception.Flatten().InnerExceptions);
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }

            try
            {
                World.Dispose();
            }
            catch (AggregateException exception)
            {
                exceptions.AddRange(exception.Flatten().InnerExceptions);
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
            throw new AggregateException("GameWorld disposal failed.", exceptions);
    }
}

