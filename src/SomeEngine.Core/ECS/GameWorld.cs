using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Core.ECS;

public sealed class GameWorld : IDisposable
{
    private const int LifetimeOpen = 0;
    private const int LifetimeClosing = 1;
    private const int LifetimeClosed = 2;

    private readonly object _lifetimeGate = new();
    private int _lifetimeState;
    private int _lifetimeOwnerThreadId;

    public World World { get; }
    public SystemGroup<ImmediateSystemContext> Systems { get; }
    public GameWorld()
    {
        World = new World();
        Systems = new SystemGroup<ImmediateSystemContext>(
            new ImmediateSystemDriver(World));
        Systems.Add(new TransformSystem());
    }

    public void Update(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _lifetimeState) != LifetimeOpen,
            this);
        using var scope = Profiler.BeginScope("GameWorld.Update");

        using (Profiler.BeginScope("GameWorld.Systems.Update"))
        {
            Systems.Update();
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

