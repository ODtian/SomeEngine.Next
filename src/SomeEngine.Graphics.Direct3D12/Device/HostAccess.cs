using Vortice.Direct3D12;
using D3D12Range = Vortice.Direct3D12.Range;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    public unsafe BufferMapping MapBuffer(BufferHandle buffer, BufferMapMode mode, in BufferRange range)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return MapBufferCore(GetBuffer(buffer), mode, range);
    }

    private unsafe BufferMapping MapBufferCore(NativeBuffer native, BufferMapMode mode, in BufferRange range)
    {
        ValidateBufferMapping(native, mode, range, out ulong offset, out ulong size);
        return MapValidatedBuffer(native, mode, offset, size);
    }

    private void ValidateBufferMapping(
        NativeBuffer native,
        BufferMapMode mode,
        in BufferRange range,
        out ulong offset,
        out ulong size)
    {
        if (mode == BufferMapMode.Write && native.MemoryType != MemoryType.Upload)
            throw new InvalidOperationException("Write mappings require upload memory.");
        if (mode == BufferMapMode.Read && native.MemoryType != MemoryType.Readback)
            throw new InvalidOperationException("Read mappings require readback memory.");
        if (!native.HasCompletedLastUse(_native))
            throw new InvalidOperationException("A buffer cannot be mapped before its exact queue uses have completed.");
        ResolveBufferRange(native.Desc, range, out offset, out size);
        if (size > int.MaxValue) throw new NotSupportedException("A managed mapping lease cannot exceed Int32.MaxValue bytes.");
        if (!native.TryBeginMapping()) throw new InvalidOperationException("A buffer permits only one active mapping lease.");
    }

    private unsafe BufferMapping MapValidatedBuffer(
        NativeBuffer native,
        BufferMapMode mode,
        ulong offset,
        ulong size)
    {
        void* pointer = null;
        try
        {
            D3D12Range readRange = mode == BufferMapMode.Read
                ? new D3D12Range(new UIntPtr(offset), new UIntPtr(checked(offset + size)))
                : new D3D12Range(UIntPtr.Zero, UIntPtr.Zero);
            native.Resource.Map(0, readRange, &pointer).CheckError();
            NativeBufferMappingOwner owner = new(
                native,
                checked((int)size),
                mode,
                offset);
            return new BufferMapping(
                new Span<byte>((byte*)pointer + checked((nint)offset), checked((int)size)),
                owner,
                mode,
                new BufferRange(offset, size));
        }
        catch
        {
            native.EndMapping();
            throw;
        }
    }

    public bool WaitIdle(TimeSpan timeout)
    {
        ThrowIfDisposed();
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        GpuCompletion[] snapshot = Compilation.Queues
            .Select(queue =>
            {
                NativeQueue native = _native.GetQueue(queue);
                lock (native.SubmissionGate)
                    return new GpuCompletion(_domain, queue, native.SubmittedValue);
            })
            .Where(static completion => completion.Value != 0)
            .ToArray();
        long started = timeout == Timeout.InfiniteTimeSpan ? 0 : Environment.TickCount64;
        foreach (GpuCompletion completion in snapshot)
        {
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? timeout
                : timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (timeout != Timeout.InfiniteTimeSpan && remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (!Wait(completion, remaining)) return false;
        }
        return true;
    }

    private sealed class NativeBufferMappingOwner : IBufferMappingOwner
    {
        private NativeBuffer? _buffer;
        private readonly int _length;
        private readonly BufferMapMode _mode;
        private readonly ulong _offset;

        public NativeBufferMappingOwner(
            NativeBuffer buffer,
            int length,
            BufferMapMode mode,
            ulong offset)
        {
            _buffer = buffer;
            _length = length;
            _mode = mode;
            _offset = offset;
        }

        public bool IsDisposed => Volatile.Read(ref _buffer) is null;

        public void Dispose()
        {
            NativeBuffer? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is null) return;
            D3D12Range written = _mode == BufferMapMode.Write
                ? new D3D12Range(new UIntPtr(_offset), new UIntPtr(checked(_offset + (ulong)_length)))
                : new D3D12Range(UIntPtr.Zero, UIntPtr.Zero);
            buffer.Resource.Unmap(0, written);
            buffer.EndMapping();
        }
    }
}
