namespace SomeEngine.Graphics;

internal static class Timeouts
{
    internal static int ToMilliseconds(TimeSpan timeout, string parameterName)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return Timeout.Infinite;

        double totalMilliseconds = timeout.TotalMilliseconds;
        if (totalMilliseconds < 0 || totalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Timeout must be nonnegative, infinite, or at most Int32.MaxValue milliseconds.");
        }

        return (int)totalMilliseconds;
    }
}
