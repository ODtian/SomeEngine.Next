using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

internal readonly record struct QueryMatch(Archetype Archetype, QueryRowFilter? Filter);

internal sealed class QueryRowFilter(int[] required, int[] excluded, int[] any, int[] changed, QueryDescription description)
{
    internal static QueryRowFilter? Create(Archetype archetype, QueryDescription description)
    {
        if ((archetype.TransferKinds & 4) == 0 && !archetype.Types.Bits.Intersects(description.Disabled)
            && !archetype.Types.Bits.Intersects(description.Changed) && description.Shared.Length == 0) return null;
        int[] required = archetype.Types.Types.Where(t => (archetype.Optional.Has(t) || description.Disabled.Has(t)) && description.Required.Has(t)).ToArray();
        int[] excluded = archetype.Types.Types.Where(t => archetype.Optional.Has(t) && description.Excluded.Has(t)).ToArray();
        int[] any = description.Any.Count != 0 && !archetype.Types.Bits.IntersectsExcept(description.Any, archetype.Optional)
            ? archetype.Types.Types.Where(description.Any.Has).ToArray() : [];
        int[] changed = archetype.Types.Types.Where(description.Changed.Has).ToArray();
        return required.Length + excluded.Length + any.Length + changed.Length + description.Shared.Length == 0
            ? null : new(required, excluded, any, changed, description);
    }

    private ulong BaseMask(Chunk chunk, int word, ulong since)
    {
        ulong mask = ulong.MaxValue;
        foreach (int type in required)
        {
            if (chunk.Columns[type].Presence is { } words) mask &= words[word];
            if (description.Stored.Has(type)) continue;
            ulong active = chunk.Enabled?[chunk.Columns[type].ColumnPlusOne - 1] is { } enabled ? enabled[word] : ulong.MaxValue;
            mask &= description.Disabled.Has(type) ? ~active : active;
        }
        foreach (int type in excluded)
        {
            ulong active = chunk.Columns[type].Presence is { } words ? words[word] : ulong.MaxValue;
            if (chunk.Enabled?[chunk.Columns[type].ColumnPlusOne - 1] is { } enabled) active &= enabled[word];
            mask &= ~active;
        }
        if (any.Length != 0)
        {
            ulong matching = 0;
            foreach (int type in any)
            {
                ulong active = chunk.Columns[type].Presence is { } words ? words[word] : ulong.MaxValue;
                if (chunk.Enabled?[chunk.Columns[type].ColumnPlusOne - 1] is { } enabled) active &= enabled[word];
                matching |= active;
            }
            mask &= matching;
        }
        foreach (int type in changed)
        {
            int column = chunk.Columns[type].ColumnPlusOne - 1;
            mask &= chunk.Changes![column].Select(word, chunk.Count, since,
                description.Added.Has(type), chunk.Archetype.Components[column].TracksAdded);
        }
        return mask;
    }

    private ulong Mask(Chunk chunk, int word, ulong since)
    {
        ulong mask = BaseMask(chunk, word, since);
        foreach (var condition in description.Shared)
        {
            int[] values = Unsafe.As<int[]>(chunk.Columns[condition.Type].Values!);
            int start = word << 6, end = Math.Min(start + 64, chunk.Count);
            ulong matching = 0;
            int row = start;
            if (Vector256.IsHardwareAccelerated && end - start >= Vector256<int>.Count)
            {
                Vector256<int> expected = Vector256.Create(condition.Value);
                ref int first = ref values[start];
                for (; row <= end - Vector256<int>.Count; row += Vector256<int>.Count)
                {
                    Vector256<int> current = Vector256.LoadUnsafe(ref first, (nuint)(row - start));
                    matching |= (ulong)Vector256.ExtractMostSignificantBits(Vector256.Equals(current, expected)) << (row - start);
                }
            }
            for (; row < end; row++)
                if (values[row] == condition.Value) matching |= 1UL << (row - start);
            mask &= matching;
        }
        return mask;
    }

