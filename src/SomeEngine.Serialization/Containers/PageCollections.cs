namespace SomeEngine.Serialization.Containers;

/// <summary>Explicit page operations for collections exposed through ordinary IList properties.</summary>
public static class PageCollections
{
    /// <summary>The callback is synchronous and may run once per page. Its spans cannot escape the callback.</summary>
    public static void Write<T>(this IList<T> values, int index, int count, PageWrite<T> write)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is not PageList<T> pages)
            throw new NotSupportedException("Batch page writes require a paged collection.");
        pages.Write(index, count, write);
    }

    /// <summary>The returned read holds these values across await, later edits, and collection disposal.</summary>
    public static PageRange<T> Retain<T>(this IList<T> values, int index = 0, int? count = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is not PageList<T> pages)
            throw new NotSupportedException("Retained page reads require a paged collection.");
        return pages.Retain(index, count);
    }
}
