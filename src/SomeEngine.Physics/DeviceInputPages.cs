using SomeEngine.Graphics;
using System.Runtime.InteropServices;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Mapped transfer pages for the parameter rows that have actually been written.</summary>
internal sealed class DeviceInputPages(IGraphicsBackend backend, Device device, int capacity, int stride, string name) : IDisposable
{
    private readonly int _rowsPerPage = Math.Max(1, 65536 / stride);
    private readonly Page?[] _pages = new Page?[(int)(((long)capacity + Math.Max(1, 65536 / stride) - 1) / Math.Max(1, 65536 / stride))];
    private readonly List<(int Index, Page Page)> _allocatedPages = [];
    private readonly List<Page> _copiedPages = [];
    private Buffer? _zeros;
    internal long Size => (long)capacity * stride;

    internal Span<byte> Row(int row)
    {
        int pageIndex = row / _rowsPerPage;
        Page? page = Volatile.Read(ref _pages[pageIndex]);
        if (page is null)
        {
            lock (_pages)
            {
                page = _pages[pageIndex];
                if (page is null)
                {
                    int count = Math.Min(_rowsPerPage, capacity - pageIndex * _rowsPerPage);
                    Buffer buffer = backend.CreateBuffer(device, new((ulong)(count * stride), BufferUsages.CopySource, name), MemoryType.Upload);
                    MappedBuffer mapping = backend.Map(buffer, MapType.Write, BufferRange.Whole);
                    mapping.Bytes.Clear();
                    page = new(buffer, mapping);
                    _allocatedPages.Add((pageIndex, page));
                    Volatile.Write(ref _pages[pageIndex], page);
                }
            }
        }
        ulong offset = (ulong)(row % _rowsPerPage * stride);
        page.WriteOffset = Math.Min(page.WriteOffset, offset);
        page.WriteEnd = Math.Max(page.WriteEnd, offset + (ulong)stride);
        return page.Mapping.Bytes.Slice((int)offset, stride);
    }

    internal void Clear()
    {
        foreach (var allocated in _allocatedPages)
        {
            Page page = allocated.Page;
            page.Mapping.Bytes.Clear();
            page.WriteOffset = 0;
            page.WriteEnd = page.Buffer.Info.Size;
        }
    }
    internal void CopyWrittenPages(CommandContext commands, Buffer destination)
    {
        foreach ((int index, Page page) in _allocatedPages)
        {
            if (page.WriteEnd == 0) continue;
            ulong size = page.WriteEnd - page.WriteOffset;
            page.Mapping.Flush(new(page.WriteOffset, size));
            backend.CopyBuffer(commands, new(page.Buffer, page.WriteOffset, destination,
                (ulong)index * (ulong)_rowsPerPage * (ulong)stride + page.WriteOffset, size));
            page.WriteOffset = ulong.MaxValue;
            page.WriteEnd = 0;
        }
    }
    internal static void MergeRanges(List<BufferRange> ranges)
    {
        ranges.Sort(static (first, second) => first.Offset.CompareTo(second.Offset));
        int count = 0;
        foreach (BufferRange range in CollectionsMarshal.AsSpan(ranges))
        {
            if (count != 0 && ranges[count - 1].Offset + ranges[count - 1].Size >= range.Offset)
            {
                BufferRange previous = ranges[count - 1];
                ranges[count - 1] = new(previous.Offset, Math.Max(previous.Offset + previous.Size, range.Offset + range.Size) - previous.Offset);
            }
            else ranges[count++] = range;
        }
        if (count < ranges.Count) ranges.RemoveRange(count, ranges.Count - count);
    }
    internal void Copy(CommandContext commands, Buffer destination, ReadOnlySpan<BufferRange> ranges)
    {
        ulong pageBytes = (ulong)_rowsPerPage * (ulong)stride;
        foreach (BufferRange range in ranges)
        {
            if (range.Size == 0) continue;
            int firstPage = (int)(range.Offset / pageBytes), lastPage = (int)((range.Offset + range.Size - 1) / pageBytes);
            if (lastPage - firstPage + 1 > _allocatedPages.Count)
            {
                foreach ((int index, Page page) in _allocatedPages)
                    if (index >= firstPage && index <= lastPage) CopyPage(commands, destination, range, index, page);
            }
            else
                for (int index = firstPage; index <= lastPage; index++)
                    if (_pages[index] is { } page) CopyPage(commands, destination, range, index, page);
        }
        foreach (Page page in _copiedPages)
        {
            page.Mapping.Flush(new(page.FlushOffset, page.FlushEnd - page.FlushOffset));
            page.FlushEnd = 0;
        }
        _copiedPages.Clear();
    }
    private void CopyPage(CommandContext commands, Buffer destination, BufferRange range, int index, Page page)
    {
        ulong start = (ulong)index * (ulong)_rowsPerPage * (ulong)stride;
        ulong first = Math.Max(start, range.Offset), last = Math.Min(start + page.Buffer.Info.Size, range.Offset + range.Size);
        if (page.FlushEnd == 0)
        {
            _copiedPages.Add(page);
            page.FlushOffset = first - start; page.FlushEnd = last - start;
        }
        else
        {
            page.FlushOffset = Math.Min(page.FlushOffset, first - start);
            page.FlushEnd = Math.Max(page.FlushEnd, last - start);
        }
        backend.CopyBuffer(commands, new(page.Buffer, first - start, destination, first, last - first));
    }
    internal void Zero(CommandContext commands, Buffer destination)
    {
        if (_zeros is null)
        {
            _zeros = backend.CreateBuffer(device, new(65536, BufferUsages.CopySource), MemoryType.Upload);
            using MappedBuffer mapping = backend.Map(_zeros, MapType.Write, BufferRange.Whole);
            mapping.Bytes.Clear(); mapping.Flush(mapping.Range);
        }
        for (ulong offset = 0; offset < destination.Info.Size; offset += _zeros.Info.Size)
            backend.CopyBuffer(commands, new(_zeros, 0, destination, offset, Math.Min(_zeros.Info.Size, destination.Info.Size - offset)));
    }
    private sealed class Page(Buffer buffer, MappedBuffer mapping)
    {
        internal readonly Buffer Buffer = buffer;
        internal readonly MappedBuffer Mapping = mapping;
        internal ulong FlushOffset, FlushEnd;
        internal ulong WriteOffset = ulong.MaxValue;
        internal ulong WriteEnd;
    }
    public void Dispose()
    {
        foreach (var allocated in _allocatedPages) { allocated.Page.Mapping.Dispose(); allocated.Page.Buffer.Dispose(); }
        _zeros?.Dispose();
    }
}
