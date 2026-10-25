using System.Threading.Tasks.Sources;

namespace SomeEngine.Job.Tests;

public sealed class ExternalCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValueTaskFailureReachesItsConsumer(bool completed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var source = new TaskCompletionSource();
        var failure = new ApplicationException();
        if (completed)
            source.SetException(failure);
        JobHandle handle = scheduler.CreateExternalFenceHandle(new ValueTask(source.Task));
        ValueTask result = handle.WaitAsync();
        if (!completed)
            source.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(async () => await result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValueTaskCancellationReachesItsConsumer(bool completed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new TaskCompletionSource();
        if (completed)
            source.SetCanceled(cancellation.Token);
        JobHandle handle = scheduler.CreateExternalFenceHandle(new ValueTask(source.Task));
        Task result = handle.WaitAsync().AsTask();
        if (!completed)
            source.SetCanceled(cancellation.Token);

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => result.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(result.IsCanceled);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task CancellingOneWaiterKeepsTheWorkAndResourceAlive()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var cancellation = new CancellationTokenSource();
        var source = new TaskCompletionSource();
        JobResource resource = scheduler.CreateResource();
        JobHandle handle = scheduler.CreateExternalFenceHandle(source.Task, [JobResourceAccess.Write(resource)]);
        Task shared = handle.WaitAsync().AsTask();
        try
        {
            Task waiting = handle.WaitAsync(cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            Assert.False(handle.IsCompleted);
            Assert.False(shared.IsCompleted);
            Assert.Throws<JobResourceSafetyException>(() => scheduler.ReleaseResource(resource));
        }
        finally
        {
            source.TrySetResult();
            await shared.WaitAsync(TimeSpan.FromSeconds(10));
            scheduler.ReleaseResource(resource);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskCancellationReachesItsConsumer(bool completed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new TaskCompletionSource();
        if (completed)
            source.SetCanceled(cancellation.Token);
        JobHandle handle = scheduler.CreateExternalFenceHandle(source.Task);
        Task result = handle.WaitAsync().AsTask();
        if (!completed)
            source.SetCanceled(cancellation.Token);

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => result.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(result.IsCanceled);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueTaskSourceIsConsumedBeforeDependentWork(bool completed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var source = new NumberSource();
        if (completed)
            source.Complete();
        JobHandle handle = scheduler.CreateExternalFenceHandle(source.Wait());
        int[] values = [0];
        JobHandle read = scheduler.Schedule(new ReadNumber(source, values), handle);
        if (!completed)
            source.Complete();
        read.Complete();

        Assert.Equal(42, values[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskFailureReachesItsConsumer(bool completed)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var source = new TaskCompletionSource();
        var failure = new ApplicationException();
        if (completed)
            source.SetException(failure);
        JobHandle handle = scheduler.CreateExternalFenceHandle(source.Task);
        ValueTask result = handle.WaitAsync();
        if (!completed)
            source.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(async () => await result));
    }

    [Fact]
    public async Task ExternalFenceFailureReachesItsConsumer()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        var failure = new ApplicationException();
        JobHandle handle = scheduler.CreateExternalFenceHandle(new FailedFence(failure));

        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(
            async () => await handle.WaitAsync()));
    }

    private sealed class FailedFence(Exception failure) : IJobExternalFence
    {
        public bool IsSignaled => true;
        public void OnSignaled(Action<object?> continuation, object? state) => continuation(state);
        public void GetResult() => throw failure;
    }

    private readonly record struct ReadNumber(NumberSource Source, int[] Values) : IJob
    {
        public void Execute() => Values[0] = Source.Value;
    }

    private sealed class NumberSource : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _completion;
        internal int Value { get; private set; }
        internal ValueTask Wait() => new(this, _completion.Version);
        internal void Complete() => _completion.SetResult(true);

        public void GetResult(short token)
        {
            _completion.GetResult(token);
            Value = 42;
        }

        public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
    }
}
