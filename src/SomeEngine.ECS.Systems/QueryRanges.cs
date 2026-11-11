using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Systems;

internal readonly struct QueryRange(Chunk chunk, QueryRowFilter? filter, int start, int end)
{
    internal readonly Chunk Chunk = chunk;
    internal readonly QueryRowFilter? Filter = filter;
    internal readonly int Start = start;
    internal readonly int End = end;
}

internal sealed class QueryRanges
{
    private static readonly object FreeLock = new();
    private static QueryRanges[]? s_free;
    private static int s_freeCount;

    internal QueryRange[] Items = new QueryRange[16];
    internal int Count;
    private bool _hasFilters;
    internal bool HasFilters => _hasFilters;

    internal static QueryRanges Rent()
    {
        lock (FreeLock)
        {
            if (s_freeCount != 0)
            {
                int index = --s_freeCount;
                QueryRanges ranges = s_free![index];
                s_free[index] = null!;
                return ranges;
            }
        }
        return new QueryRanges();
    }

    internal void Return()
    {
        Array.Clear(Items, 0, Count);
        Count = 0;
        _hasFilters = false;
        lock (FreeLock)
        {
            QueryRanges[] free = s_free ??= new QueryRanges[4];
            if (s_freeCount == free.Length)
            {
                Array.Resize(ref free, free.Length * 2);
                s_free = free;
            }
            free[s_freeCount++] = this;
        }
    }

    internal void Build(Query query, int rangeRows)
    {
        QueryMatch[] matches = query.GetMatches(out int count);
        for (int i = 0; i < count; i++)
        {
            QueryMatch match = matches[i];
            QueryRowFilter? filter = match.Filter;
            _hasFilters |= filter is not null;
            foreach (Chunk chunk in match.Archetype.AllChunks)
            {
                int rows = chunk.Count;
                int needed = Count + (rows + rangeRows - 1) / rangeRows;
                if (needed > Items.Length) Array.Resize(ref Items, Math.Max(needed, Items.Length * 2));
                QueryRange[] items = Items;
                int next = Count;
                if (filter is null)
                {
                    for (int start = 0; start < rows; start += rangeRows)
                        items[next++] = new(chunk, null, start, Math.Min(start + rangeRows, rows));
                }
                else
                    for (int start = 0; start < rows; start += rangeRows)
                        items[next++] = new(chunk, filter, start, Math.Min(start + rangeRows, rows));
                Count = next;
            }
        }
    }

    internal void Select(ulong since)
    {
        if (!_hasFilters) return;
        int selected = 0;
        for (int i = 0; i < Count; i++)
        {
            QueryRange range = Items[i];
            bool any = range.Filter is null;
            if (!any)
                for (int word = range.Start >> 6; word < (range.End + 63) >> 6; word++)
                {
                    ulong mask = range.Filter!.SelectWord(range.Chunk, word, since);
                    int remaining = range.End - (word << 6);
                    if (remaining < 64) mask &= (1UL << remaining) - 1;
                    if (mask != 0) { any = true; break; }
                }
            if (any) Items[selected++] = range;
        }
        Array.Clear(Items, selected, Count - selected);
        Count = selected;
    }

    internal void Merge<T>(ulong version) where T : struct
    {
        int type = ComponentType<T>.TypeId;
        for (int i = 0; i < Count; i++)
        {
            QueryRange range = Items[i];
            Chunk chunk = range.Chunk;
            ref ComponentChanges changes = ref chunk.Changes![chunk.Columns[type].ColumnPlusOne - 1];
            changes.MergeWords(range.Start >> 6, (range.End + 63) >> 6, version);
        }
    }
}
