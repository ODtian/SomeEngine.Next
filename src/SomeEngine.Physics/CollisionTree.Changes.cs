using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

public sealed partial class CollisionTree
{
    public bool TryGetLeaf(Entity entity, out int leaf) => _leaves.TryGetValue(entity, out leaf);

    /// <summary>Inserts one member; refit the returned leaf and its ancestors with attached summaries.</summary>
    public int Insert(Entity entity, in CollisionBounds bounds)
    {
        int leaf = AllocateNode();
        _nodes[leaf] = new CollisionNode(bounds, entity, -1, -1, -1, 1, 0);
        _leaves.Add(entity, leaf);
        LeafCount++;
        if (Root < 0)
        {
            Root = leaf;
            return leaf;
        }

        int sibling = Root;
        while (!_nodes[sibling].IsLeaf)
        {
            ref readonly CollisionNode branch = ref _nodes[sibling];
            int first = branch.FirstChild;
            int second = branch.SecondChild;
            float firstGrowth = Growth(in _nodes[first].Bounds, in bounds);
            float secondGrowth = Growth(in _nodes[second].Bounds, in bounds);
            sibling = firstGrowth <= secondGrowth ? first : second;
        }

        int parent = _nodes[sibling].Parent;
        int joined = AllocateNode();
        _nodes[joined] = new CollisionNode(default, default, parent, sibling, leaf, 2, 1);
        _nodes[sibling] = _nodes[sibling].WithParent(joined);
        _nodes[leaf] = _nodes[leaf].WithParent(joined);
        if (parent < 0)
            Root = joined;
        else
            ReplaceChild(parent, sibling, joined);
        return leaf;
    }

    /// <summary>Removes one member and returns the lowest surviving node whose ancestors need refitting.</summary>
    public int Remove(Entity entity)
    {
        int leaf = _leaves[entity];
        _leaves.Remove(entity);
        LeafCount--;
        int parent = _nodes[leaf].Parent;
        if (parent < 0)
        {
            Root = -1;
            FreeNode(leaf);
            return -1;
        }

        int grandparent = _nodes[parent].Parent;
        int sibling = _nodes[parent].FirstChild == leaf ? _nodes[parent].SecondChild : _nodes[parent].FirstChild;
        _nodes[sibling] = _nodes[sibling].WithParent(grandparent);
        if (grandparent < 0)
            Root = sibling;
        else
            ReplaceChild(grandparent, parent, sibling);
        FreeNode(leaf);
        FreeNode(parent);
        return grandparent < 0 ? sibling : grandparent;
    }

    public void SetBounds(int leaf, in CollisionBounds bounds)
    {
        ref readonly CollisionNode previous = ref _nodes[leaf];
        _nodes[leaf] = new CollisionNode(bounds, previous.Entity, previous.Parent, -1, -1, 1, 0);
    }

    /// <summary>Merges this branch's already-updated children; a leaf keeps its current range.</summary>
    public void Refit(int node)
    {
        CollisionNode previous = _nodes[node];
        if (previous.IsLeaf)
            return;
        ref readonly CollisionNode first = ref _nodes[previous.FirstChild];
        ref readonly CollisionNode second = ref _nodes[previous.SecondChild];
        _nodes[node] = new CollisionNode(CollisionBounds.Union(in first.Bounds, in second.Bounds), default,
            previous.Parent, previous.FirstChild, previous.SecondChild, first.MemberCount + second.MemberCount,
            1 + Math.Max(first.Height, second.Height));
    }

    private int AllocateNode()
    {
        if (_freeNode >= 0)
        {
            int node = _freeNode;
            _freeNode = _nodes[node].Parent;
            return node;
        }
        EnsureCapacity(NodeCount + 1);
        return NodeCount++;
    }

    private void FreeNode(int node)
    {
        _nodes[node] = new CollisionNode(default, default, _freeNode, -1, -1, 0, 0);
        _freeNode = node;
    }

    private void ReplaceChild(int parent, int previous, int next)
    {
        CollisionNode branch = _nodes[parent];
        _nodes[parent] = new CollisionNode(branch.Bounds, default, branch.Parent,
            branch.FirstChild == previous ? next : branch.FirstChild,
            branch.SecondChild == previous ? next : branch.SecondChild, branch.MemberCount, branch.Height);
    }

    private static float Growth(in CollisionBounds current, in CollisionBounds addition)
    {
        CollisionBounds combined = CollisionBounds.Union(in current, in addition);
        return Area(in combined) - Area(in current);
    }

    private static float Area(in CollisionBounds bounds)
    {
        var size = bounds.Max - bounds.Min;
        return size.X * size.Y + size.X * size.Z + size.Y * size.Z;
    }
}
