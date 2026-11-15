using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

internal static class SavedChanges
{
    internal static void WriteBaseline(ref DataWriter writer, int count, bool written, bool added, ulong version)
    {
        writer.WriteByte((byte)((written ? 1 : 0) | (added ? 2 : 0)));
        if (!written) return;
        for (int row = 0; row < count; row++)
        {
            if (added) writer.WriteUInt64(version);
            writer.WriteUInt64(version);
        }
    }
    internal static void Write(ref DataWriter writer, in ComponentChanges changes, int count, bool written, bool added)
    {
        writer.WriteByte((byte)((written ? 1 : 0) | (added ? 2 : 0)));
        if (!written) return;
        if (BitConverter.IsLittleEndian)
        {
            if (added) writer.WriteRawBytes(MemoryMarshal.AsBytes(((RowVersions[])changes.Rows).AsSpan(0, count)));
            else writer.WriteRawBytes(MemoryMarshal.AsBytes(((ulong[])changes.Rows).AsSpan(0, count)));
            return;
        }
        if (added)
        {
            var rows = (RowVersions[])changes.Rows;
            for (int row = 0; row < count; row++) { writer.WriteUInt64(rows[row].Added); writer.WriteUInt64(rows[row].Written); }
        }
        else foreach (ulong version in ((ulong[])changes.Rows).AsSpan(0, count)) writer.WriteUInt64(version);
    }

    internal static void Read(ref DataReader reader, ref ComponentChanges target, int count, bool written, bool added,
        bool preserve, ulong baseline)
    {
        byte flags = reader.ReadByte();
        if (flags != ((written ? 1 : 0) | (added ? 2 : 0)))
        {
            // Saved versions only matter when progress is kept. Otherwise every row starts at the baseline, so a
            // component that started or stopped tracking changes since the save still loads.
            if (preserve || flags > 3) throw new InvalidDataException("Component tracking differs from its registered schema.");
            if ((flags & 1) != 0) reader.Skip((long)count * ((flags & 2) != 0 ? 16 : 8));
            if (written) Baseline(ref target, count, added, baseline);
            return;
        }
        if (!written) return;
        Array.Clear(target.Words);
        Array.Clear(target.Blocks);
        target.LastWrite = 0;
        for (int row = 0; row < count; row++)
        {
            ulong addedVersion = added ? reader.ReadUInt64() : 0;
            ulong writtenVersion = reader.ReadUInt64();
            if (added) Unsafe.As<RowVersions[]>(target.Rows)[row] = new()
            { Added = preserve ? addedVersion : baseline, Written = preserve ? writtenVersion : baseline };
            else Unsafe.As<ulong[]>(target.Rows)[row] = preserve ? writtenVersion : baseline;
            target.Copy(row, ref target, row, added);
        }
    }

    private static void Baseline(ref ComponentChanges target, int count, bool added, ulong baseline)
    {
        Array.Clear(target.Words);
        Array.Clear(target.Blocks);
        target.LastWrite = 0;
        for (int row = 0; row < count; row++)
        {
            if (added) Unsafe.As<RowVersions[]>(target.Rows)[row] = new() { Added = baseline, Written = baseline };
            else Unsafe.As<ulong[]>(target.Rows)[row] = baseline;
            target.Copy(row, ref target, row, added);
        }
    }
}
