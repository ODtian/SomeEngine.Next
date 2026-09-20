using System.Collections;
using System.Runtime.CompilerServices;

namespace SomeEngine.RenderGraph;

// Edits share a radix tree. A write copies only its directory path and data page;
// unrelated pages and branches remain shared with all earlier graph versions.
internal sealed class GraphTable<T> : IReadOnlyList<T>
{
    private const int PageShift = 6;
    private const int PageSize = 1 << PageShift;
    private const int BranchShift = 5;
    private const int BranchSize = 1 << BranchShift;
    private static readonly Branch Empty = new(new object());
    private object? _edit;
    private Branch _root;
    private int _depth;

    private sealed class Branch(object edit)
    {
        internal readonly object Edit = edit;
        internal readonly object?[] Children = new object?[BranchSize];
    }

    private sealed class Page(object edit, T[] values)
    {
        internal readonly object Edit = edit;
        internal readonly T[] Values = values;
    }

    internal GraphTable() => _root = Empty;

    private GraphTable(Branch root, int depth, int count)
    {
        _root = root;
        _depth = depth;
        Count = count;
    }

    public int Count { get; private set; }
    internal object BlockIdentity => _root;

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            DevelopmentCheck.Require((uint)index < (uint)Count, "A graph table row is out of range.");
            return ReadPage(index >> PageShift)[index & (PageSize - 1)];
        }
    }

    internal GraphTable<T> Fork()
    {
        // Neither side may write a shared branch or page after a fork.
        _edit = null;
        return new GraphTable<T>(_root, _depth, Count);
    }

    internal void Add(T value)
    {
        int index = Count;
        WritePage(index >> PageShift)[index & (PageSize - 1)] = value;
        Count++;
    }

    internal void Set(int index, T value)
    {
        DevelopmentCheck.Require((uint)index < (uint)Count, "A graph table row is out of range.");
        WritePage(index >> PageShift)[index & (PageSize - 1)] = value;
    }

    internal void RemoveLast()
    {
        DevelopmentCheck.Require(Count > 0, "An empty graph table cannot remove a row.");
        int index = Count - 1;
        WritePage(index >> PageShift)[index & (PageSize - 1)] = default!;
        Count = index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T[] ReadPage(int page)
    {
        Branch branch = _root;
        for (int shift = _depth * BranchShift; shift > 0; shift -= BranchShift)
            branch = (Branch)branch.Children[(page >> shift) & (BranchSize - 1)]!;
        return ((Page)branch.Children[page & (BranchSize - 1)]!).Values;
    }

    private static Branch WriteBranch(Branch source, object edit)
    {
        if (ReferenceEquals(source.Edit, edit)) return source;
        var copy = new Branch(edit);
        source.Children.CopyTo(copy.Children, 0);
        return copy;
    }

    private T[] WritePage(int page)
    {
        // Forking does not need a write identity. Create one only when this
        // version changes a page; the other versions retain different identities.
        object edit = _edit ??= new object();
        while ((long)page >= 1L << ((_depth + 1) * BranchShift))
        {
            var root = new Branch(edit);
            root.Children[0] = _root;
            _root = root;
            _depth++;
        }

        Branch branch = _root = WriteBranch(_root, edit);
        for (int shift = _depth * BranchShift; shift > 0; shift -= BranchShift)
        {
            int child = (page >> shift) & (BranchSize - 1);
            Branch next = branch.Children[child] is Branch previous
                ? WriteBranch(previous, edit) : new Branch(edit);
            branch.Children[child] = next;
            branch = next;
        }

        int position = page & (BranchSize - 1);
        if (branch.Children[position] is not Page data)
            branch.Children[position] = data = new Page(edit, new T[PageSize]);
        else if (!ReferenceEquals(data.Edit, edit))
            branch.Children[position] = data = new Page(edit, (T[])data.Values.Clone());
        return data.Values;
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    internal struct Enumerator(GraphTable<T> table) : IEnumerator<T>
    {
        private int _index = -1;
        public T Current => table[_index];
        object? IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < table.Count;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}
