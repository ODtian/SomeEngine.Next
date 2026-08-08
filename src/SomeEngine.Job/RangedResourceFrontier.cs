namespace SomeEngine.Job;

/// <summary>
/// Deterministic AVL interval frontier for one resource and one access-mode view. Nodes retain
/// subtree bounds so a range lookup visits only the search spine and overlapping entries. The
/// local free list keeps node storage attached to the pooled ResourceState across reuse.
/// </summary>
internal sealed class RangedResourceFrontier
{
    private Node? _root;
    private Node? _freeNodes;

    internal void Add(ResourceManager.ActiveResourceAccess access)
    {
        _root = Insert(_root, access);
    }

    internal bool Remove(ResourceManager.ActiveResourceAccess access)
    {
        bool removed = false;
        _root = Remove(_root, access, ref removed);
        return removed;
    }

    internal void Clear()
    {
        ReturnTree(_root);
        _root = null;
    }

    internal int AddDependencies(
        ResourceManager manager,
        JobResourceAccess candidate,
        JobHandle owner,
        ref AccessBuilder<ResourceDependency> dependencies,
        ref HashSet<ResourceManager.ResourceDependencyKey>? dependencySet)
    {
        if (!candidate.HasRange)
        {
            return AddAllDependencies(
                _root,
                manager,
                owner,
                ref dependencies,
                ref dependencySet);
        }

        long end = candidate.RangeStart + candidate.RangeLength;
        return AddOverlappingDependencies(
            _root,
            candidate.RangeStart,
            end,
            manager,
            owner,
            ref dependencies,
            ref dependencySet);
    }

    private Node Insert(Node? node, ResourceManager.ActiveResourceAccess access)
    {
        if (node is null)
            return RentNode(access);

        int comparison = Compare(access, node.Access);
        if (comparison < 0)
        {
            node.Left = Insert(node.Left, access);
        }
        else if (comparison > 0)
        {
            node.Right = Insert(node.Right, access);
        }
        else
        {
            node.Multiplicity = checked(node.Multiplicity + 1);
            return node;
        }

        return Balance(node);
    }

    private Node? Remove(
        Node? node,
        ResourceManager.ActiveResourceAccess access,
        ref bool removed)
    {
        if (node is null)
            return null;

        int comparison = Compare(access, node.Access);
        if (comparison < 0)
        {
            node.Left = Remove(node.Left, access, ref removed);
        }
        else if (comparison > 0)
        {
            node.Right = Remove(node.Right, access, ref removed);
        }
        else
        {
            removed = true;
            if (node.Multiplicity > 1)
            {
                node.Multiplicity--;
                return node;
            }

            if (node.Left is null || node.Right is null)
            {
                Node? replacement = node.Left ?? node.Right;
                ReturnNode(node);
                return replacement;
            }

            node.Right = RemoveMinimum(node.Right, out Node successor);
            node.Access = successor.Access;
            node.End = successor.End;
            node.Multiplicity = successor.Multiplicity;
            ReturnNode(successor);
        }

        return removed ? Balance(node) : node;
    }

    private Node? RemoveMinimum(Node node, out Node minimum)
    {
        if (node.Left is null)
        {
            minimum = node;
            return node.Right;
        }

        node.Left = RemoveMinimum(node.Left, out minimum);
        return Balance(node);
    }

    private static int AddOverlappingDependencies(
        Node? node,
        long start,
        long end,
        ResourceManager manager,
        JobHandle owner,
        ref AccessBuilder<ResourceDependency> dependencies,
        ref HashSet<ResourceManager.ResourceDependencyKey>? dependencySet)
    {
        if (node is null)
            return 0;

        int steps = 1;
        if (node.MaxEnd <= start || node.MinStart >= end)
            return steps;

        steps += AddOverlappingDependencies(
            node.Left,
            start,
            end,
            manager,
            owner,
            ref dependencies,
            ref dependencySet);

        long nodeStart = node.Access.Access.RangeStart;
        if (nodeStart < end && start < node.End)
        {
            manager.AddDependency(
                ref dependencies,
                ref dependencySet,
                node.Access,
                owner);
        }

        steps += AddOverlappingDependencies(
            node.Right,
            start,
            end,
            manager,
            owner,
            ref dependencies,
            ref dependencySet);
        return steps;
    }

