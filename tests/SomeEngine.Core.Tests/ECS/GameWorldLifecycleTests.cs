using System.Reflection;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Core.Tests.ECS;

public sealed class GameWorldLifecycleTests
{
    [Fact]
    public void GameWorldUsesTheCanonicalImmediateSystemContextWithoutForwardingAliases()
    {
        Assembly assembly = typeof(GameWorld).Assembly;
        Assert.Null(assembly.GetType(
            "SomeEngine.Core.ECS.EngineSystemContext",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "SomeEngine.Core.ECS.EngineDriver",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "SomeEngine.Core.ECS.SystemContext",
            throwOnError: false));

        PropertyInfo? systems = typeof(GameWorld).GetProperty(
            nameof(GameWorld.Systems),
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(systems);
        Assert.Equal(
            typeof(SystemGroup<ImmediateSystemContext>),
            systems!.PropertyType);
    }

    [Fact]
    public async Task Dispose_WaitsWorldJobsAndClosesSystemsAndWorld()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new BlockingState();
        var gameWorld = new GameWorld();
        ComponentJobAccess<LocalTransform>.ScheduleRead(
            gameWorld.World,
            new BlockingJob(state));
        Assert.True(state.Started.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> disposal = StartLongRunning(gameWorld.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ThrowsDisposed(() => _ = gameWorld.World.EntityCount),
                TimeSpan.FromSeconds(5)));
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            state.Release.Set();
        }

        Assert.Null(await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => _ = gameWorld.World.EntityCount);
        Assert.Throws<ObjectDisposedException>(() => gameWorld.Systems.Update());
        gameWorld.Dispose();
    }

    [Fact]
    public async Task ConcurrentDispose_WaitsUntilTheOwningTeardownCompletes()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new BlockingDestroyState();
        var gameWorld = new GameWorld();
        gameWorld.Systems.Add(new BlockingDestroySystem(state));
        gameWorld.Update(0);

        Task<Exception?> first = StartLongRunning(gameWorld.Dispose);
        Assert.True(state.Started.Wait(TimeSpan.FromSeconds(5)));

        using var secondStarted = new ManualResetEventSlim();
        Task<Exception?> second = Task.Factory.StartNew(() =>
        {
            secondStarted.Set();
            return Capture(gameWorld.Dispose);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(second.IsCompleted);
        }
        finally
        {
            state.Release.Set();
        }

        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Dispose_StillClosesWorldWhenSystemDestroyFaults()
    {
        using var runtime = new JobRuntimeScope();
        var gameWorld = new GameWorld();
        gameWorld.Systems.Add(new FaultingDestroySystem());
        gameWorld.Update(0);

        AggregateException error = Assert.Throws<AggregateException>(gameWorld.Dispose).Flatten();

        Assert.Contains(error.InnerExceptions, exception => exception.Message == "engine-destroy-fault");
        Assert.Throws<ObjectDisposedException>(() => _ = gameWorld.World.EntityCount);
        gameWorld.Dispose();
    }

    [Fact]
    public void DisposeFromJobCallback_RejectsBeforeMutatingGameWorldState()
    {
        using var runtime = new JobRuntimeScope();
        var gameWorld = new GameWorld();

        JobHandle disposer = JobSystem.Schedule(new DisposeGameWorldJob(gameWorld));
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => disposer.Complete());

        Assert.Contains("Job callback", error.Message, StringComparison.Ordinal);
        gameWorld.Update(0);
        _ = gameWorld.World.EntityCount;
        gameWorld.Dispose();
    }

    [Fact]
    public void DisposeFromSystemCallback_RejectsBeforeMutatingGameWorldState()
    {
        using var runtime = new JobRuntimeScope();
        var gameWorld = new GameWorld();
        var state = new ReentrantDisposeState();
        gameWorld.Systems.Add(new DisposeGameWorldSystem(gameWorld, state));

        gameWorld.Update(0);

        Assert.IsType<InvalidOperationException>(state.Error);
        gameWorld.Update(0);
        _ = gameWorld.World.EntityCount;
        gameWorld.Dispose();
    }

    private static bool ThrowsDisposed(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Task<Exception?> StartLongRunning(Action action) =>
        Task.Factory.StartNew(
            () => Capture(action),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private readonly struct BlockingJob : IJob
    {
        private readonly BlockingState _state;

        internal BlockingJob(BlockingState state)
        {
            _state = state;
        }

        public void Execute()
        {
            _state.Started.Set();
            _state.Release.Wait();
        }
    }

    private readonly struct DisposeGameWorldJob : IJob
    {
        private readonly GameWorld _gameWorld;

        internal DisposeGameWorldJob(GameWorld gameWorld)
        {
            _gameWorld = gameWorld;
        }

        public void Execute()
        {
            _gameWorld.Dispose();
        }
    }

    private readonly struct FaultingDestroySystem : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
        }

        public void OnDestroy(ref ImmediateSystemContext context)
        {
            throw new InvalidOperationException("engine-destroy-fault");
        }
    }

    private readonly struct BlockingDestroySystem : ISystem<ImmediateSystemContext>
    {
        private readonly BlockingDestroyState _state;

        internal BlockingDestroySystem(BlockingDestroyState state)
        {
            _state = state;
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
        }

        public void OnDestroy(ref ImmediateSystemContext context)
        {
            _state.Started.Set();
            _state.Release.Wait();
        }
    }

    private readonly struct DisposeGameWorldSystem : ISystem<ImmediateSystemContext>
    {
        private readonly GameWorld _gameWorld;
        private readonly ReentrantDisposeState _state;

        internal DisposeGameWorldSystem(
            GameWorld gameWorld,
            ReentrantDisposeState state)
        {
            _gameWorld = gameWorld;
            _state = state;
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            if (Interlocked.Exchange(ref _state.Attempted, 1) != 0)
                return;

            try
            {
                _gameWorld.Dispose();
            }
            catch (Exception exception)
            {
                _state.Error = exception;
            }
        }
    }

    private sealed class ReentrantDisposeState
    {
        internal int Attempted;
        internal Exception? Error;
    }

    private sealed class BlockingState : IDisposable
    {
        internal ManualResetEventSlim Started { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void Dispose()
        {
            Started.Dispose();
            Release.Dispose();
        }
    }

    private sealed class BlockingDestroyState : IDisposable
    {
        internal ManualResetEventSlim Started { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void Dispose()
        {
            Release.Set();
            Started.Dispose();
            Release.Dispose();
        }
    }

    private sealed class JobRuntimeScope : IDisposable
    {
        private readonly ManagedPayloadPolicy _payloadPolicy = JobSystem.ManagedPayloadPolicy;
        private readonly JobSafetyMode _safetyMode = JobSystem.SafetyMode;

        internal JobRuntimeScope()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = 2,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
                SafetyMode = JobSafetyMode.Checked,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = 2,
                ManagedPayloadPolicy = _payloadPolicy,
                SafetyMode = _safetyMode,
            });
        }
    }
}
