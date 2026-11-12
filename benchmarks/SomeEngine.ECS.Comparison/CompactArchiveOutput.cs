using System.Buffers;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Comparison;

internal sealed class CompactArchiveOutput : IBufferWriter<byte>, IDisposable
{
    private const int SegmentBytes = 64 * 1024;
    private struct Segment(byte[] bytes)
    {
        internal byte[]? Bytes = bytes;
        internal int Written = 0;
    }

    internal readonly record struct Costs(long SegmentCapacityBytes, int SegmentCount,
        long ByteArrayAllocationBytes, int ByteArrayAllocationCount,
        long DirectoryArrayAllocationBytes, int DirectoryArrayAllocationCount,
        long MergeCopyBytes, int Capacity, long DirectoryCapacityBytes);

    private Segment[] _segments = [];
    private byte[] _result = [];
    private int _segmentCount;
    private long _segmentCapacityBytes, _byteArrayAllocationBytes, _directoryAllocationBytes, _mergeCopyBytes;
    private int _segmentsInPass, _byteArraysAllocated, _directoryArraysAllocated;
    internal int WrittenCount { get; private set; }
    internal int Capacity => _result.Length;
    internal ReadOnlySpan<byte> WrittenSpan => _result.AsSpan(0, WrittenCount);
    internal Costs Statistics => new(_segmentCapacityBytes, _segmentsInPass, _byteArrayAllocationBytes, _byteArraysAllocated,
        _directoryAllocationBytes, _directoryArraysAllocated, _mergeCopyBytes, Capacity,
        (long)_segments.Length * Unsafe.SizeOf<Segment>());

    internal void Clear()
    {
        _result.AsSpan().Clear();
        Array.Clear(_segments, 0, _segmentCount);
        _segmentCount = WrittenCount = 0;
        _segmentCapacityBytes = _byteArrayAllocationBytes = _directoryAllocationBytes = _mergeCopyBytes = 0;
        _segmentsInPass = _byteArraysAllocated = _directoryArraysAllocated = 0;
        if (_result.Length != 0) AddSegment(_result);
    }

    public void Advance(int count)
    {
        _segments[_segmentCount - 1].Written += count;
        WrittenCount += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        sizeHint = Math.Max(1, sizeHint);
        if (_segmentCount == 0 || sizeHint > _segments[_segmentCount - 1].Bytes!.Length - _segments[_segmentCount - 1].Written)
        {
            int capacity = Math.Max(SegmentBytes, sizeHint);
            AddSegment(AllocateBytes(capacity));
        }
        ref Segment segment = ref _segments[_segmentCount - 1];
        return segment.Bytes!.AsMemory(segment.Written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    private byte[] AllocateBytes(int capacity)
    {
        _byteArrayAllocationBytes += capacity;
        _byteArraysAllocated++;
        return new byte[capacity];
    }

    private void AddSegment(byte[] bytes)
    {
        if (_segmentCount == _segments.Length)
        {
            int capacity = Math.Max(4, _segments.Length * 2);
            Array.Resize(ref _segments, capacity);
            _directoryAllocationBytes += (long)capacity * Unsafe.SizeOf<Segment>();
            _directoryArraysAllocated++;
        }
        _segments[_segmentCount++] = new(bytes);
        _segmentCapacityBytes += bytes.Length;
        _segmentsInPass++;
    }

    internal void Complete()
    {
        byte[] result = _result.Length == WrittenCount ? _result : AllocateBytes(WrittenCount);
        int offset = 0;
        for (int i = 0; i < _segmentCount; i++)
        {
            Segment segment = _segments[i];
            if (!ReferenceEquals(segment.Bytes, result) || offset != 0)
            {
                segment.Bytes!.AsSpan(0, segment.Written).CopyTo(result.AsSpan(offset));
                _mergeCopyBytes += segment.Written;
            }
            offset += segment.Written;
        }
        _result = result;
        Array.Clear(_segments, 0, _segmentCount);
        _segmentCount = 0;
    }

    public void Dispose()
    {
        _segments = [];
        _result = [];
        _segmentCount = WrittenCount = 0;
    }
}
