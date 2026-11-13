namespace SomeEngine.Graphics;

internal static class NativeMemoryChecks
{
    internal static int SpanLength(ulong size)
    {
        if (size > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(size));
        return (int)size;
    }

    internal static void Range(ulong capacity, ulong offset, ulong size)
    {
        if (offset > capacity || size > capacity - offset)
            throw new ArgumentOutOfRangeException(nameof(size), "The native memory range exceeds its allocation.");
    }
}
