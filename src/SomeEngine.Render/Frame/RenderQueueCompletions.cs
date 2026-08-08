using SomeEngine.Graphics;

namespace SomeEngine.Render.Frame;

internal static class RenderQueueCompletions
{
    internal static QueueCompletion[] Merge(
        ReadOnlySpan<QueueCompletion> left,
        ReadOnlySpan<QueueCompletion> right)
    {
        if (left.IsEmpty && right.IsEmpty)
            return [];

        QueueCompletion[] merged = new QueueCompletion[checked(left.Length + right.Length)];
        int count = 0;
        Add(left);
        Add(right);
        return merged.AsSpan(0, count).ToArray();

        void Add(ReadOnlySpan<QueueCompletion> values)
        {
            foreach (ref readonly QueueCompletion value in values)
            {
                Queue queue = value.Queue;
                int existing = -1;
                for (int index = 0; index < count; index++)
                {
                    if (ReferenceEquals(merged[index].Queue, queue))
                    {
                        existing = index;
                        break;
                    }
                }

                if (existing < 0)
                    merged[count++] = value;
                else if (value.Value > merged[existing].Value)
                    merged[existing] = value;
            }
        }
    }

    internal static bool WaitAll(
        IGraphicsBackend backend,
        ReadOnlySpan<QueueCompletion> completions,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        if (timeout == TimeSpan.Zero)
        {
            foreach (ref readonly QueueCompletion completion in completions)
            {
                if (!backend.IsComplete(completion))
                    return false;
            }
            return true;
        }

        long started = Environment.TickCount64;
        foreach (ref readonly QueueCompletion completion in completions)
        {
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : Remaining(timeout, started);
            if (backend.WaitCpu(completion, remaining) != WaitStatus.Completed)
                return false;
        }
        return true;
    }

    private static TimeSpan Remaining(TimeSpan timeout, long started)
    {
        TimeSpan remaining = timeout -
            TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
