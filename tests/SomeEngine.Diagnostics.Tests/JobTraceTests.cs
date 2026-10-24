using SomeEngine.Job;
using SomeEngine.Trace;

namespace SomeEngine.Diagnostics.Tests;

public sealed class JobTraceTests
{
    private static readonly TraceSite Work = new("test", "ScheduledWork");

    [Fact]
    public void DeferredJobKeepsItsOriginWhileNestedInAnotherFramesWait()
    {
        string path = Path.Combine(Path.GetTempPath(), $"someengine-job-{Guid.NewGuid():N}.setrace");
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        try
        {
            using (EngineTrace.Start(path))
            {
                JobHandle handle;
                using (EngineTrace.BeginFrame(1)) handle = JobSystem.Schedule(new TracedJob());
                using (EngineTrace.BeginFrame(2)) handle.Complete();
            }
            TraceAnalysis analysis = TraceAnalyzer.Analyze(TraceFile.Read(path));
            Assert.True(analysis.Complete, string.Join(", ", analysis.Issues));
            TraceSpan work = Assert.Single(analysis.Spans, s => s.Site.Name == "ScheduledWork");
            TraceSpan execute = analysis.Spans.Single(s => s.Id == work.Parent);
            Assert.Equal(1, work.Frame);
            Assert.Equal("Execute", execute.Site.Name);
            Assert.NotEqual(0, execute.Related);
            Assert.True(analysis.Frames.Single(f => f.Id == 1).OutsideFrameMilliseconds > 0);
        }
        finally { JobSystem.Shutdown(); File.Delete(path); }
    }

    private struct TracedJob : IJob
    {
        public void Execute()
        {
            using var trace = EngineTrace.Begin(Work);
            Thread.SpinWait(1000);
        }
    }
}
