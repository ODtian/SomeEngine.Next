namespace SomeEngine.Job.Tests;

public sealed class EventLogTests
{
    [Fact]
    public void AnEmptyCapturedReadAllowsTheFirstObservedAppend()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var log = new EventLog<int>(scheduler, initialCapacity: 1);
        using EventReader<int> reader = log.Watch();
        EventReadRange empty = reader.Capture();
        Assert.Equal(0UL, empty.First);
        Assert.Equal(0UL, empty.Through);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var written = new ManualResetEventSlim();
        JobHandle reading = scheduler.Schedule(new HoldEmptyRead(reader, empty, entered, release), empty.Read, dependency: empty.Ready);
        JobHandle published = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            EventWriteRange next = log.ReserveRange(1);
            JobHandle writing = scheduler.Schedule(new FillEvents(log, next, [47], written), next.Write, dependency: next.Ready);
            published = log.Publish(next, writing);
            Assert.True(written.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            reading.Complete();
            published.Complete();
            using EventRead<int> read = reader.Open(reader.Capture());
            Assert.Equal([47], read.First.ToArray());
        }
        finally { release.Set(); reading.Complete(); published.Complete(); }
    }

    [Fact]
    public void AReaderStartsWithEventsPublishedAfterItsRegistration()
    {
        using var log = new EventLog<int>();
        Assert.False(log.HasReaders);
        using (EventWriter<int> ignored = log.Reserve(2))
        {
            Assert.True(ignored.Values.IsEmpty);
            log.Publish(ignored.Range);
        }
        using EventReader<int> reader = log.Watch();
        Append(log, 7, 11);
        using EventRead<int> read = reader.Open();
        Assert.Equal([7, 11], read.First.ToArray());
        Assert.Equal(2ul, read.Through);
    }

    [Fact]
    public void ReadersKeepTheirIndependentProgressThroughArrayGrowth()
    {
        using var log = new EventLog<int>(initialCapacity: 2);
        using EventReader<int> first = log.Watch();
        using EventReader<int> second = log.Watch();
        Append(log, 1, 2);
        EventReadRange earlier = first.Capture();
        first.Advance(earlier.Through);
        Append(log, 3, 4, 5);
        using EventRead<int> firstRead = first.Open();
        using EventRead<int> secondRead = second.Open();
        Assert.Equal([3, 4, 5], firstRead.First.ToArray());
        Assert.Equal([1, 2, 3, 4, 5], secondRead.First.ToArray());
        Assert.Equal(2ul, first.Position);
        Assert.Equal(0ul, second.Position);
    }

    [Fact]
    public void AdvancingAnEarlierReadKeepsLaterPublishedEvents()
    {
        using var log = new EventLog<int>();
        using EventReader<int> reader = log.Watch();
        Append(log, 1, 2);
        EventReadRange earlier = reader.Capture();
        Append(log, 3);
        using (EventRead<int> read = reader.Open(earlier))
        {
            Assert.Equal([1, 2], read.First.ToArray());
            Assert.Equal(earlier.Through, read.Through);
        }
        reader.Advance(earlier.Through);
        using EventRead<int> later = reader.Open();
        Assert.Equal([3], later.First.ToArray());
    }

    [Fact]
    public void ReaderConvenienceMembersUseOneCapturedPublishedInterval()
    {
        using var log = new EventLog<int>();
        using EventReader<int> reader = log.Watch();
        Append(log, 13);
        JobHandle ready = reader.Ready;
        JobResourceAccess access = reader.Read;
        Append(log, 17);
        ready.Complete();
        using (EventRead<int> read = reader.Open())
        {
            Assert.Equal([13], read.First.ToArray());
            Assert.Equal(1ul, read.Through);
        }
        EventReadRange refreshed = reader.Capture();
        using EventRead<int> current = reader.Open(refreshed);
        Assert.Equal([13, 17], current.First.ToArray());
    }

    [Fact]
    public void NamedReadersContinueWithTheirRetainedEventsAfterDataRestore()
    {
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        using var source = new EventLog<int>();
        using EventReader<int> first = source.Watch(firstId);
        using EventReader<int> second = source.Watch(secondId);
        Append(source, 2, 3, 5);
        first.Advance(1);
        EventLogData<int> data = source.CaptureData();
        using var restored = new EventLog<int>();
        restored.RestoreData(data);
        using EventReader<int> resumedFirst = restored.Watch(firstId);
        using EventReader<int> resumedSecond = restored.Watch(secondId);
        Assert.Same(resumedFirst, restored.Watch(firstId));
        using EventRead<int> firstRead = resumedFirst.Open();
        using EventRead<int> secondRead = resumedSecond.Open();
        Assert.Equal([3, 5], firstRead.First.ToArray());
        Assert.Equal([2, 3, 5], secondRead.First.ToArray());
        Assert.Equal(source.Through, restored.Through);
    }

    [Fact]
    public void CapturedDataKeepsOnlyTheUnconsumedSuffix()
    {
        using var log = new EventLog<int>();
        using EventReader<int> reader = log.Watch(Guid.NewGuid());
        Append(log, 2, 3, 5);
        reader.Advance(2);
        EventLogData<int> data = log.CaptureData();
        Assert.Equal([5], data.Values.AsSpan(0, data.Count).ToArray());
        Assert.Equal(3ul, data.Through);
        Assert.Equal(2ul, data.Readers[0].Position);
    }

    [Fact]
    public void TheNextStepWritesWhileAnEarlierPublishedIntervalIsBeingRead()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var log = new EventLog<int>(scheduler, initialCapacity: 8);
        using EventReader<int> reader = log.Watch();
        Append(log, 11);
        EventReadRange earlier = reader.Capture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var written = new ManualResetEventSlim();
        int[] observed = new int[2];
        JobHandle reading = scheduler.Schedule(new HoldRead(reader, earlier, observed, entered, release),
            earlier.Read, dependency: earlier.Ready);
        JobHandle published = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            EventWriteRange next = log.ReserveRange(1);
            next.Ready.Complete();
            JobHandle writing = scheduler.Schedule(new FillEvents(log, next, [23], written), next.Write,
                dependency: next.Ready);
            published = log.Publish(next, writing);
            Assert.True(written.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(11, observed[0]);
            release.Set();
            published.Complete();
            reading.Complete();
            Assert.Equal([11, 11], observed);
            reader.Advance(earlier.Through);
            using EventRead<int> current = reader.Open();
            Assert.Equal([23], current.First.ToArray());
        }
        finally { release.Set(); reading.Complete(); published.Complete(); }
    }

    [Fact]
    public void StorageCompactionPreservesTheOldReadAndTheRetainedSuffix()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var log = new EventLog<int>(scheduler, initialCapacity: 4);
        using EventReader<int> reader = log.Watch();
        Append(log, 1, 2, 3, 4);
        reader.Advance(2);
        EventReadRange earlier = reader.Capture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int[] observed = new int[2];
        JobHandle reading = scheduler.Schedule(new HoldRead(reader, earlier, observed, entered, release), earlier.Read);
        JobHandle published = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            EventWriteRange next = log.ReserveRange(2);
            JobHandle writing = scheduler.Schedule(new FillEvents(log, next, [5, 6]), next.Write, dependency: next.Ready);
            published = log.Publish(next, writing);
            release.Set();
            published.Complete();
            reading.Complete();
            Assert.Equal([3, 3], observed);
            using EventRead<int> current = reader.Open(reader.Capture());
            Assert.Equal([3, 4, 5, 6], current.First.ToArray());
        }
        finally { release.Set(); reading.Complete(); published.Complete(); }
    }

    [Fact]
    public void CountAndPrefixOrderedWritesProduceTheSameEventSequence()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var first = new EventLog<int>(scheduler);
        using var second = new EventLog<int>(scheduler);
        using EventReader<int> firstReader = first.Watch();
        using EventReader<int> secondReader = second.Watch();
        foreach (EventLog<int> log in new[] { first, second })
        {
            EventWriteRange range = log.ReserveRange(6);
            JobHandle writing = scheduler.ScheduleParallel(new FillPrefixes(log, range, [2, 1, 3], [0, 2, 3]),
                3, 1, [range.Write], dependency: range.Ready);
            log.Publish(range, writing).Complete();
        }
        using EventRead<int> firstRead = firstReader.Open();
        using EventRead<int> secondRead = secondReader.Open();
        Assert.Equal([0, 1, 10, 20, 21, 22], firstRead.First.ToArray());
        Assert.Equal(firstRead.First.ToArray(), secondRead.First.ToArray());
    }

    [Fact]
    public void StepAccessCarriesTheProducerCompletionAndItsReadDeclarations()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 0 });
        int[] values = [0];
        JobResourceAccess write = JobResourceAccess.Write(scheduler, values);
        JobResourceAccess read = JobResourceAccess.Read(scheduler, values);
        JobHandle produced = scheduler.Schedule(new SetValue(values), write);
        var access = new StepAccess(produced, [read]);
        int[] result = [0];
        scheduler.Schedule(new ReadValue(values, result), access.Reads, dependency: access.Completed).Complete();
        Assert.Equal(37, result[0]);
    }

    [Fact]
    public void CommittedPrefixEventsCompleteSeparatelyFromAFailedProducerCall()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var log = new EventLog<int>(scheduler);
        using EventReader<int> reader = log.Watch();
        JobHandle failed = scheduler.Schedule(new CommitPrefixAndFail(log), log.Write);
        Assert.Throws<InvalidOperationException>(failed.Complete);
        EventReadRange range = reader.Capture();
        range.Ready.Complete();
        int[] result = [0];
        scheduler.Schedule(new CopyEvent(reader, range, result), range.Read, dependency: range.Ready).Complete();
        Assert.Equal(73, result[0]);
        Assert.Equal(1ul, range.Through);
    }

