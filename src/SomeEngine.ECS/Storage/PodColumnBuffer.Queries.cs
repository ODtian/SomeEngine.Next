using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Storage;

internal sealed unsafe partial class PodColumnBuffer
{
    internal void ForEach<T, TAction>(ref TAction action)
        where T : struct where TAction : struct, IComponentAction<T>
    {
        nint rows = _rows;
        foreach (ref readonly RowRange range in CollectionsMarshal.AsSpan(_ranges))
        {
            int count = range.Chunk.Count;
            if (count == 0) continue;
            action.Execute(MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T>((void*)(rows + (nint)range.Start * Unsafe.SizeOf<T>())), count));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ForEach<T1, T2, TAction>(PodColumnBuffer second, ref TAction action)
        where T1 : struct where T2 : struct where TAction : struct, IComponentAction<T1, T2>
    {
        nint firstRows = _rows, secondRows = second._rows;
        foreach (ref readonly RowRange range in CollectionsMarshal.AsSpan(_ranges))
        {
            int count = range.Chunk.Count;
            if (count == 0) continue;
            nint start = range.Start;
            action.Execute(
                MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T1>((void*)(firstRows + start * Unsafe.SizeOf<T1>())), count),
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef<T2>((void*)(secondRows + start * Unsafe.SizeOf<T2>())), count));
        }
    }

    internal void ForEach<T1, T2, T3, TAction>(PodColumnBuffer second, PodColumnBuffer third, ref TAction action)
        where T1 : struct where T2 : struct where T3 : struct where TAction : struct, IComponentAction<T1, T2, T3>
    {
        nint firstRows = _rows, secondRows = second._rows, thirdRows = third._rows;
        foreach (ref readonly RowRange range in CollectionsMarshal.AsSpan(_ranges))
        {
            int count = range.Chunk.Count;
            if (count == 0) continue;
            nint start = range.Start;
            action.Execute(
                MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T1>((void*)(firstRows + start * Unsafe.SizeOf<T1>())), count),
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef<T2>((void*)(secondRows + start * Unsafe.SizeOf<T2>())), count),
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef<T3>((void*)(thirdRows + start * Unsafe.SizeOf<T3>())), count));
        }
    }
}