    internal bool Contains(Chunk chunk, int row, ulong since)
    {
        if ((BaseMask(chunk, row >> 6, since) & (1UL << (row & 63))) == 0) return false;
        foreach (var condition in description.Shared)
            if (Unsafe.As<int[]>(chunk.Columns[condition.Type].Values!)[row] != condition.Value) return false;
        return true;
    }

    internal ulong SelectWord(Chunk chunk, int word, ulong since) => Mask(chunk, word, since);

    internal int Count(Chunk chunk, ulong since)
    {
        int whole = WholeRange(chunk);
        if (whole != 0) return whole > 0 ? chunk.Count : 0;
        int count = 0;
        int fullWords = chunk.Count >> 6;
        for (int word = 0; word < fullWords; word++) count += BitOperations.PopCount(Mask(chunk, word, since));
        int remainder = chunk.Count & 63;
        if (remainder != 0) count += BitOperations.PopCount(Mask(chunk, fullWords, since) & ((1UL << remainder) - 1));
        return count;
    }

    internal bool Any(Chunk chunk, ulong since)
    {
        int whole = WholeRange(chunk);
        if (whole != 0) return whole > 0 && chunk.Count != 0;
        int fullWords = chunk.Count >> 6;
        for (int word = 0; word < fullWords; word++) if (Mask(chunk, word, since) != 0) return true;
        int remainder = chunk.Count & 63;
        return remainder != 0 && (Mask(chunk, fullWords, since) & ((1UL << remainder) - 1)) != 0;
    }

    internal bool Next(Chunk chunk, ulong since, ref int next, out int start, out int count, int end = int.MaxValue)
    {
        end = Math.Min(end, chunk.Count);
        if (next == 0)
        {
            int whole = WholeRange(chunk);
            if (whole != 0)
            {
                start = 0;
                count = whole > 0 ? end : 0;
                next = end;
                return count != 0;
            }
        }
        while (next < end)
        {
            int word = next >> 6;
            int offset = next & 63;
            ulong mask = Mask(chunk, word, since) & (ulong.MaxValue << offset);
            if (mask == 0) { next = (word + 1) << 6; continue; }
            offset = BitOperations.TrailingZeroCount(mask);
            start = (word << 6) + offset;
            if (start >= end) break;
            int length = BitOperations.TrailingZeroCount(~(mask >> offset));
            length = Math.Min(length, 64 - offset);
            int stop = start + length;
            while (stop < end && (stop & 63) == 0)
            {
                ulong following = Mask(chunk, stop >> 6, since);
                int prefix = BitOperations.TrailingZeroCount(~following);
                stop += prefix;
                if (prefix != 64) break;
            }
            next = Math.Min(stop, end);
            count = next - start;
            return true;
        }
        start = count = 0;
        next = end;
        return false;
    }

    private int WholeRange(Chunk chunk)
    {
        if (any.Length != 0 || changed.Length != 0 || description.Shared.Length != 0) return 0;
        bool all = true;
        foreach (int type in required)
        {
            if (chunk.Columns[type].Presence is not null) { all = false; continue; }
            if (description.Stored.Has(type)) continue;
            int disabledCount = chunk.DisabledCounts?[chunk.Columns[type].ColumnPlusOne - 1] ?? 0;
            int matching = description.Disabled.Has(type) ? disabledCount : chunk.Count - disabledCount;
            if (matching == 0) return -1;
            if (matching != chunk.Count) all = false;
        }
        foreach (int type in excluded)
        {
            if (chunk.Columns[type].Presence is not null) { all = false; continue; }
            int disabledCount = chunk.DisabledCounts?[chunk.Columns[type].ColumnPlusOne - 1] ?? 0;
            if (disabledCount == 0) return -1;
            if (disabledCount != chunk.Count) all = false;
        }
        return all ? 1 : 0;
    }
}
