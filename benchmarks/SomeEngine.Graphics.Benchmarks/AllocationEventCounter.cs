using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace SomeEngine.Graphics.Benchmarks;

internal sealed class AllocationEventCounter : EventListener
{
    private readonly long _recordingThreadId = GetCurrentThreadId();
    private long _count;

    internal long Count => Interlocked.Read(ref _count);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (string.Equals(eventSource.Name, "Microsoft-Windows-DotNETRuntime", StringComparison.Ordinal))
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.OSThreadId == _recordingThreadId &&
            eventData.EventName?.Contains("GCAllocationTick", StringComparison.Ordinal) == true)
        {
            Interlocked.Increment(ref _count);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
