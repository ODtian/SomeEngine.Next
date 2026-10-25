using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Encoded material bytes. Changes copy the pages containing those bytes.</summary>
internal sealed class ClusterBytes
{
    private const int PageSize = 4096;
    private readonly ImmutableDictionary<int, byte[]> _pages;

    private ClusterBytes(int length, ImmutableDictionary<int, byte[]> pages)
    {
        Length = length;
        _pages = pages;
    }

    internal int Length { get; }
    internal static ClusterBytes Zero(int length) => new(length, ImmutableDictionary<int, byte[]>.Empty);

    internal static ClusterBytes FromBytes(ReadOnlySpan<byte> bytes)
    {
        var pages = ImmutableDictionary.CreateBuilder<int, byte[]>();
        for (int offset = 0; offset < bytes.Length;)
        {
            int count = Math.Min(PageSize, bytes.Length - offset);
            pages.Add(offset / PageSize, bytes.Slice(offset, count).ToArray());
            offset += count;
        }
        return new(bytes.Length, pages.ToImmutable());
    }

    internal ClusterBytes WithChanges(int length, IReadOnlyList<(int Offset, byte[] Bytes)> changes)
    {
        if (length == Length && changes.Count == 0) return this;
        var changedPages = new Dictionary<int, byte[]>();
        foreach ((int first, byte[] bytes) in changes)
        {
            int offset = first;
            ReadOnlySpan<byte> source = bytes;
            while (!source.IsEmpty)
            {
                int pageIndex = offset / PageSize;
                int pageOffset = offset % PageSize;
                byte[] page = GetChangedPage(length, pageIndex, changedPages);
                int count = Math.Min(source.Length, page.Length - pageOffset);
                source[..count].CopyTo(page.AsSpan(pageOffset));
                source = source[count..];
                offset += count;
            }
        }
        return WithChangedPages(length, changedPages);
    }

    internal ClusterBytes WithUInt32Changes(int length, IReadOnlyList<(int Offset, uint Value)> changes)
    {
        if (length == Length && changes.Count == 0) return this;
        var changedPages = new Dictionary<int, byte[]>();
        foreach ((int offset, uint value) in changes)
        {
            // Index-table offsets and lengths are uint-aligned; a value never crosses a page.
            int pageIndex = offset / PageSize;
            int pageOffset = offset % PageSize;
            byte[] page = GetChangedPage(length, pageIndex, changedPages);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(pageOffset, sizeof(uint)), value);
        }
        return WithChangedPages(length, changedPages);
    }

    private byte[] GetChangedPage(int length, int pageIndex, Dictionary<int, byte[]> changedPages)
    {
        if (!changedPages.TryGetValue(pageIndex, out byte[]? page))
        {
            page = new byte[Math.Min(PageSize, length - pageIndex * PageSize)];
            if (_pages.TryGetValue(pageIndex, out byte[]? previous))
                previous.AsSpan(0, Math.Min(previous.Length, page.Length)).CopyTo(page);
            changedPages.Add(pageIndex, page);
        }
        return page;
    }

    private ClusterBytes WithChangedPages(int length, Dictionary<int, byte[]> changedPages)
    {
        var pages = _pages;
        foreach ((int index, byte[] page) in changedPages) pages = pages.SetItem(index, page);
        for (int page = PageCount(length); page < PageCount(Length); page++)
            pages = pages.Remove(page);
        return new(length, pages);
    }

    internal void CopyTo(Span<byte> destination, int sourceOffset = 0)
    {
        while (!destination.IsEmpty)
        {
            int pageIndex = sourceOffset / PageSize;
            int pageOffset = sourceOffset % PageSize;
            int count = Math.Min(destination.Length, PageSize - pageOffset);
            Span<byte> part = destination[..count];
            if (_pages.TryGetValue(pageIndex, out byte[]? page) && pageOffset < page.Length)
            {
                int copied = Math.Min(count, page.Length - pageOffset);
                page.AsSpan(pageOffset, copied).CopyTo(part);
                part[copied..].Clear();
            }
            else part.Clear();
            destination = destination[count..];
            sourceOffset += count;
        }
    }

    private static int PageCount(int length) => length == 0 ? 0 : (length - 1) / PageSize + 1;

}
