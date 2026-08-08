using System.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

internal static class RuntimeWait
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);
    private static int s_admittedCpuIntervalActive;
    private static long s_admittedCpuTaskWaitCalls;

    internal static T Task<T>(Task<T> task, NativeWindow window, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(window);
        ValidateTimeout(timeout);
        if (Volatile.Read(ref s_admittedCpuIntervalActive) != 0)
            Interlocked.Increment(ref s_admittedCpuTaskWaitCalls);
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Pump(window);
            if (HasExpired(elapsed, timeout))
                throw new TimeoutException("The runtime operation did not complete before its timeout.");
        }
        return task.GetAwaiter().GetResult();
    }

    internal static void BeginAdmittedCpuInterval()
    {
        if (Volatile.Read(ref s_admittedCpuIntervalActive) != 0)
            throw new InvalidOperationException("A Runtime wait interval is already active.");
        Interlocked.Exchange(ref s_admittedCpuTaskWaitCalls, 0);
        Volatile.Write(ref s_admittedCpuIntervalActive, 1);
    }

    internal static long EndAdmittedCpuInterval()
    {
        if (Interlocked.Exchange(ref s_admittedCpuIntervalActive, 0) == 0)
            throw new InvalidOperationException("No Runtime wait interval is active.");
        return Interlocked.Read(ref s_admittedCpuTaskWaitCalls);
    }

    internal static void Position(
        IGraphicsBackend backend,
        in QueueCompletion position,
        NativeWindow window,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(window);
        ValidateTimeout(timeout);
        var elapsed = Stopwatch.StartNew();
        while (!backend.IsComplete(position))
        {
            Pump(window);
            if (HasExpired(elapsed, timeout))
                throw new TimeoutException("GPU work did not complete before the runtime timeout.");
        }
    }

    private static void Pump(NativeWindow window)
    {
        _ = window.PumpMessages();
        window.WaitForEvents(PollInterval);
    }

    private static bool HasExpired(Stopwatch elapsed, TimeSpan timeout) =>
        timeout != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= timeout;

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }
}
