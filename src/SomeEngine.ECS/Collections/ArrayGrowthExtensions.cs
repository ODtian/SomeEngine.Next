using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.ECS.Collections;

internal static class ArrayGrowthExtensions
{
    public static void EnsureCapacity<T>(
        [NotNull] ref T[]? array,
        int requiredCapacity,
        int minimumCapacity = 4
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredCapacity);

        if (array is not null && requiredCapacity <= array.Length)
            return;

        int currentLength = array?.Length ?? 0;
        Array.Resize(ref array, GetNewCapacity(currentLength, requiredCapacity, minimumCapacity));
    }

    private static int GetNewCapacity(int currentLength, int requiredCapacity, int minimumCapacity)
    {
        int newCapacity = currentLength == 0 ? minimumCapacity : currentLength;
        if (newCapacity < minimumCapacity)
            newCapacity = minimumCapacity;

        while (newCapacity < requiredCapacity)
            newCapacity *= 2;

        return newCapacity;
    }
}

