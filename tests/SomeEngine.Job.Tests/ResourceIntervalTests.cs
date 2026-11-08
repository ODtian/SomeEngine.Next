namespace SomeEngine.Job.Tests;

public sealed class ResourceIntervalTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AdjacentAndOverlappingReadIntervalsWaitForThePrecedingWriter(int secondStart)
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1 });
        JobResource resource = scheduler.CreateResource();
        var events = new ExecutionEvents();
        using var entered = events.CreateSignal();
        using var release = events.CreateSignal();
        using var written = events.CreateSignal();
        using var read = events.CreateSignal();
        JobHandle writer = scheduler.Schedule(new GatedJob(entered, release, written),
            JobResourceAccess.Write(resource, secondStart + 1, 1));
        entered.Wait();
        JobResourceAccess[] reads = Enumerable.Range(0, 8)
            .Select(index => JobResourceAccess.Read(resource, index % 2 == 0 ? 0 : secondStart, 2)).ToArray();

        JobHandle reader = scheduler.Schedule(new SignalJob(read), reads);
        scheduler.Schedule(new SignalJob(release)).Complete();
        scheduler.CombineDependencies([writer, reader]).Complete();

        events.AssertBefore(written, read);
    }

    [Fact]
    public void SeparateWriteIntervalsAllowTheGapToExecuteConcurrently()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1 });
        JobResource resource = scheduler.CreateResource();
        var events = new ExecutionEvents();
        using var entered = events.CreateSignal();
        using var release = events.CreateSignal();
        using var finished = events.CreateSignal();
        using var gap = events.CreateSignal();
        JobResourceAccess[] writes = Enumerable.Range(0, 8)
            .Select(index => JobResourceAccess.Write(resource, index % 2 == 0 ? 0 : 4, 2)).ToArray();
        JobHandle outside = scheduler.Schedule(new GatedJob(entered, release, finished), writes);
        entered.Wait();

        JobHandle inside = scheduler.Schedule(new SignalJob(gap), JobResourceAccess.Write(resource, 2, 2));
        scheduler.Schedule(new SignalJob(release)).Complete();
        scheduler.CombineDependencies([outside, inside]).Complete();

        events.AssertBefore(gap, finished);
    }

    [Fact]
    public void ReadIntervalsShareAccessWhileSeparateIntervalsAreWritten()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1 });
        JobResource resource = scheduler.CreateResource();
        var events = new ExecutionEvents();
        using var entered = events.CreateSignal();
        using var release = events.CreateSignal();
        using var finished = events.CreateSignal();
        using var shared = events.CreateSignal();
        JobHandle reader = scheduler.Schedule(new GatedJob(entered, release, finished),
            JobResourceAccess.Read(resource, 0, 2));
        entered.Wait();
        JobResourceAccess[] accesses = Enumerable.Range(0, 8).Select(index => index % 2 == 0
            ? JobResourceAccess.Read(resource, 0, 2) : JobResourceAccess.Write(resource, 4, 2)).ToArray();

        JobHandle mixed = scheduler.Schedule(new SignalJob(shared), accesses);
        scheduler.Schedule(new SignalJob(release)).Complete();
        scheduler.CombineDependencies([reader, mixed]).Complete();

        events.AssertBefore(shared, finished);
    }

    private readonly record struct GatedJob(JobSignal Entered, JobSignal Release, JobSignal Finished) : IJob
    {
        public void Execute()
        {
            Entered.Set();
            Release.Wait();
            Finished.Set();
        }
    }

    private readonly record struct SignalJob(JobSignal Signal) : IJob
    {
        public void Execute() => Signal.Set();
    }
}
