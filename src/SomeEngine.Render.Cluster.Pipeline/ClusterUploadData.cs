using System.Collections.Immutable;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Immutable upload bytes. Local changes copy only the pages containing those bytes.</summary>
internal sealed class ClusterUploadData
{
    private const int PageSize = 4096;
    private readonly ImmutableDictionary<int, byte[]> _pages;

    private ClusterUploadData(int length, ImmutableDictionary<int, byte[]> pages)
    {
        Length = length;
        _pages = pages;
    }

    internal int Length { get; }
    internal static ClusterUploadData Zero(int length) => new(length, ImmutableDictionary<int, byte[]>.Empty);

    internal static ClusterUploadData FromBytes(ReadOnlySpan<byte> bytes)
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

    internal ClusterUploadData WithChanges(int length, IReadOnlyList<(int Offset, byte[] Bytes)> changes)
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
                if (!changedPages.TryGetValue(pageIndex, out byte[]? page))
                {
                    page = new byte[Math.Min(PageSize, length - pageIndex * PageSize)];
                    if (_pages.TryGetValue(pageIndex, out byte[]? previous))
                        previous.AsSpan(0, Math.Min(previous.Length, page.Length)).CopyTo(page);
                    changedPages.Add(pageIndex, page);
                }
                int count = Math.Min(source.Length, page.Length - pageOffset);
                source[..count].CopyTo(page.AsSpan(pageOffset));
                source = source[count..];
                offset += count;
            }
        }
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
            part.Clear();
            if (_pages.TryGetValue(pageIndex, out byte[]? page) && pageOffset < page.Length)
                page.AsSpan(pageOffset, Math.Min(count, page.Length - pageOffset)).CopyTo(part);
            destination = destination[count..];
            sourceOffset += count;
        }
    }

    private static int PageCount(int length) => length == 0 ? 0 : (length - 1) / PageSize + 1;

    internal bool ContentEquals(ReadOnlySpan<byte> other)
    {
        if (other.Length != Length) return false;
        Span<byte> page = stackalloc byte[Math.Min(PageSize, Length)];
        for (int offset = 0; offset < Length;)
        {
            Span<byte> part = page[..Math.Min(PageSize, Length - offset)];
            CopyTo(part, offset);
            if (!part.SequenceEqual(other.Slice(offset, part.Length))) return false;
            offset += part.Length;
        }
        return true;
    }
}