#if DEBUG
    [Fact]
    public void OpeningWithoutItsDeclaredReadTriggersTheDevelopmentCheck()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 0 });
        using var log = new EventLog<int>(scheduler);
        using EventReader<int> reader = log.Watch();
        Append(log, 1);
        EventReadRange range = reader.Capture();
        JobHandle job = scheduler.Schedule(new OpenUndeclared(reader, range));
        Assert.Throws<InvalidOperationException>(job.Complete);
    }

    [Fact]
    public void OpeningADisposedReaderTriggersTheDevelopmentCheck()
    {
        using var log = new EventLog<int>();
        EventReader<int> reader = log.Watch();
        reader.Dispose();
        Assert.Throws<InvalidOperationException>(() => { using EventRead<int> read = reader.Open(); });
    }

    [Fact]
    public void AReaderUnadvancedForTheConfiguredStepsTriggersTheDevelopmentCheck()
    {
        using var log = new EventLog<int>(stalledStepLimit: 2);
        using EventReader<int> reader = log.Watch();
        Append(log, 1);
        log.EndStep();
        Append(log, 2);
        Append(log, 3);
        Assert.Throws<InvalidOperationException>(log.EndStep);
        using EventRead<int> read = reader.Open();
        Assert.Equal([1, 2, 3], read.First.ToArray());
    }

    [Fact]
    public void ManyPublishesWithinOneStepDoNotReportAReaderThatAdvancesEachStep()
    {
        using var log = new EventLog<int>(stalledStepLimit: 2);
        using EventReader<int> reader = log.Watch();
        for (int step = 0; step < 4; step++)
        {
            for (int value = 0; value < 200; value++) Append(log, value);
            ulong through;
            using (EventRead<int> read = reader.Open()) through = read.Through;
            reader.Advance(through);
            log.EndStep();
        }
    }
