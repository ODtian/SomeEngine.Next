using System.Buffers;

namespace SomeEngine.Serialization.Containers;

/// <summary>Named byte buffers with a shared balanced index and page-level copy on write.</summary>
public sealed class PageBufferMap : IDisposable
{
    private readonly PageArena _arena;
    private Node? _root;
    private bool _disposed;
    public PageAccess Access { get; }
    public PageArena Arena => _arena;

    public PageBufferMap(PageArena arena, PageAccess? access = null)
    {
        ArgumentNullException.ThrowIfNull(arena);
        _arena = arena;
        Access = access ?? new();
    }

    public PageBufferMap Duplicate(PageAccess? access = null)
    {
        lock (Access.SyncRoot)
        {
            RequireIdle();
            return new(_arena, access) { _root = _root?.Retain() };
        }
    }

    public bool Contains(ulong key)
    {
        lock (Access.SyncRoot) { RequireIdle(); return Find(key, out _) is not null; }
    }
    public long GetLength(ulong key)
    {
        lock (Access.SyncRoot) { RequireIdle(); return Require(key).Bytes.Length; }
    }
    public PageBuffer DuplicateBuffer(ulong key)
    {
        lock (Access.SyncRoot) { RequireIdle(); return Require(key).Bytes.Duplicate(); }
    }
    public PageRange<byte> Retain(ulong key, long offset = 0, long? length = null)
    {
        lock (Access.SyncRoot)
        {
            RequireIdle();
            PageBuffer bytes = Require(key).Bytes;
            return bytes.Retain(offset, length ?? bytes.Length - offset);
        }
    }
    public void Read(ulong key, long offset, Span<byte> destination)
    {
        lock (Access.SyncRoot) { RequireIdle(); Require(key).Bytes.Read(offset, destination); }
    }

