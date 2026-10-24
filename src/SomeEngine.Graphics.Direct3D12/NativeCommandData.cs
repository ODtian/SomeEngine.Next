using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D12;
using NativeQueryType = Silk.NET.Direct3D12.QueryType;

namespace SomeEngine.Graphics.Direct3D12;

// Stores arguments passed to Silk.NET, including pointed-to arrays. GPU buffer
// contents are deliberately absent: the GPU reads the current contents on submit.
internal sealed unsafe partial class NativeCommandData
{
    private byte[] _bytes = [];
    private int _length;
    internal bool CanRepeat { get; private set; } = true;

    internal ReadOnlySpan<byte> Bytes => _bytes.AsSpan(0, _length);

    internal void Clear()
    {
        _length = 0;
        CanRepeat = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write<T>(T value) where T : unmanaged
    {
        if (typeof(T) == typeof(NativeCommandType) &&
            Unsafe.As<T, NativeCommandType>(ref value) is NativeCommandType.BeginQuery or
                NativeCommandType.ResolveQueryData)
            CanRepeat = false;
        Span<byte> destination = Append(sizeof(T));
        MemoryMarshal.Write(destination, in value);
    }

    internal void WriteEndQuery(nint heap, NativeQueryType type, uint index)
    {
        CanRepeat &= type == NativeQueryType.Timestamp;
        Write(NativeCommandType.EndQuery);
        Write(heap);
        Write(type);
        Write(index);
    }

    internal void WriteArray(void* values, int byteCount)
    {
        Write(byteCount);
        new ReadOnlySpan<byte>(values, byteCount).CopyTo(Append(byteCount));
    }

    private Span<byte> Append(int byteCount)
    {
        int alignedCount = (byteCount + 7) & ~7;
        int end = checked(_length + alignedCount);
        if (end > _bytes.Length)
            Array.Resize(ref _bytes, Math.Max(end, Math.Max(256, _bytes.Length * 2)));
        Span<byte> result = _bytes.AsSpan(_length, alignedCount);
        result.Slice(byteCount).Clear();
        _length = end;
        return result.Slice(0, byteCount);
    }

    private ref struct CommandReader(byte* bytes)
    {
        private byte* _next = bytes;

        internal T Read<T>() where T : unmanaged
        {
            T value = Unsafe.ReadUnaligned<T>(_next);
            _next += (sizeof(T) + 7) & ~7;
            return value;
        }

        internal void* ReadArray()
        {
            int byteCount = Read<int>();
            void* result = byteCount == 0 ? null : _next;
            _next += (byteCount + 7) & ~7;
            return result;
        }

        internal bool Reached(byte* end) => _next == end;
    }

    internal void WriteBarriers(uint groupCount, BarrierGroup* groups)
    {
        Write(NativeCommandType.Barrier);
        Write(groupCount);
        for (uint index = 0; index < groupCount; index++)
        {
            ref BarrierGroup group = ref groups[index];
            Write(group.Type);
            Write(group.NumBarriers);
            int size = group.Type switch
            {
                BarrierType.Global => sizeof(GlobalBarrier),
                BarrierType.Buffer => sizeof(Silk.NET.Direct3D12.BufferBarrier),
                _ => sizeof(Silk.NET.Direct3D12.TextureBarrier),
            };
            WriteArray(group.Anonymous.PGlobalBarriers, checked((int)group.NumBarriers * size));
        }
    }

    private static void ExecuteBarriers(ID3D12GraphicsCommandList10* target, ref CommandReader reader)
    {
        uint groupCount = reader.Read<uint>();
        // The engine emits at most one group for each of the three barrier types.
        BarrierGroup* groups = stackalloc BarrierGroup[checked((int)groupCount)];
        for (uint index = 0; index < groupCount; index++)
        {
            groups[index] = default;
            groups[index].Type = reader.Read<BarrierType>();
            groups[index].NumBarriers = reader.Read<uint>();
            groups[index].Anonymous.PGlobalBarriers = (GlobalBarrier*)reader.ReadArray();
        }
        CommandTransferTrace.Barrier(target, groupCount, groups);
    }
}
