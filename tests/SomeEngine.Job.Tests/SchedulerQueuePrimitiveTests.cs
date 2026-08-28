namespace SomeEngine.Job.Tests;

public sealed class SchedulerQueuePrimitiveTests
{
    [Fact]
    public void MpmcInjectorIsBoundedAndFifoForSingleProducer()
    {
        var queue = new MpmcInjector<int>(4);

        Assert.True(queue.TryEnqueue(1));
        Assert.True(queue.TryEnqueue(2));
        Assert.True(queue.TryEnqueue(3));
        Assert.True(queue.TryEnqueue(4));
        Assert.False(queue.TryEnqueue(5));

        for (int expected = 1; expected <= 4; expected++)
        {
            Assert.True(queue.TryDequeue(out int actual));
            Assert.Equal(expected, actual);
        }
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public async Task MpmcInjectorConcurrentProducersAndConsumersDeliverExactlyOnce()
    {
        const int producerCount = 4;
        const int consumerCount = 4;
        const int itemsPerProducer = 20_000;
        const int total = producerCount * itemsPerProducer;
        var queue = new MpmcInjector<int>(1_024);
        var seen = new int[total];
        int consumed = 0;
        using var start = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task[] consumers = Enumerable.Range(0, consumerCount).Select(_ => Task.Run(() =>
        {
            start.Wait(timeout.Token);
            var spin = new SpinWait();
            while (Volatile.Read(ref consumed) < total)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!queue.TryDequeue(out int item))
                {
                    spin.SpinOnce();
                    continue;
                }

                Assert.InRange(item, 0, total - 1);
                Assert.Equal(1, Interlocked.Increment(ref seen[item]));
                Interlocked.Increment(ref consumed);
                spin = new SpinWait();
            }
        }, timeout.Token)).ToArray();

        Task[] producers = Enumerable.Range(0, producerCount).Select(producer => Task.Run(() =>
        {
            start.Wait(timeout.Token);
            var spin = new SpinWait();
            int first = producer * itemsPerProducer;
            for (int i = 0; i < itemsPerProducer; i++)
            {
                int item = first + i;
                while (!queue.TryEnqueue(item))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    spin.SpinOnce();
                }
                spin = new SpinWait();
            }
        }, timeout.Token)).ToArray();

        start.Set();
        await Task.WhenAll(producers);
        await Task.WhenAll(consumers);

        Assert.Equal(total, consumed);
        Assert.All(seen, count => Assert.Equal(1, count));
    }

    [Fact]
    public void ChaseLevDequeUsesOwnerLifoAndThiefFifoOrdering()
    {
        var deque = new ChaseLevDeque<int>(4);
        Assert.True(deque.TryPush(1));
        Assert.True(deque.TryPush(2));
        Assert.True(deque.TryPush(3));
        Assert.True(deque.TryPush(4));
        Assert.False(deque.TryPush(5));

        Assert.True(deque.TryPop(out int owner));
        Assert.Equal(4, owner);
        Assert.True(deque.TrySteal(out int thief));
        Assert.Equal(1, thief);
        Assert.True(deque.TryPop(out owner));
        Assert.Equal(3, owner);
        Assert.True(deque.TryPop(out owner));
        Assert.Equal(2, owner);
        Assert.True(deque.IsEmpty);
    }

    [Fact]
    public async Task ChaseLevDequeConcurrentStealsAndOwnerPopsDeliverExactlyOnce()
    {
        const int count = 2_000;
        var deque = new ChaseLevDeque<int>(4_096);
        var seen = new int[count];
        int consumed = 0;
        for (int i = 0; i < count; i++)
            Assert.True(deque.TryPush(i));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task[] thieves = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref consumed) < count)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!deque.TrySteal(out int item))
                {
                    spin.SpinOnce();
                    continue;
                }

                Assert.Equal(1, Interlocked.Increment(ref seen[item]));
                Interlocked.Increment(ref consumed);
                spin = new SpinWait();
            }
        }, timeout.Token)).ToArray();

        var ownerSpin = new SpinWait();
        while (Volatile.Read(ref consumed) < count)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!deque.TryPop(out int item))
            {
                ownerSpin.SpinOnce();
                continue;
            }

            Assert.Equal(1, Interlocked.Increment(ref seen[item]));
            Interlocked.Increment(ref consumed);
            ownerSpin = new SpinWait();
        }

        await Task.WhenAll(thieves);
        Assert.All(seen, value => Assert.Equal(1, value));
    }
}
