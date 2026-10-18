using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public sealed partial class CollisionTree
{
    public CollisionKeySummary<TComponent, TKey> CreateKeySummary<TComponent, TKey>()
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
        => new(this);
}

public readonly struct CollisionKeyData<TKey>(int keyCount, bool hasUniformKey, TKey uniformKey)
{
    public readonly int KeyCount = keyCount;
    public readonly bool HasUniformKey = hasUniformKey;
    public readonly TKey UniformKey = uniformKey;
}

public sealed class CollisionKeySummary<TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private CollisionKeyData<TKey>[] _nodes = [];

    internal CollisionKeySummary(CollisionTree tree) => Tree = tree;

    public CollisionTree Tree { get; }

    public ReadOnlySpan<CollisionKeyData<TKey>> Nodes => _nodes.AsSpan(0, Tree.NodeCount);

    public void Rebuild(QueryCursor access)
    {
        Resize();
        if (Tree.Root >= 0)
            Fill(Tree.Root, access);
    }

    private void Fill(int node, QueryCursor access)
    {
        ref readonly CollisionNode value = ref Tree.Nodes[node];
        if (!value.IsLeaf)
        {
            Fill(value.FirstChild, access);
            Fill(value.SecondChild, access);
        }
        Refit(node, access);
    }

    public void Refit(int node, QueryCursor access)
    {
        Resize();
        ref readonly CollisionNode value = ref Tree.Nodes[node];
        if (value.IsLeaf)
        {
            _nodes[node] = access.TryGetRow(value.Entity, out var row) && row.Has<TComponent>()
                ? new CollisionKeyData<TKey>(1, true, row.ReadRef<TComponent>().GetKey())
                : default;
            return;
        }
        ref readonly var first = ref _nodes[value.FirstChild];
        ref readonly var second = ref _nodes[value.SecondChild];
        if (first.KeyCount == 0)
            _nodes[node] = second;
        else if (second.KeyCount == 0)
            _nodes[node] = first;
        else
        {
            bool uniform = first.HasUniformKey && second.HasUniformKey && first.UniformKey.Equals(second.UniformKey);
            _nodes[node] = new CollisionKeyData<TKey>(first.KeyCount + second.KeyCount,
                uniform, uniform ? first.UniformKey : default!);
        }
    }

    private void Resize()
    {
        if (_nodes.Length < Tree.NodeCount)
            Array.Resize(ref _nodes, Tree.NodeCount);
    }
}
