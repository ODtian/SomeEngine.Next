using SomeEngine.Trace;

namespace SomeEngine.Diagnostics.Tests;

public sealed class TraceAnalysisTests
{
    [Fact]
    public void NestingIsExclusiveAndWorkersAreNotAddedToFrameLatency()
    {
        TraceAnalysis result = TraceAnalyzer.Analyze(Data([
            Begin(1, 0, 0, 1, 1, 100, kind: TraceEventKind.Frame),
            Begin(2, 1, 0, 1, 1, 120), Begin(3, 2, 0, 1, 1, 125), End(3, 1, 135), End(2, 1, 160),
            Begin(4, 0, 2, 1, 2, 170), End(1, 1, 200), End(4, 2, 220)]));
        Assert.True(result.Complete, string.Join(", ", result.Issues));
        TraceFrame frame = Assert.Single(result.Frames);
        Assert.Equal(100, frame.Milliseconds);
        Assert.Equal(100, frame.MainThreadMilliseconds);
        Assert.Equal(50, frame.WorkerThreadMilliseconds);
        Assert.Equal(120, frame.WorkExtentMilliseconds);
        Assert.Equal(20, frame.OutsideFrameMilliseconds);
        Assert.Equal(30, result.Spans.Single(s => s.Id == 2).SelfTicks);
    }

    [Fact]
    public void CausalAssociationDoesNotChangeExecutionNesting()
    {
        TraceAnalysis result = TraceAnalyzer.Analyze(Data([
            Begin(1, 0, 0, 1, 1, 100, kind: TraceEventKind.Frame),
            Begin(2, 1, 0, 1, 1, 110), Begin(3, 2, 1, 2, 1, 120), End(3, 1, 150),
            End(2, 1, 180), End(1, 1, 200),
            Begin(4, 0, 0, 2, 1, 210, kind: TraceEventKind.Frame), End(4, 1, 230)]));
        Assert.True(result.Complete, string.Join(", ", result.Issues));
        Assert.Equal(40, result.Spans.Single(s => s.Id == 2).SelfTicks);
        Assert.Equal(30, result.Frames.Single(f => f.Id == 2).OutsideFrameMilliseconds);
        Assert.Equal(50, result.Frames.Single(f => f.Id == 2).MainThreadMilliseconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingEventsInvalidateAccounting(bool missingBegin)
    {
        TraceAnalysis result = TraceAnalyzer.Analyze(Data(missingBegin ? [End(1, 1, 200)] : [Begin(1, 0, 0, 0, 1, 100)]));
        Assert.False(result.Complete);
        Assert.True(result.IssueCount > 0);
    }

    [Fact]
    public void OverlappingSiblingsAreRejected()
    {
        TraceAnalysis result = TraceAnalyzer.Analyze(Data([
            Begin(1, 0, 0, 0, 1, 100), Begin(2, 1, 0, 0, 1, 110), End(2, 1, 140),
            Begin(3, 1, 0, 0, 1, 130), End(3, 1, 150), End(1, 1, 200)]));
        Assert.False(result.Complete);
        Assert.Contains(result.Issues, i => i.Contains("Overlapping"));
    }

    [Fact]
    public void SubTickSiblingsDoNotOverlapANonemptyIntervalAtTheSameTick()
    {
        TraceAnalysis result = TraceAnalyzer.Analyze(Data([
            Begin(1, 0, 0, 0, 1, 100), Begin(2, 1, 0, 0, 1, 110), End(2, 1, 110),
            Begin(3, 1, 0, 0, 1, 110), End(3, 1, 111), End(1, 1, 120)]));
        Assert.True(result.Complete);
        Assert.Equal(19, result.Spans.Single(s => s.Id == 1).SelfTicks);
    }

    [Fact]
    public void WarmupDoesNotHideCorruptCaptureData()
    {
        TraceAnalysis result = TraceAnalyzer.Analyze(Data([
            Begin(1, 0, 0, 1, 1, 100, kind: TraceEventKind.Frame), End(1, 1, 200),
            Begin(2, 0, 0, 2, 1, 210, kind: TraceEventKind.Frame), End(2, 1, 220), End(99, 1, 230)]), 1);
        Assert.False(result.Complete);
        Assert.Equal(2, Assert.Single(result.Frames).Id);
    }

    private static ThreadEvent Begin(long id, long parent, long related, long frame, int thread, long timestamp,
        TraceEventKind kind = TraceEventKind.Begin) => new(thread, new(timestamp, id, parent, related, frame, 0, 1, kind));
    private static ThreadEvent End(long id, int thread, long timestamp) => new(thread, new(timestamp, id, 0, 0, 0, 0, 0, TraceEventKind.End));
    private static TraceData Data(ThreadEvent[] events) => new(1000, 0, 300, 0, 1, false, false, [],
        [new TraceSiteInfo(1, "test", "Work", false)], [new TraceThreadInfo(1, 101, "main", 0), new TraceThreadInfo(2, 102, "worker", 0)], events, 0);
}
