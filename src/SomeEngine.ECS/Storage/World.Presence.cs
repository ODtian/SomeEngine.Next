using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong[] EnsurePresence(Chunk chunk, int type)
        => chunk.Columns[type].Presence ?? CreatePresence(chunk, type);

    private ulong[] CreatePresence(Chunk chunk, int type)
    {
        var words = new ulong[(chunk.Capacity + 63) >> 6];
        words.AsSpan(0, chunk.Count >> 6).Fill(ulong.MaxValue);
        if ((chunk.Count & 63) != 0) words[chunk.Count >> 6] = (1UL << (chunk.Count & 63)) - 1;
        chunk.Columns[type].Presence = words;
        if ((uint)type < (uint)_podBuffers.Length) _podBuffers[type]?.Clear();
        chunk.Archetype.Optional.Add(type);
        chunk.Archetype.TransferKinds |= 4;
        QueryVersion++;
        RefreshQueryMatches();
        return words;
    }

    private void TransferPresence(Chunk source, int sourceRow, Chunk target, int targetRow, int last)
    {
        target.InitializePresence(targetRow);
        TransferEnabled(source, sourceRow, target, targetRow, last);
        foreach (int type in source.Archetype.Types.Types)
        {
            if (source.Columns[type].Presence is not { } words) continue;
            bool present = (words[sourceRow >> 6] & (1UL << (sourceRow & 63))) != 0;
            if (!present && (uint)type < (uint)target.Columns.Length && target.Columns[type].ColumnPlusOne != 0)
                EnsurePresence(target, type)[targetRow >> 6] &= ~(1UL << (targetRow & 63));
            ulong moved = (words[last >> 6] >> (last & 63)) & 1;
            words[sourceRow >> 6] = (words[sourceRow >> 6] & ~(1UL << (sourceRow & 63))) | (moved << (sourceRow & 63));
            words[last >> 6] &= ~(1UL << (last & 63));
        }
    }
}
