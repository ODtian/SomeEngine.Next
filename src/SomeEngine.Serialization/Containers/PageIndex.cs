namespace SomeEngine.Serialization.Containers;

// Nodes retain children directly. A shared ancestor is detached before descending, so a leaf
// whose direct reference count is one still cannot be modified through a shared ancestor.
internal sealed class PageIndex<T>
{
    private int _references = 1;
    internal Page<T>? Page;
    internal PageIndex<T>? Left, Right;
    internal readonly int Start;
    internal readonly long Length;
    internal readonly int Height;
    internal bool IsShared => Volatile.Read(ref _references) != 1;

    internal PageIndex(Page<T> page, int start, int length)
    { Page = page; Start = start; Length = length; Height = 1; }

    private PageIndex(PageIndex<T> left, PageIndex<T> right)
    {
        Left = left.Retain(); Right = right.Retain();
        Length = checked(left.Length + right.Length);
        Height = Math.Max(left.Height, right.Height) + 1;
    }

    internal PageIndex<T> Retain() { Interlocked.Increment(ref _references); return this; }
    internal void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        Page?.Release(); Left?.Release(); Right?.Release();
    }

    internal PageIndex<T> Copy() => Page is not null
        ? new(Page.Retain(), Start, (int)Length) : new(Left!, Right!);

    internal PageIndex<T> Find(long offset, out int start)
    {
        PageIndex<T> node = this;
        while (node.Page is null)
        {
            if (offset < node.Left!.Length) node = node.Left;
            else { offset -= node.Left.Length; node = node.Right!; }
        }
        start = (int)offset;
        return node;
    }

    // Returns one retained result and never mutates either input.
    internal static PageIndex<T>? Join(PageIndex<T>? left, PageIndex<T>? right)
    {
        if (left is null) return right?.Retain();
        if (right is null) return left.Retain();
        if (left.Height > right.Height + 1)
        {
            PageIndex<T> joined = Join(left.Right, right)!;
            try { return Balance(left.Left!, joined); }
            finally { joined.Release(); }
        }
        if (right.Height > left.Height + 1)
        {
            PageIndex<T> joined = Join(left, right.Left)!;
            try { return Balance(joined, right.Right!); }
            finally { joined.Release(); }
        }
        return new(left, right);
    }

    private static PageIndex<T> Balance(PageIndex<T> left, PageIndex<T> right)
    {
        if (left.Height > right.Height + 1)
        {
            if (left.Left!.Height >= left.Right!.Height)
            {
                var lower = new PageIndex<T>(left.Right, right);
                try { return new(left.Left, lower); }
                finally { lower.Release(); }
            }
            var first = new PageIndex<T>(left.Left, left.Right.Left!);
            var second = new PageIndex<T>(left.Right.Right!, right);
            try { return new(first, second); }
            finally { first.Release(); second.Release(); }
        }
        if (right.Height > left.Height + 1)
        {
            if (right.Right!.Height >= right.Left!.Height)
            {
                var lower = new PageIndex<T>(left, right.Left);
                try { return new(lower, right.Right); }
                finally { lower.Release(); }
            }
            var first = new PageIndex<T>(left, right.Left.Left!);
            var second = new PageIndex<T>(right.Left.Right!, right.Right);
            try { return new(first, second); }
            finally { first.Release(); second.Release(); }
        }
        return new(left, right);
    }

    internal static PageIndex<T>? Slice(PageIndex<T>? source, long offset, long length)
    {
        if (length == 0) return null;
        if (offset == 0 && length == source!.Length) return source.Retain();
        if (source!.Page is not null)
            return new(source.Page.Retain(), source.Start + (int)offset, (int)length);
        long leftLength = source.Left!.Length;
        if (offset >= leftLength) return Slice(source.Right, offset - leftLength, length);
        if (length <= leftLength - offset) return Slice(source.Left, offset, length);
        PageIndex<T>? left = Slice(source.Left, offset, leftLength - offset);
        PageIndex<T>? right = null;
        try
        {
            right = Slice(source.Right, 0, length - (leftLength - offset));
            return Join(left, right);
        }
        finally { left?.Release(); right?.Release(); }
    }
}