    public void Set(ulong key, ReadOnlySpan<byte> bytes)
    {
        using var content = new PageBuffer(_arena, bytes.Length);
        content.Write(0, bytes);
        Set(key, content);
    }
    public void Set(ulong key, PageBuffer bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        // Make a private buffer access before entering the map's gate, avoiding lock inversion
        // with a caller's other content branch.
        using PageBuffer copy = ReferenceEquals(bytes.Arena, _arena) ? bytes.Duplicate() : Copy(bytes);
        lock (Access.SyncRoot)
        {
            RequireIdle();
            using var content = new BufferContent(copy.Duplicate());
            ReplaceRoot(Set(_root, key, content));
        }
    }
    private PageBuffer Copy(PageBuffer bytes)
    {
        using PageRange<byte> source = bytes.Retain();
        var result = new PageBuffer(_arena, source.Sequence.Length);
        try
        {
            long offset = 0;
            foreach (ReadOnlyMemory<byte> segment in source.Sequence)
            {
                result.Write(offset, segment.Span);
                offset += segment.Length;
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Write(ulong key, long offset, ReadOnlySpan<byte> bytes)
    {
        lock (Access.SyncRoot) { RequireIdle(); GetWritable(key).Write(offset, bytes); }
    }
    public void Write(ulong key, long offset, long length, PageWrite<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        PageBuffer bytes;
        lock (Access.SyncRoot)
        {
            RequireIdle();
            bytes = GetWritable(key);
            Access.BeginWrite();
        }
        try { bytes.Write(offset, length, writer); }
        finally { lock (Access.SyncRoot) Access.EndWrite(); }
    }
    public void Resize(ulong key, long length)
    {
        lock (Access.SyncRoot) { RequireIdle(); GetWritable(key).Resize(length); }
    }
    public bool Remove(ulong key)
    {
        lock (Access.SyncRoot)
        {
            RequireIdle();
            if (Find(key, out _) is null) return false;
            ReplaceRoot(Remove(_root!, key));
            return true;
        }
    }

    public IEnumerable<ulong> Keys
    {
        get
        {
            using PageBufferMap copy = Duplicate();
            var path = new Stack<Node>();
            Node? current = copy._root;
            while (current is not null || path.Count != 0)
            {
                while (current is not null) { path.Push(current); current = current.Left; }
                current = path.Pop();
                yield return current.Key;
                current = current.Right;
            }
        }
    }

    private BufferContent Require(ulong key)
        => Find(key, out _)?.Content ?? throw new KeyNotFoundException($"Buffer 0x{key:X16} was not found.");
    private Node? Find(ulong key, out bool shared)
    {
        shared = false;
        for (Node? node = _root; node is not null; node = key < node.Key ? node.Left : node.Right)
        {
            shared |= node.IsShared;
            if (key == node.Key) return node;
        }
        return null;
    }
    private PageBuffer GetWritable(ulong key)
    {
        Node node = Find(key, out bool shared) ?? throw new KeyNotFoundException($"Buffer 0x{key:X16} was not found.");
        if (!shared && !node.Content.IsShared) return node.Content.Bytes;
        using var content = new BufferContent(node.Content.Bytes.Duplicate());
        ReplaceRoot(Set(_root, key, content));
        return content.Bytes;
    }
    private void ReplaceRoot(Node? replacement)
    {
        Node? previous = _root;
        _root = replacement;
        previous?.Dispose();
    }
    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Access.RequireIdle();
    }
    public void Dispose()
    {
        lock (Access.SyncRoot)
        {
            if (_disposed) return;
            RequireIdle();
            _disposed = true;
            ReplaceRoot(null);
        }
    }

    private static Node Set(Node? node, ulong key, BufferContent content)
    {
        if (node is null) return new(key, content, null, null);
        if (key == node.Key) return new(key, content, node.Left, node.Right);
        using Node changed = Set(key < node.Key ? node.Left : node.Right, key, content);
        return Balance(key < node.Key
            ? new(node.Key, node.Content, changed, node.Right)
            : new(node.Key, node.Content, node.Left, changed));
    }
    private static Node? Remove(Node node, ulong key)
    {
        if (key == node.Key)
        {
            if (node.Left is null) return node.Right?.Retain();
            if (node.Right is null) return node.Left.Retain();
            Node next = node.Right;
            while (next.Left is not null) next = next.Left;
            using Node? right = Remove(node.Right, next.Key);
            return Balance(new(next.Key, next.Content, node.Left, right));
        }
        using Node? changed = Remove((key < node.Key ? node.Left : node.Right)!, key);
        return Balance(key < node.Key
            ? new(node.Key, node.Content, changed, node.Right)
            : new(node.Key, node.Content, node.Left, changed));
    }
    private static Node Balance(Node node)
    {
        int difference = Height(node.Left) - Height(node.Right);
        if (difference is >= -1 and <= 1) return node;
        using (node)
        {
            if (difference > 1)
            {
                Node left = node.Left!;
                if (Height(left.Left) >= Height(left.Right))
                {
                    using var right = new Node(node.Key, node.Content, left.Right, node.Right);
                    return new(left.Key, left.Content, left.Left, right);
                }
                Node middle = left.Right!;
                using var first = new Node(left.Key, left.Content, left.Left, middle.Left);
                using var last = new Node(node.Key, node.Content, middle.Right, node.Right);
                return new(middle.Key, middle.Content, first, last);
            }
            else
            {
                Node right = node.Right!;
                if (Height(right.Right) >= Height(right.Left))
                {
                    using var left = new Node(node.Key, node.Content, node.Left, right.Left);
                    return new(right.Key, right.Content, left, right.Right);
                }
                Node middle = right.Left!;
                using var first = new Node(node.Key, node.Content, node.Left, middle.Left);
                using var last = new Node(right.Key, right.Content, middle.Right, right.Right);
                return new(middle.Key, middle.Content, first, last);
            }
        }
    }
    private static int Height(Node? node) => node?.Height ?? 0;

    private sealed class Node : IDisposable
    {
        private int _references = 1;
        internal readonly ulong Key;
        internal readonly BufferContent Content;
        internal readonly Node? Left, Right;
        internal readonly int Height;
        internal bool IsShared => Volatile.Read(ref _references) != 1;
        internal Node(ulong key, BufferContent content, Node? left, Node? right)
        {
            Key = key; Content = content.Retain(); Left = left?.Retain(); Right = right?.Retain();
            Height = 1 + Math.Max(PageBufferMap.Height(left), PageBufferMap.Height(right));
        }
        internal Node Retain() { Interlocked.Increment(ref _references); return this; }
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            Content.Dispose(); Left?.Dispose(); Right?.Dispose();
        }
    }
    private sealed class BufferContent(PageBuffer bytes) : IDisposable
    {
        private int _references = 1;
        internal readonly PageBuffer Bytes = bytes;
        internal bool IsShared => Volatile.Read(ref _references) != 1;
        internal BufferContent Retain() { Interlocked.Increment(ref _references); return this; }
        public void Dispose() { if (Interlocked.Decrement(ref _references) == 0) Bytes.Dispose(); }
    }
}
