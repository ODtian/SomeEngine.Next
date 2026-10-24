using System.Runtime.CompilerServices;
using SomeEngine.Core.Collections;

namespace SomeEngine.Core.Tests.Collections;

public sealed class WeakValuesTests
{
    [Fact]
    public async Task LiveValueIsReturnedForTheSameKey()
    {
        var values = new WeakValues<string, TestValue>();
        _ = values.GetOrBegin("item", out var completion);
        var expected = new TestValue(7);
        await values.CompleteAsync("item", completion!, new ValueTask<TestValue>(expected));

        TestValue actual = await values.GetOrBegin("item", out var nextCompletion);

        Assert.Null(nextCompletion);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ConcurrentReadersShareTheCompleteCreation()
    {
        var values = new WeakValues<string, TestValue>();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0;
        int reads = 0;
        int creates = 0;

        async ValueTask<TestValue> CreateAsync()
        {
            Interlocked.Increment(ref opens);
            await opened.Task;
            Interlocked.Increment(ref reads);
            read.SetResult();
            Interlocked.Increment(ref creates);
            return new TestValue(42);
        }

        async Task<TestValue> GetAsync()
        {
            ValueTask<TestValue> result = values.GetOrBegin("item", out var completion);
            return completion is null
                ? await result
                : await values.CompleteAsync("item", completion, CreateAsync());
        }

        Task<TestValue>[] requests = Enumerable.Range(0, 32).Select(_ => GetAsync()).ToArray();
        Assert.Equal(1, opens);
        opened.SetResult();
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        TestValue[] results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, reads);
        Assert.Equal(1, creates);
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public async Task FailedCreationIsSharedAndTheNextRequestRetries()
    {
        var values = new WeakValues<string, TestValue>();
        ValueTask<TestValue> first = values.GetOrBegin("item", out var completion);
        ValueTask<TestValue> second = values.GetOrBegin("item", out var secondCompletion);

        await Assert.ThrowsAsync<IOException>(() => values.CompleteAsync("item", completion!,
            ValueTask.FromException<TestValue>(new IOException())).AsTask());
        await Assert.ThrowsAsync<IOException>(() => first.AsTask());
        await Assert.ThrowsAsync<IOException>(() => second.AsTask());
        Assert.Null(secondCompletion);

        _ = values.GetOrBegin("item", out var retryCompletion);
        var expected = new TestValue(9);
        TestValue actual = await values.CompleteAsync("item", retryCompletion!, new ValueTask<TestValue>(expected));

        Assert.Same(expected, actual);
        Assert.Same(expected, await values.GetOrBegin("item", out _));
    }

    [Fact]
    public async Task CancelingAWaiterKeepsTheSharedCreationAvailable()
    {
        var values = new WeakValues<string, TestValue>();
        _ = values.GetOrBegin("item", out var completion);
        var creation = new TaskCompletionSource<TestValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<TestValue> publishing = values.CompleteAsync("item", completion!, new ValueTask<TestValue>(creation.Task));
        using var cancellation = new CancellationTokenSource();
        Task<TestValue> waiting = values.GetOrBegin("item", out _).AsTask().WaitAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var expected = new TestValue(11);
        creation.SetResult(expected);

        Assert.Same(expected, await publishing);
        Assert.Same(expected, await values.GetOrBegin("item", out _));
    }

    [Fact]
    public async Task CanceledCreationIsSharedAndTheNextRequestRetries()
    {
        var values = new WeakValues<string, TestValue>();
        ValueTask<TestValue> first = values.GetOrBegin("item", out var completion);
        ValueTask<TestValue> second = values.GetOrBegin("item", out _);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => values.CompleteAsync("item", completion!,
            ValueTask.FromCanceled<TestValue>(cancellation.Token)).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AsTask());
        _ = values.GetOrBegin("item", out var retryCompletion);
        var expected = new TestValue(12);

        Assert.Same(expected, await values.CompleteAsync("item", retryCompletion!, new ValueTask<TestValue>(expected)));
    }

