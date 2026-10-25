using System.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

internal static class RuntimeWait
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    internal static void Task(Task task, NativeWindow window, TimeSpan timeout)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ApplicationTrace.TaskCompletion);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(window);
        ValidateTimeout(timeout);
        WaitForTask(task, window, timeout);
        task.GetAwaiter().GetResult();
    }

    internal static T Task<T>(Task<T> task, NativeWindow window, TimeSpan timeout)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ApplicationTrace.TaskCompletion);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(window);
        ValidateTimeout(timeout);
        WaitForTask(task, window, timeout);
        return task.GetAwaiter().GetResult();
    }

    private static void WaitForTask(Task task, NativeWindow window, TimeSpan timeout)
    {
        if (task.IsCompleted) return;
        long started = Stopwatch.GetTimestamp();
        TimeSpan spinDuration = TimeSpan.FromTicks(timeout == Timeout.InfiniteTimeSpan
            ? TimeSpan.TicksPerMillisecond : Math.Min(timeout.Ticks, TimeSpan.TicksPerMillisecond));
        while (!task.IsCompleted && Stopwatch.GetElapsedTime(started) < spinDuration)
            Thread.SpinWait(64);
        if (task.IsCompleted) return;
        WaitHandle completion = ((IAsyncResult)task).AsyncWaitHandle;
        while (!task.IsCompleted)
        {
            _ = window.PumpMessages();
            if (task.IsCompleted) break;
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan : timeout - Stopwatch.GetElapsedTime(started);
            if (timeout != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
                throw new TimeoutException("The runtime operation did not complete before its timeout.");
            window.WaitForEvents(completion, remaining);
        }
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
