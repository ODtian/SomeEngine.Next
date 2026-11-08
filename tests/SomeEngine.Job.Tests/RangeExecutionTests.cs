using System.Collections.Concurrent;

namespace SomeEngine.Job.Tests;

public sealed class RangeExecutionTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    public void EveryRangeRunsOnceWithTheCallingThreadParticipating(int count)
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        int[] items = Enumerable.Range(0, count).ToArray();
        int[] visits = new int[count], threads = new int[count];
        int caller = Environment.CurrentManagedThreadId;
        using var helperStarted = new ManualResetEventSlim();

        scheduler.ExecuteRanges(items, count, new VisitItems(visits, threads, helperStarted));

        Assert.All(visits, value => Assert.Equal(1, value));
        Assert.Equal(caller, threads[0]);
        Assert.Contains(threads, thread => thread != caller);
    }

    [Fact]
    public void TheParentCompletionIncludesChildrenScheduledByItsRanges()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        int[] values = new int[8];
        int[] items = Enumerable.Range(0, values.Length).ToArray();

        scheduler.Schedule(new ScheduleRanges(scheduler, items, values)).Complete();

        Assert.Equal(Enumerable.Range(1, values.Length), values);
    }

    [Fact]
    public async Task RangeFailureIsDeliveredAfterTheOtherRangeFinishes()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        Task<Exception?> execution = Task.Run<Exception?>(() =>
        {
            Exception? failure = Record.Exception(() => scheduler.ExecuteRanges(new[] { 0, 1 }, 2,
                new FailAndWait(entered, release, events)));
            events.Enqueue("failure delivered");
            return failure;
        });
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }

        Assert.IsType<InvalidOperationException>(await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(["range finished", "failure delivered"], events);
    }

    private readonly record struct VisitItems(int[] Visits, int[] Threads, ManualResetEventSlim HelperStarted) : IRangeWork<int>
    {
        public void Execute(in int item)
        {
            Threads[item] = Environment.CurrentManagedThreadId;
            if (item == 0) Assert.True(HelperStarted.Wait(TimeSpan.FromSeconds(10)));
            else HelperStarted.Set();
            Interlocked.Increment(ref Visits[item]);
        }
    }

    private readonly record struct ScheduleRanges(Scheduler Scheduler, int[] Items, int[] Values) : IJob
    {
        public void Execute() => Scheduler.ExecuteRanges(Items, Items.Length, new ScheduleChild(Scheduler, Values));
    }

    private readonly record struct ScheduleChild(Scheduler Scheduler, int[] Values) : IRangeWork<int>
    {
        public void Execute(in int item) => Scheduler.Schedule(new StoreValue(Values, item));
    }

    private readonly record struct StoreValue(int[] Values, int Index) : IJob
    {
        public void Execute() => Values[Index] = Index + 1;
    }

    private readonly record struct FailAndWait(ManualResetEventSlim Entered, ManualResetEventSlim Release,
        ConcurrentQueue<string> Events) : IRangeWork<int>
    {
        public void Execute(in int item)
        {
            if (item == 0) throw new InvalidOperationException();
            Entered.Set();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(10)));
            Events.Enqueue("range finished");
        }
    }
}
