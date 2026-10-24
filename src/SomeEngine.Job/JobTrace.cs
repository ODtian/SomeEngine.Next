using SomeEngine.Diagnostics;

namespace SomeEngine.Job;

internal static class JobTrace
{
    internal static readonly TraceSite Execute = new("job", "Execute");
    internal static readonly TraceSite Complete = new("wait", "Job.Complete");
    internal static readonly TraceSite Latency = new("job", "LatencyWork");
    internal static readonly TraceSite JoinLatency = new("wait", "Job.JoinLatencyWork");
}
