using Xunit;

namespace SomeEngine.Job.Tests;

public sealed class SynchronousWorkTests
{
    [Fact]
    public void BorrowedSpansUseTheDeclaredRangeAcrossNestedSynchronousWork()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        int[] values = [1, 2, 3];
        JobResourceAccess write = JobResourceAccess.Write(scheduler, values, 0, values.Length);
        var work = new NestedSpanWork(scheduler, values.AsSpan(), write);

        scheduler.Execute(ref work, [write]);

        Assert.Equal(new[] { 2, 3, 4 }, values);
        Assert.Equal(9, work.Sum);
    }

    [Fact]
    public void MissingNestedAccessReleasesTheOuterRangeAndRestoresTheCallingThread()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        int[] declared = [0], values = [3];
        JobResourceAccess outer = JobResourceAccess.Write(scheduler, declared, 0, 1);
        JobResourceAccess required = JobResourceAccess.Write(scheduler, values, 0, 1);
        var rejected = new NestedSpanWork(scheduler, values.AsSpan(), required);
        JobResourceSafetyException? failure = null;
        try { scheduler.Execute(ref rejected, [outer]); }
        catch (JobResourceSafetyException exception) { failure = exception; }
        Assert.NotNull(failure);
        Assert.Equal(3, values[0]);

        scheduler.Schedule(new WriteArray(declared), [outer]).Complete();
        var accepted = new NestedSpanWork(scheduler, values.AsSpan(), required);
        scheduler.Execute(ref accepted, [required]);

        Assert.Equal(7, declared[0]);
        Assert.Equal(4, values[0]);
        Assert.Equal(4, accepted.Sum);
    }

    private ref struct NestedSpanWork : IJob
    {
        private readonly Scheduler _scheduler;
        private readonly Span<int> _values;
        private readonly JobResourceAccess _required;
        internal int Sum;
        internal NestedSpanWork(Scheduler scheduler,Span<int> values,JobResourceAccess required)
        { _scheduler=scheduler; _values=values; _required=required; Sum=0; }
        public void Execute()
        {
            var add=new AddSpan(_values);
            _scheduler.Execute(ref add,[_required]);
            foreach(int value in _values) Sum+=value;
        }
    }
    private readonly ref struct AddSpan : IJob
    {
        private readonly Span<int> _values;
        internal AddSpan(Span<int> values)=>_values=values;
        public void Execute() { for(int index=0;index<_values.Length;index++) _values[index]++; }
    }
    private readonly struct WriteArray(int[] values) : IJob
    {
        public void Execute() => values[0] = 7;
    }
}
