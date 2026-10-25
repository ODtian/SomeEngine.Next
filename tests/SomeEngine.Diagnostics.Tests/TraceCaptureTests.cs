using SomeEngine.Diagnostics;
using SomeEngine.Trace;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SomeEngine.Diagnostics.Tests;

public sealed class TraceCaptureTests : IDisposable
{
    private static readonly TraceSite Work = new("test", "Work");
    private static readonly TraceSite Detail = new("native", "Command", detail: true);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"someengine-{Guid.NewGuid():N}.setrace");
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void DisabledScopesLeaveContextEmpty()
    {
        using (EngineTrace.Begin(Work)) { }
        Assert.Equal(default, EngineTrace.Context);
    }

    [Fact, Trait("Category", "Performance")]
    public void DisabledScopesDoNotAllocate()
    {
        using (EngineTrace.Begin(Work)) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) using (EngineTrace.Begin(Work)) { }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void NestedScopesPreserveFramesAndAllocationSamples()
    {
        using (EngineTrace.Start(_path, new TraceOptions { Allocations = true }))
        using (EngineTrace.BeginFrame(17))
        using (EngineTrace.Begin(Work)) GC.KeepAlive(new byte[120]);
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(4, data.Events.Length);
        TraceEvent frame = data.Events[0].Event, child = data.Events[1].Event, end = data.Events[2].Event;
        Assert.Equal(frame.Id, child.Parent);
        Assert.Equal(17, child.Frame);
        Assert.Equal(child.Id, end.Id);
        Assert.True(end.Value - child.Value >= 120);
        Assert.True(end.Timestamp >= child.Timestamp);
        Assert.Equal(0, data.Misuse);
    }

    [Fact]
    public void DetailFilteringDoesNotProduceOrphanEnds()
    {
        using (EngineTrace.Start(_path))
        using (EngineTrace.BeginFrame(1))
        using (EngineTrace.Begin(Detail)) { }
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(2, data.Events.Length);
        Assert.DoesNotContain(data.Events, e => e.Event.Site == Detail.Id);
    }

    [Fact]
    public void CrossThreadWorkKeepsOriginAndRestoresWorkerContext()
    {
        bool restored = false;
        using (EngineTrace.Start(_path))
        using (EngineTrace.BeginFrame(9))
        {
            TraceContext context = EngineTrace.Context;
            var thread = new Thread(() =>
            {
                using (EngineTrace.Associate(context))
                using (EngineTrace.Begin(Work)) { }
                restored = EngineTrace.Context == default;
            });
            thread.Start(); thread.Join();
        }
        TraceData data = TraceFile.Read(_path);
        ThreadEvent frame = Assert.Single(data.Events, e => e.Event.Kind == TraceEventKind.Frame);
        ThreadEvent child = Assert.Single(data.Events, e => e.Event.Kind == TraceEventKind.Begin);
        Assert.Equal(frame.Event.Id, child.Event.Related);
        Assert.Equal(0, child.Event.Parent);
        Assert.Equal(9, child.Event.Frame);
        Assert.NotEqual(frame.Thread, child.Thread);
        Assert.True(restored);
        Assert.Equal(0, data.Misuse);
    }


    [Fact]
    public void AssociationDisposalOnAnotherThreadPreservesCallingThreadContext()
    {
        bool callingThreadContextPreserved = false;
        using (EngineTrace.Start(_path, new TraceOptions { Allocations = true }))
        using (EngineTrace.BeginFrame(9))
        {
            TraceContext frameContext = EngineTrace.Context;
            var association = EngineTrace.Associate(default);
            var thread = new Thread(() =>
            {
                using (EngineTrace.BeginFrame(17))
                {
                    TraceContext callingThreadContext = EngineTrace.Context;
                    association.Dispose();
                    callingThreadContextPreserved = EngineTrace.Context == callingThreadContext;
                }
            });
            thread.Start(); thread.Join();
            Assert.True(callingThreadContextPreserved);
            Assert.Equal(default, EngineTrace.Context);
            association.Dispose();
            Assert.Equal(frameContext, EngineTrace.Context);
        }
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(1, data.Misuse);
        Assert.Equal(4, data.Events.Length);
        Assert.Equal(
            data.Events.Where(e => e.Event.Kind == TraceEventKind.Frame).Select(e => e.Event.Id).Order(),
            data.Events.Where(e => e.Event.Kind == TraceEventKind.End).Select(e => e.Event.Id).Order());
    }

    [Fact]
    public void AssociationDisposalOutOfOrderPreservesNestedContext()
    {
        using (EngineTrace.Start(_path, new TraceOptions { Allocations = true }))
        using (EngineTrace.BeginFrame(9))
        {
            TraceContext frameContext = EngineTrace.Context;
            var association = EngineTrace.Associate(default);
            using (EngineTrace.Begin(Work))
            {
                TraceContext nestedContext = EngineTrace.Context;
                association.Dispose();
                Assert.Equal(nestedContext, EngineTrace.Context);
            }
            Assert.Equal(default, EngineTrace.Context);
            association.Dispose();
            Assert.Equal(frameContext, EngineTrace.Context);
        }
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(1, data.Misuse);
        Assert.Equal(4, data.Events.Length);
        Assert.Equal(TraceEventKind.Frame, data.Events[0].Event.Kind);
        Assert.Equal(TraceEventKind.Begin, data.Events[1].Event.Kind);
        Assert.Equal(TraceEventKind.End, data.Events[2].Event.Kind);
        Assert.Equal(data.Events[1].Event.Id, data.Events[2].Event.Id);
        Assert.Equal(TraceEventKind.End, data.Events[3].Event.Kind);
        Assert.Equal(data.Events[0].Event.Id, data.Events[3].Event.Id);
    }

    [Fact]
    public void AssociationDisposalAfterCaptureStopsDoesNotChangeNewCaptureContext()
    {
        string secondPath = _path + ".next";
        try
        {
            using TraceCapture firstCapture = EngineTrace.Start(_path, new TraceOptions { Allocations = true });
            var firstFrame = EngineTrace.BeginFrame(9);
            var association = EngineTrace.Associate(default);
            var recordedScope = EngineTrace.Begin(Work);
            firstCapture.Dispose();
            Assert.Equal(default, EngineTrace.Context);
            using (EngineTrace.Start(secondPath, new TraceOptions { Allocations = true }))
            using (EngineTrace.BeginFrame(17))
            {
                TraceContext frameContext = EngineTrace.Context;
                recordedScope.Dispose();
                Assert.Equal(frameContext, EngineTrace.Context);
                association.Dispose();
                Assert.Equal(frameContext, EngineTrace.Context);
                firstFrame.Dispose();
                Assert.Equal(frameContext, EngineTrace.Context);
                using (EngineTrace.Begin(Work)) { }
                Assert.Equal(frameContext, EngineTrace.Context);
            }
            TraceData firstData = TraceFile.Read(_path);
            Assert.Equal(2, firstData.Events.Length);
            Assert.Equal(TraceEventKind.Frame, firstData.Events[0].Event.Kind);
            Assert.Equal(TraceEventKind.Begin, firstData.Events[1].Event.Kind);
            Assert.Equal(firstData.Events[0].Event.Id, firstData.Events[1].Event.Parent);
            Assert.Equal(0, firstData.Misuse);
            TraceData secondData = TraceFile.Read(secondPath);
            Assert.Equal(0, secondData.Misuse);
            Assert.Equal(4, secondData.Events.Length);
            Assert.Equal(TraceEventKind.Frame, secondData.Events[0].Event.Kind);
            Assert.Equal(TraceEventKind.Begin, secondData.Events[1].Event.Kind);
            Assert.Equal(TraceEventKind.End, secondData.Events[2].Event.Kind);
            Assert.Equal(secondData.Events[1].Event.Id, secondData.Events[2].Event.Id);
            Assert.Equal(TraceEventKind.End, secondData.Events[3].Event.Kind);
            Assert.Equal(secondData.Events[0].Event.Id, secondData.Events[3].Event.Id);
            Assert.Equal(secondData.Events[0].Event.Id, secondData.Events[1].Event.Parent);
            Assert.Equal(17, secondData.Events[1].Event.Frame);
        }
        finally { File.Delete(secondPath); }
    }

    [Fact, Trait("Category", "Performance")]
    public void EnabledScopesDoNotAllocateInsideAnExistingBlock()
    {
        long bytes;
        using (EngineTrace.Start(_path))
        {
            using (EngineTrace.Begin(Work)) { }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) using (EngineTrace.Begin(Work)) { }
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void RepeatedDisposalPreservesThreadNamesAndEvents()
    {
        var options = new TraceOptions { BufferMegabytes = 1 };
        using TraceCapture capture = EngineTrace.Start(_path, options);
        EngineTrace.NameThread("capture-thread");
        EngineTrace.Counter(Work, 7);
        int currentThread = Environment.CurrentManagedThreadId;
        int workerThread = 0;
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                workerThread = Environment.CurrentManagedThreadId;
                EngineTrace.NameThread("worker-thread");
                EngineTrace.Counter(Work, 11);
            }
            catch (Exception error) { workerFailure = error; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(workerFailure);
        capture.Dispose();

        byte[] completed = File.ReadAllBytes(_path);
        capture.Dispose();
        EngineTrace.NameThread("after-stop");

        Assert.False(EngineTrace.IsEnabled);
        Assert.Equal(default, EngineTrace.Context);
        Assert.Same(options, capture.Options);
        Assert.Equal(2, capture.WrittenEvents);
        Assert.Equal(completed, File.ReadAllBytes(_path));
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(2, data.Threads.Length);
        Assert.Equal("capture-thread", Assert.Single(data.Threads, t => t.Id == currentThread).Name);
        Assert.Equal("worker-thread", Assert.Single(data.Threads, t => t.Id == workerThread).Name);
        Assert.Equal(2, data.Events.Length);
        Assert.Equal(7, Assert.Single(data.Events, e => e.Thread == currentThread).Event.Value);
        Assert.Equal(11, Assert.Single(data.Events, e => e.Thread == workerThread).Event.Value);
        Assert.All(data.Events, e => Assert.Equal(TraceEventKind.Counter, e.Event.Kind));
        Assert.All(data.Threads, t => Assert.Equal(0, t.Dropped));
        Assert.Equal(0, data.Misuse);
    }

    [Fact]
    public void FullBlocksAndFinalPartialBlocksAreWrittenOnce()
    {
        using (EngineTrace.Start(_path))
            for (int i = 0; i < 15000; i++) using (EngineTrace.Begin(Work)) { }
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(30000, data.Events.Length);
        Assert.Equal(15000, data.Events.Count(e => e.Event.Kind == TraceEventKind.Begin));
        Assert.Equal(15000, data.Events.Where(e => e.Event.Kind == TraceEventKind.Begin).Select(e => e.Event.Id).Distinct().Count());
        Assert.All(data.Threads, t => Assert.Equal(0, t.Dropped));
    }

    [Fact]
    public void BufferExhaustionIsExplicitAndDoesNotBlockProducers()
    {
        using (EngineTrace.Start(_path, new TraceOptions { BufferMegabytes = 1 }))
        {
            // Each short-lived thread holds one partial block until capture stops.
            for (int i = 0; i < 12; i++)
            {
                var thread = new Thread(() => EngineTrace.Counter(Work, 1));
                thread.Start(); thread.Join();
            }
        }
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(12, data.Events.Length + data.Threads.Sum(t => t.Dropped));
        Assert.True(data.Threads.Sum(t => t.Dropped) > 0);
    }

    [Fact]
    public void CaptureCanStopWhileAProducerIsRunning()
    {
        using TraceCapture capture = EngineTrace.Start(_path);
        using var started = new ManualResetEventSlim();
        bool done = false;
        var thread = new Thread(() =>
        {
            started.Set();
            while (!Volatile.Read(ref done)) EngineTrace.Counter(Work, 7);
        });
        thread.Start(); started.Wait(); capture.Dispose();
        Volatile.Write(ref done, true); thread.Join();
        TraceData data = TraceFile.Read(_path);
        Assert.All(data.Events, e => Assert.Equal(7, e.Event.Value));
    }

    [Fact]
    public void CopiedScopeDisposalIsReportedWithoutCorruptingNesting()
    {
        using (EngineTrace.Start(_path))
        {
            var scope = EngineTrace.Begin(Work);
            scope.Dispose(); scope.Dispose();
        }
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(1, data.Misuse);
        Assert.Equal(2, data.Events.Length);
    }

    [Fact]
    public void UnregisteredThreadCanReadContextWhileCapturesStartAndStop()
    {
        using var started = new ManualResetEventSlim();
        bool done = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            started.Set();
            try
            {
                while (!Volatile.Read(ref done))
                    if (EngineTrace.Context != default)
                        throw new InvalidOperationException("An unregistered thread has no trace context.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.Start(); started.Wait();
        try
        {
            for (int i = 0; i < 64; i++)
            {
                using (EngineTrace.Start(_path, new TraceOptions { BufferMegabytes = 1 })) Thread.Yield();
                File.Delete(_path);
            }
        }
        finally { Volatile.Write(ref done, true); thread.Join(); }
        Assert.Null(failure);
    }

    [Fact]
    public void FrameOnlyCaptureRetainsFrameTimingAndInputCounters()
    {
        using (EngineTrace.Start(_path, new TraceOptions { Scopes = false }))
        using (EngineTrace.BeginFrame(1))
        using (EngineTrace.Begin(Work)) EngineTrace.Counter(Work, 42);
        TraceData data = TraceFile.Read(_path);
        Assert.Equal(3, data.Events.Length);
        Assert.Equal(1, Assert.Single(data.Events, e => e.Event.Kind == TraceEventKind.Counter).Event.Frame);
        Assert.DoesNotContain(data.Events, e => e.Event.Kind == TraceEventKind.Begin);
    }

    [Fact]
    public void TruncatedFilesAreRejected()
    {
        using (EngineTrace.Start(_path)) using (EngineTrace.Begin(Work)) { }
        using (FileStream file = File.OpenWrite(_path)) file.SetLength(file.Length - 8);
        Assert.Throws<EndOfStreamException>(() => TraceFile.Read(_path));
    }
}
