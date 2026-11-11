using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Spatial;

public sealed partial class BoundsTree
{
    public BoundsKeySummary<TComponent, TKey> CreateKeySummary<TComponent, TKey>()
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
        => new(this);
}

public readonly struct BoundsKeyData<TKey>(int keyCount, bool hasUniformKey, TKey uniformKey)
{
    public readonly int KeyCount = keyCount;
    public readonly bool HasUniformKey = hasUniformKey;
    public readonly TKey UniformKey = uniformKey;
}

public sealed class BoundsKeySummary<TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private BoundsKeyData<TKey>[] _nodes = [];

    internal BoundsKeySummary(BoundsTree tree) => Tree = tree;

    public BoundsTree Tree { get; }

    public ReadOnlySpan<BoundsKeyData<TKey>> Nodes => _nodes.AsSpan(0, Tree.NodeCount);

    public int NodeCapacity => _nodes.Length;

    public ref readonly BoundsKeyData<TKey> this[int node]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _nodes[node];
    }

    public void Rebuild(World world)
    {
        Resize();
        if (Tree.Root >= 0)
            Fill(Tree.Root, world);
    }

    private void Fill(int node, World world)
    {
        ref readonly BoundsNode value = ref Tree.Nodes[node];
        if (!value.IsLeaf)
        {
            Fill(value.FirstChild, world);
            Fill(value.SecondChild, world);
        }
        Refit(node, world);
    }

    public void Refit(int node, World world)
    {
        Resize();
        ref readonly BoundsNode value = ref Tree.Nodes[node];
        if (value.IsLeaf)
        {
            _nodes[node] = world.IsAlive(value.Entity) && world.Has<TComponent>(value.Entity)
                ? new BoundsKeyData<TKey>(1, true, world.Read<TComponent>(value.Entity).GetKey())
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
            _nodes[node] = new BoundsKeyData<TKey>(first.KeyCount + second.KeyCount,
                uniform, uniform ? first.UniformKey : default!);
        }
    }

    private void Resize()
    {
        if (_nodes.Length < Tree.NodeCount)
            Array.Resize(ref _nodes, Tree.NodeCount);
    }
}
