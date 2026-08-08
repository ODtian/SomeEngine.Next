using System.Collections;

namespace SomeEngine.Render.Materials;

internal static class BorrowedReadOnlyList
{
    internal static IReadOnlyList<T> Borrow<T>(IList<T>? source)
        => source is null || source.Count == 0
            ? Array.Empty<T>()
            : source as IReadOnlyList<T> ?? new Projection<T, T>(source, static value => value);

    internal static IReadOnlyList<TResult> Project<TSource, TResult>(
        IList<TSource>? source,
        Func<TSource, TResult> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return source is null || source.Count == 0
            ? Array.Empty<TResult>()
            : new Projection<TSource, TResult>(source, projection);
    }

    private sealed class Projection<TSource, TResult>(
        IList<TSource> source,
        Func<TSource, TResult> projection) : IReadOnlyList<TResult>
    {
        public int Count => source.Count;
        public TResult this[int index] => projection(source[index]);

        public IEnumerator<TResult> GetEnumerator()
        {
            for (int index = 0; index < source.Count; index++)
                yield return projection(source[index]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