#endif

    private static void Append(EventLog<int> log, params int[] values)
    {
        using EventWriter<int> writer = log.Reserve(values.Length);
        values.CopyTo(writer.Values);
        log.Publish(writer.Range).Complete();
    }

    private readonly struct HoldEmptyRead(EventReader<int> reader, EventReadRange range,
        ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            using EventRead<int> read = reader.Open(range);
            Assert.Equal(0, read.Count);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            Assert.Equal(0, read.Count);
        }
    }

    private readonly struct HoldRead(EventReader<int> reader, EventReadRange range, int[] observed,
        ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            using EventRead<int> read = reader.Open(range);
            observed[0] = read.First[0];
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            observed[1] = read.First[0];
        }
    }

    private readonly struct FillEvents(EventLog<int> log, EventWriteRange range, int[] values,
        ManualResetEventSlim? written = null) : IJob
    {
        public void Execute()
        {
            using EventWriter<int> write = log.Open(range);
            values.CopyTo(write.Values);
            written?.Set();
        }
    }

    private readonly struct FillPrefixes(EventLog<int> log, EventWriteRange range, int[] counts, int[] offsets) : IJobParallelFor
    {
        public void Execute(int index)
        {
            using EventWriter<int> write = log.Open(range);
            for (int item = 0; item < counts[index]; item++) write.Values[offsets[index] + item] = index * 10 + item;
        }
    }

    private readonly struct SetValue(int[] values) : IJob { public void Execute() => values[0] = 37; }
    private readonly struct ReadValue(int[] values, int[] result) : IJob { public void Execute() => result[0] = values[0]; }
    private readonly struct CommitPrefixAndFail(EventLog<int> log) : IJob
    {
        public void Execute()
        {
            using EventWriter<int> write = log.Reserve(1);
            write.Values[0] = 73;
            log.Publish(write.Range);
            throw new InvalidOperationException();
        }
    }
    private readonly struct CopyEvent(EventReader<int> reader, EventReadRange range, int[] result) : IJob
    {
        public void Execute() { using EventRead<int> read = reader.Open(range); result[0] = read.First[0]; }
    }
#if DEBUG
    private readonly struct OpenUndeclared(EventReader<int> reader, EventReadRange range) : IJob
    { public void Execute() { using EventRead<int> read = reader.Open(range); } }
#endif
}
