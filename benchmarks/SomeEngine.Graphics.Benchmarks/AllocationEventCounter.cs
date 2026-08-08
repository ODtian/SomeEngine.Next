using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace SomeEngine.Graphics.Benchmarks;

internal sealed class AllocationEventCounter : EventListener
{
    private readonly long _recordingThreadId = GetCurrentThreadId();
    private long _count;

    internal long Count => Interlocked.Read(ref _count);

    internal static long AttributeIntervalEvents(
        long managedAllocatedBytes,
        long observedEvents)
    {
        if (managedAllocatedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(managedAllocatedBytes));
        if (observedEvents < 0)
            throw new ArgumentOutOfRangeException(nameof(observedEvents));

        // Runtime allocation ticks may be delivered after the allocation that
        // produced them. The exact per-thread byte counter proves whether the
        // recording thread allocated inside this interval; a tick with a zero
        // byte delta belongs to work outside the measured interval.
        return managedAllocatedBytes == 0 ? 0 : observedEvents;
    }

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
