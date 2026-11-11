using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Storage;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColumnEntry
{
    internal Array? Values;
    internal ulong[]? Presence;
    internal nint Rows;
    internal int ColumnPlusOne;
    internal int GroupPlusOne;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool Has(int row)
        => ColumnPlusOne != 0 && (Presence is not { } words || (words[row >> 6] & (1UL << (row & 63))) != 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetPresent(int row)
    {
        if (Presence is { } words) words[row >> 6] |= 1UL << (row & 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T At<T>(ref ColumnEntry column, int row) where T : struct
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return ref Unsafe.As<T[]>(column.Values!)[row];
        return ref Unsafe.AsRef<T>((void*)(column.Rows + (nint)row * Unsafe.SizeOf<T>()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Span<T> Span<T>(ref ColumnEntry column, int count) where T : struct
        => MemoryMarshal.CreateSpan(ref At<T>(ref column, 0), count);

}
