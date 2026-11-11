using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace SomeEngine.ECS.Storage;

internal struct RowVersions
{
    internal ulong Added, Written;
}

internal struct ChangeSummary
{
    internal ulong Latest, OtherLatest, Members;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Raise(ulong version, ulong member)
    {
        if (version == Latest) Members |= member;
        else if (version > Latest)
        {
            OtherLatest = Latest;
            Latest = version;
            Members = member;
        }
        else OtherLatest = Math.Max(OtherLatest, version);
    }
}

internal struct ComponentChanges
{
    internal Array Rows;
    internal ChangeSummary[] Words;
    internal ChangeSummary[] Blocks;
    internal ulong LastWrite;
    internal readonly ulong WrittenAt(int row, bool tracksAdded)
        => tracksAdded ? Unsafe.As<RowVersions[]>(Rows)[row].Written : Unsafe.As<ulong[]>(Rows)[row];

    internal ComponentChanges(int capacity, bool added)
    {
        Rows = added ? new RowVersions[capacity] : new ulong[capacity];
        Words = new ChangeSummary[(capacity + 63) >> 6];
        Blocks = new ChangeSummary[(capacity + 4095) >> 12];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Stamp(int row, ulong version, bool added, bool tracksAdded)
    {
        if (tracksAdded)
        {
            ref RowVersions values = ref Unsafe.As<RowVersions[]>(Rows)[row];
            values.Written = version;
            if (added) values.Added = version;
        }
        else Unsafe.As<ulong[]>(Rows)[row] = version;
        ref ChangeSummary word = ref Words[row >> 6];
        ulong bit = 1UL << (row & 63);
        if (word.Latest == version) word.Members |= bit;
        else
        {
            word.OtherLatest = word.Latest;
            word.Latest = version;
            word.Members = bit;
            Blocks[row >> 12].Raise(version, 1UL << ((row >> 6) & 63));
            LastWrite = version;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void StampWord(int row, ulong version, bool tracksAdded)
    {
        if (tracksAdded) Unsafe.As<RowVersions[]>(Rows)[row].Written = version;
        else Unsafe.As<ulong[]>(Rows)[row] = version;
        ref ChangeSummary word = ref Words[row >> 6];
        ulong bit = 1UL << (row & 63);
        if (word.Latest == version) word.Members |= bit;
        else
        {
            word.OtherLatest = word.Latest;
            word.Latest = version;
            word.Members = bit;
        }
    }

    // All rows are marked before the user callback. Parallel ranges publish only
    // per-word summaries; their existing join still owns Blocks and LastWrite.
    internal void StampRange(int start, int count, ulong version, bool tracksAdded, bool publish)
    {
        if (count == 0) return;
        int end = start + count;
        if (tracksAdded)
        {
            RowVersions[] rows = Unsafe.As<RowVersions[]>(Rows);
            for (int row = start; row < end; row++) rows[row].Written = version;
        }
        else Unsafe.As<ulong[]>(Rows).AsSpan(start, count).Fill(version);

        int firstWord = start >> 6, lastWord = (end - 1) >> 6;
        for (int index = firstWord; index <= lastWord; index++)
        {
            int firstBit = index == firstWord ? start & 63 : 0;
            int endBit = index == lastWord ? ((end - 1) & 63) + 1 : 64;
            ulong mask = (ulong.MaxValue << firstBit)
                & (endBit == 64 ? ulong.MaxValue : (1UL << endBit) - 1);
            ref ChangeSummary word = ref Words[index];
            if (word.Latest == version) word.Members |= mask;
            else
            {
                word.OtherLatest = word.Latest;
                word.Latest = version;
                word.Members = mask;
                if (publish)
                {
                    Blocks[index >> 6].Raise(version, 1UL << (index & 63));
                    LastWrite = version;
                }
            }
        }
    }

    internal void MergeWords(int firstWord, int endWord, ulong version)
    {
        bool changed = false;
        for (int word = firstWord; word < endWord; word++)
            if (Words[word].Latest == version)
            {
                Blocks[word >> 6].Raise(version, 1UL << (word & 63));
                changed = true;
            }
        if (changed) LastWrite = version;
    }

    internal void Copy(int targetRow, ref ComponentChanges source, int sourceRow, bool tracksAdded)
    {
        ulong written;
        if (tracksAdded)
        {
            RowVersions value = Unsafe.As<RowVersions[]>(source.Rows)[sourceRow];
            Unsafe.As<RowVersions[]>(Rows)[targetRow] = value;
            written = value.Written;
        }
        else
        {
            written = Unsafe.As<ulong[]>(source.Rows)[sourceRow];
            Unsafe.As<ulong[]>(Rows)[targetRow] = written;
        }
        ref ChangeSummary word = ref Words[targetRow >> 6];
        ulong bit = 1UL << (targetRow & 63);
        word.Members &= ~bit;
        word.Raise(written, bit);
        Blocks[targetRow >> 12].Raise(word.Latest, 1UL << ((targetRow >> 6) & 63));
        LastWrite = Math.Max(LastWrite, written);
    }

    internal void ClearRow(int row, bool tracksAdded)
    {
        if (tracksAdded) Unsafe.As<RowVersions[]>(Rows)[row] = default;
        else Unsafe.As<ulong[]>(Rows)[row] = 0;
        Words[row >> 6].Members &= ~(1UL << (row & 63));
    }

    internal void ClearTail(int start, int end)
    {
        Array.Clear(Rows, start, end - start);
        int word = start >> 6;
        if ((start & 63) != 0)
        {
            Words[word].Members &= (1UL << (start & 63)) - 1;
            word++;
        }
        int endWord = (end + 63) >> 6;
        for (; word < endWord; word++) Words[word].Members = 0;
    }

    internal void Grow(int capacity, bool tracksAdded)
    {
        if (tracksAdded)
        {
            var rows = Unsafe.As<RowVersions[]>(Rows);
            Array.Resize(ref rows, capacity);
            Rows = rows;
        }
        else
        {
            var rows = Unsafe.As<ulong[]>(Rows);
            Array.Resize(ref rows, capacity);
            Rows = rows;
        }
        Array.Resize(ref Words, (capacity + 63) >> 6);
        Array.Resize(ref Blocks, (capacity + 4095) >> 12);
    }

    internal readonly ComponentChanges Copy(int count, bool added)
    {
        var copy = new ComponentChanges(count, added);
        Array.Copy(Rows, copy.Rows, count);
        Words.AsSpan(0, copy.Words.Length).CopyTo(copy.Words);
        Blocks.AsSpan(0, copy.Blocks.Length).CopyTo(copy.Blocks);
        copy.LastWrite = LastWrite;
        return copy;
    }

    internal readonly void CopyTo(ref ComponentChanges target, int count)
    {
        Array.Copy(Rows, target.Rows, count);
        Words.AsSpan(0, (count + 63) >> 6).CopyTo(target.Words);
        Blocks.AsSpan(0, (count + 4095) >> 12).CopyTo(target.Blocks);
        target.LastWrite = LastWrite;
    }

    // The sparse store supplies a currently live row; preserve Select's summary
    // gates but inspect only that row rather than rebuilding its entire word mask.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Contains(int row, ulong since, bool added, bool tracksAdded)
    {
        if (LastWrite <= since) return false;
        int wordIndex = row >> 6;
        ref ChangeSummary block = ref Blocks[wordIndex >> 6];
        if (block.Latest <= since || (block.OtherLatest <= since && (block.Members & (1UL << (wordIndex & 63))) == 0))
            return false;
        ref ChangeSummary word = ref Words[wordIndex];
        if (word.Latest <= since) return false;
        if (word.OtherLatest <= since)
        {
            if ((word.Members & (1UL << (row & 63))) == 0) return false;
            if (!added) return true;
        }
        ulong version = tracksAdded
            ? (added ? Unsafe.As<RowVersions[]>(Rows)[row].Added : Unsafe.As<RowVersions[]>(Rows)[row].Written)
            : Unsafe.As<ulong[]>(Rows)[row];
        return version > since;
    }

    internal ulong Select(int wordIndex, int count, ulong since, bool added, bool tracksAdded)
    {
        if (LastWrite <= since) return 0;
        ref ChangeSummary block = ref Blocks[wordIndex >> 6];
        if (block.Latest <= since || (block.OtherLatest <= since && (block.Members & (1UL << (wordIndex & 63))) == 0)) return 0;
        ref ChangeSummary word = ref Words[wordIndex];
        if (word.Latest <= since) return 0;
        ulong selected = word.OtherLatest <= since ? word.Members : ulong.MaxValue;
        int start = wordIndex << 6;
        int length = Math.Min(64, count - start);
        if (length < 64) selected &= (1UL << length) - 1;
        if (word.OtherLatest <= since && !added) return selected;
        ulong result = 0;
        if (!tracksAdded && !added && length == 64 && selected == ulong.MaxValue && Vector256.IsHardwareAccelerated)
        {
            Vector256<ulong> sinceVector = Vector256.Create(since);
            ref ulong firstVersion = ref Unsafe.As<ulong[]>(Rows)[start];
            for (int offset = 0; offset < 64; offset += Vector256<ulong>.Count)
            {
                Vector256<ulong> versions = Vector256.LoadUnsafe(ref firstVersion, (nuint)offset);
                result |= (ulong)Vector256.ExtractMostSignificantBits(Vector256.GreaterThan(versions, sinceVector)) << offset;
            }
            return result;
        }
        while (selected != 0)
        {
            int bit = BitOperations.TrailingZeroCount(selected);
            int row = start + bit;
            ulong version = tracksAdded
                ? (added ? Unsafe.As<RowVersions[]>(Rows)[row].Added : Unsafe.As<RowVersions[]>(Rows)[row].Written)
                : Unsafe.As<ulong[]>(Rows)[row];
            if (version > since) result |= 1UL << bit;
            selected &= selected - 1;
        }
        return result;
    }
}
