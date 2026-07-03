namespace SomeEngine.ECS.Collections;

internal static class SmallListExtensions
{
    public static int IndexOf<T>(this ref SmallList<T> list, T item)
        where T : IEquatable<T>
    {
        return list.AsSpan().IndexOf(item);
    }

    public static bool RemoveStable<T>(this ref SmallList<T> list, T item)
        where T : IEquatable<T>
    {
        int index = list.IndexOf(item);
        if (index < 0)
            return false;

        list.RemoveAt(index);
        return true;
    }

    public static bool RemoveSwapBack<T>(this ref SmallList<T> list, T item)
        where T : IEquatable<T>
    {
        int index = list.IndexOf(item);
        if (index < 0)
            return false;

        list.SwapRemoveAt(index);
        return true;
    }

    public static void SwapRemoveAt<T>(this ref SmallList<T> list, int index)
    {
        if ((uint)index >= (uint)list.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        int lastIndex = list.Count - 1;
        if (index != lastIndex)
            list[index] = list[lastIndex];

        list.RemoveAt(lastIndex);
    }
}