    private static int AddAllDependencies(
        Node? node,
        ResourceManager manager,
        JobHandle owner,
        ref AccessBuilder<ResourceDependency> dependencies,
        ref HashSet<ResourceManager.ResourceDependencyKey>? dependencySet)
    {
        if (node is null)
            return 0;

        int steps = 1;
        steps += AddAllDependencies(
            node.Left,
            manager,
            owner,
            ref dependencies,
            ref dependencySet);
        manager.AddDependency(
            ref dependencies,
            ref dependencySet,
            node.Access,
            owner);
        steps += AddAllDependencies(
            node.Right,
            manager,
            owner,
            ref dependencies,
            ref dependencySet);
        return steps;
    }

    private Node RentNode(ResourceManager.ActiveResourceAccess access)
    {
        Node node;
        if (_freeNodes is null)
        {
            node = new Node();
        }
        else
        {
            node = _freeNodes;
            _freeNodes = node.FreeNext;
        }

        node.Access = access;
        node.End = access.Access.RangeStart + access.Access.RangeLength;
        node.MinStart = access.Access.RangeStart;
        node.MaxEnd = node.End;
        node.Height = 1;
        node.Multiplicity = 1;
        node.Left = null;
        node.Right = null;
        node.FreeNext = null;
        return node;
    }

    private void ReturnTree(Node? node)
    {
        if (node is null)
            return;

        ReturnTree(node.Left);
        ReturnTree(node.Right);
        ReturnNode(node);
    }

    private void ReturnNode(Node node)
    {
        node.Access = default;
        node.End = 0;
        node.MinStart = 0;
        node.MaxEnd = 0;
        node.Height = 0;
        node.Multiplicity = 0;
        node.Left = null;
        node.Right = null;
        node.FreeNext = _freeNodes;
        _freeNodes = node;
    }

    private static Node Balance(Node node)
    {
        Update(node);
        int balance = Height(node.Left) - Height(node.Right);
        if (balance > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node.Left = RotateLeft(node.Left);
            return RotateRight(node);
        }

        if (balance < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node.Right = RotateRight(node.Right);
            return RotateLeft(node);
        }

        return node;
    }

    private static Node RotateLeft(Node node)
    {
        Node replacement = node.Right!;
        node.Right = replacement.Left;
        replacement.Left = node;
        Update(node);
        Update(replacement);
        return replacement;
    }

    private static Node RotateRight(Node node)
    {
        Node replacement = node.Left!;
        node.Left = replacement.Right;
        replacement.Right = node;
        Update(node);
        Update(replacement);
        return replacement;
    }

    private static void Update(Node node)
    {
        node.Height = Math.Max(Height(node.Left), Height(node.Right)) + 1;
        node.MinStart = node.Access.Access.RangeStart;
        node.MaxEnd = node.End;
        if (node.Left is not null)
        {
            node.MinStart = Math.Min(node.MinStart, node.Left.MinStart);
            node.MaxEnd = Math.Max(node.MaxEnd, node.Left.MaxEnd);
        }

        if (node.Right is not null)
        {
            node.MinStart = Math.Min(node.MinStart, node.Right.MinStart);
            node.MaxEnd = Math.Max(node.MaxEnd, node.Right.MaxEnd);
        }
    }

    private static int Height(Node? node) => node?.Height ?? 0;

    private static int Compare(
        ResourceManager.ActiveResourceAccess left,
        ResourceManager.ActiveResourceAccess right)
    {
        int comparison = left.Access.RangeStart.CompareTo(right.Access.RangeStart);
        if (comparison != 0)
            return comparison;

        comparison = left.Access.RangeLength.CompareTo(right.Access.RangeLength);
        if (comparison != 0)
            return comparison;

        comparison = left.Access.Mode.CompareTo(right.Access.Mode);
        if (comparison != 0)
            return comparison;

        comparison = left.Owner.Generation.CompareTo(right.Owner.Generation);
        if (comparison != 0)
            return comparison;

        comparison = left.Owner.Index.CompareTo(right.Owner.Index);
        return comparison != 0
            ? comparison
            : left.Owner.Version.CompareTo(right.Owner.Version);
    }

    private sealed class Node
    {
        internal ResourceManager.ActiveResourceAccess Access;
        internal Node? Left;
        internal Node? Right;
        internal Node? FreeNext;
        internal long End;
        internal long MinStart;
        internal long MaxEnd;
        internal int Height;
        internal int Multiplicity;
    }
}
