namespace SomeEngine.Job.Tests;

public sealed class CompletionNotificationTests
{
    [Fact]
    public async Task SynchronousReadProceedsAfterCpuWorkWhileChildFenceIsIncomplete()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 1 });
        JobResource resource = JobSystem.CreateResource("CPU data with outstanding child fence");
        var fence = new TestFence();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var reading = new ManualResetEventSlim();
        JobHandle producer = JobSystem.Schedule(new SubmitFence(fence, entered, release),
            JobResourceAccess.Write(resource));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task reader = Task.Run(() =>
        {
            using var access = JobSystem.AcquireSynchronousAccess(JobResourceAccess.Read(resource));
            reading.Set();
        });
        try
        {
            Assert.False(reading.Wait(TimeSpan.FromMilliseconds(50)));
            release.Set();
            Assert.True(reading.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(producer.IsCompleted);
        }
        finally
        {
            release.Set();
            fence.Signal();
            producer.Complete();
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
            JobSystem.ReleaseResource(resource);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task NestedCompletionObservesFenceSignalsWithoutNewQueuedWork(int workerCount)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = workerCount });
        for (int iteration = 0; iteration < 128; iteration++)
        {
            var fence = new TestFence();
            JobHandle dependency = scheduler.CreateExternalFenceHandle(fence);
            using var entered = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            JobHandle parent = scheduler.Schedule(new WaitForFence(dependency, entered, finished));
            Task completion = Task.Run(parent.Complete);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            fence.Signal();
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(finished.IsSet);
        }
    }

    private readonly struct WaitForFence(JobHandle dependency, ManualResetEventSlim entered,
        ManualResetEventSlim finished) : IJob
    {
        public void Execute()
        {
            entered.Set();
            dependency.Complete();
            finished.Set();
        }
    }

    private sealed class TestFence : IJobExternalFence
    {
        private readonly Lock _gate = new();
        private bool _signaled;
        private Action<object?>? _callback;
        private object? _state;

        public bool IsSignaled { get { lock (_gate) return _signaled; } }

        public void OnSignaled(Action<object?> callback, object? state)
        {
            lock (_gate)
            {
                if (!_signaled)
                {
                    _callback = callback;
                    _state = state;
                    return;
                }
            }
            callback(state);
        }

        internal void Signal()
        {
            Action<object?>? callback;
            object? state;
            lock (_gate)
            {
                _signaled = true;
                callback = _callback;
                state = _state;
            }
            callback?.Invoke(state);
        }
    }

    private readonly struct SubmitFence(IJobExternalFence fence, ManualResetEventSlim entered,
        ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            JobSystem.CreateExternalFenceHandle(fence);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }
    }
}
