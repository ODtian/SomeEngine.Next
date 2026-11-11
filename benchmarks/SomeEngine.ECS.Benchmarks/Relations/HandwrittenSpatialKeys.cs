using SomeEngine.ECS;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Direct character-key exclusion over the same collision nodes and key summaries.</summary>
public static class HandwrittenSpatialKeys
{
    public static bool Except<TOutput>(World world, Query bodies, BoundsTree tree,
        ReadOnlySpan<BoundsKeyData<Entity>> keys, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope scope = bodies.WithOptionalRead<Body>().WithOptionalRead<Hair>().OpenRead();
        var execution = new KeyPairs<TOutput>(tree, keys, output);
        try { execution.Execute(scope); return execution.Completed; }
        finally { output = execution.Output; }
    }

    private ref struct KeyPairs<TOutput>(BoundsTree tree,
        ReadOnlySpan<BoundsKeyData<Entity>> keys, TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<BoundsKeyData<Entity>> _keys = keys;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(scoped QueryScope members) => Completed = Traverse(members);

        private bool Traverse(scoped QueryScope members)
        {
            if (tree.Root < 0)
                return true;
            ReadOnlySpan<BoundsNode> nodes = tree.Nodes;
            Span<KeyNodePair> work = stackalloc KeyNodePair[2 * tree.Height + 1];
            int count = 0;
            work[count++] = new(tree.Root, tree.Root, default, default);
            while (count > 0)
            {
                KeyNodePair pair = work[--count];
                ref readonly BoundsNode first = ref nodes[pair.First];
                ref readonly BoundsNode second = ref nodes[pair.Second];
                if (pair.First == pair.Second && first.IsLeaf ||
                    pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                    continue;
                GroupMatch forward = Groups(nodes, _keys, pair.First, pair.Second, pair.Forward);
                GroupMatch reverse = Groups(nodes, _keys, pair.Second, pair.First, pair.Reverse);
                if (forward == GroupMatch.All && reverse == GroupMatch.All)
                    continue;
                if (pair.First == pair.Second)
                {
                    work[count++] = new(first.SecondChild, first.SecondChild, forward, reverse);
                    work[count++] = new(first.FirstChild, first.SecondChild, forward, reverse);
                    work[count++] = new(first.FirstChild, first.FirstChild, forward, reverse);
                }
                else if (first.IsLeaf && second.IsLeaf)
                {
                    bool ascending = EntityOrder.Precedes(first.Entity, second.Entity);
                    int firstNode = ascending ? pair.First : pair.Second;
                    int secondNode = ascending ? pair.Second : pair.First;
                    GroupMatch match = ascending ? forward : reverse;
                    if (match == GroupMatch.All)
                        continue;
                    var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                    if (!members.Contains(entities.Item1) || !members.Contains(entities.Item2))
                        continue;
                    if (match == GroupMatch.Unknown)
                    {
                        ref readonly var firstKey = ref _keys[firstNode];
                        ref readonly var secondKey = ref _keys[secondNode];
                        if (firstKey.KeyCount != 0 && secondKey.KeyCount != 0 && firstKey.UniformKey == secondKey.UniformKey)
                            continue;
                    }
                    if (!Output.Add(in entities))
                        return false;
                }
                else if (second.IsLeaf || !first.IsLeaf && first.Height >= second.Height)
                {
                    work[count++] = new(first.SecondChild, pair.Second, forward, reverse);
                    work[count++] = new(first.FirstChild, pair.Second, forward, reverse);
                }
                else
                {
                    work[count++] = new(pair.First, second.SecondChild, forward, reverse);
                    work[count++] = new(pair.First, second.FirstChild, forward, reverse);
                }
            }
            return true;
        }
    }

    private static GroupMatch Groups(ReadOnlySpan<BoundsNode> nodes,
        ReadOnlySpan<BoundsKeyData<Entity>> keys, int firstNode, int secondNode, GroupMatch inherited)
    {
        if (inherited != GroupMatch.Unknown)
            return inherited;
        ref readonly var first = ref keys[firstNode];
        ref readonly var second = ref keys[secondNode];
        if (first.KeyCount == 0 || second.KeyCount == 0)
            return GroupMatch.None;
        if (first.HasUniformKey && second.HasUniformKey)
        {
            if (first.UniformKey != second.UniformKey)
                return GroupMatch.None;
            if (first.KeyCount == nodes[firstNode].MemberCount && second.KeyCount == nodes[secondNode].MemberCount)
                return GroupMatch.All;
        }
        return GroupMatch.Unknown;
    }

    private readonly struct KeyNodePair(int first, int second, GroupMatch forward, GroupMatch reverse)
    {
        public readonly int First = first;
        public readonly int Second = second;
        public readonly GroupMatch Forward = forward;
        public readonly GroupMatch Reverse = reverse;
    }
}