    [Fact]
    public async Task ACollectedValueIsCreatedAgain()
    {
        var values = new WeakValues<int, TestValue>();
        WeakReference<TestValue> oldValue = AddCollectibleValue(values, 1);
        Collect();
        Assert.False(oldValue.TryGetTarget(out _));

        _ = values.GetOrBegin(1, out var completion);
        Assert.NotNull(completion);
        var expected = new TestValue(2);
        await values.CompleteAsync(1, completion, new ValueTask<TestValue>(expected));

        Assert.Same(expected, await values.GetOrBegin(1, out _));
    }

    [Fact]
    public async Task NewCreationsReleaseCollectedKeysAndReuseTheirRecords()
    {
        var values = new WeakValues<TestKey, TestValue>();
        WeakReference<TestKey>[] collectedKeys = AddCollectibleKeys(values);
        var retainedKey = new TestKey(1000);
        var retainedValue = new TestValue(1000);
        _ = values.GetOrBegin(retainedKey, out var retainedCompletion);
        await values.CompleteAsync(retainedKey, retainedCompletion!, new ValueTask<TestValue>(retainedValue));
        Collect();

        var newValues = new List<TestValue>();
        for (int index = 0; index < 128; index++)
        {
            var key = new TestKey(2000 + index);
            _ = values.GetOrBegin(key, out var completion);
            var value = new TestValue(index);
            newValues.Add(await values.CompleteAsync(key, completion!, new ValueTask<TestValue>(value)));
        }
        Collect();

        Assert.All(collectedKeys, key => Assert.False(key.TryGetTarget(out _)));
        Assert.Same(retainedValue, await values.GetOrBegin(retainedKey, out _));
        for (int index = 0; index < newValues.Count; index++)
            Assert.Same(newValues[index], await values.GetOrBegin(new TestKey(2000 + index), out _));
    }

    [Fact]
    public async Task GrowthKeepsEachGuidAssociatedWithItsValue()
    {
        var values = new WeakValues<Guid, TestValue>();
        var expected = new Dictionary<Guid, TestValue>();
        for (int index = 0; index < 128; index++)
        {
            Guid key = Guid.NewGuid();
            var value = new TestValue(index);
            _ = values.GetOrBegin(key, out var completion);
            expected.Add(key, await values.CompleteAsync(key, completion!, new ValueTask<TestValue>(value)));
        }

        foreach ((Guid key, TestValue value) in expected)
            Assert.Same(value, await values.GetOrBegin(key, out _));
    }

    [Fact]
    public void CollectingTheIndexKeepsAnOrdinaryValueReferenceUsable()
    {
        var value = new TestValue(81);
        WeakReference<WeakValues<int, TestValue>> values = AddCollectibleIndex(value);

        Collect();

        Assert.False(values.TryGetTarget(out _));
        Assert.Equal(81, value.Number);
        GC.KeepAlive(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TestValue> AddCollectibleValue(WeakValues<int, TestValue> values, int key)
    {
        var value = new TestValue(key);
        _ = values.GetOrBegin(key, out var completion);
        _ = values.CompleteAsync(key, completion!, new ValueTask<TestValue>(value)).Result;
        return new WeakReference<TestValue>(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TestKey>[] AddCollectibleKeys(WeakValues<TestKey, TestValue> values)
    {
        var keys = new WeakReference<TestKey>[64];
        var heldValues = new TestValue[64];
        for (int index = 0; index < keys.Length; index++)
        {
            var key = new TestKey(index);
            var value = new TestValue(index);
            heldValues[index] = value;
            _ = values.GetOrBegin(key, out var completion);
            _ = values.CompleteAsync(key, completion!, new ValueTask<TestValue>(value)).Result;
            keys[index] = new WeakReference<TestKey>(key);
        }
        GC.KeepAlive(heldValues);
        return keys;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<WeakValues<int, TestValue>> AddCollectibleIndex(TestValue value)
    {
        var values = new WeakValues<int, TestValue>();
        _ = values.GetOrBegin(1, out var completion);
        _ = values.CompleteAsync(1, completion!, new ValueTask<TestValue>(value)).Result;
        return new WeakReference<WeakValues<int, TestValue>>(values);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record TestKey(int Number)
    {
        public override int GetHashCode() => 1;
    }

    private sealed record TestValue(int Number);
}
